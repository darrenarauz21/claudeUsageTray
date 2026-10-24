using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// Transiciones suaves de la ventana. Antes de cambiar de vista se guarda el cuadro de ese momento;
/// durante 200 ms se pinta el cuadro nuevo encima del viejo: la cabecera y el pie se funden en su
/// lugar y el contenido entra deslizándose desde el lado que corresponde, mientras el alto de la
/// ventana pasa del viejo al nuevo (pegada a la bandeja: crece hacia arriba). Los interruptores y el
/// indicador de las pestañas de Ajustes se deslizan solos. Si Windows tiene apagados los efectos de
/// animación, todo cambia de golpe, como antes.
/// </summary>
sealed partial class UsageForm
{
    /// <summary>Hacia dónde entra el contenido nuevo.</summary>
    enum Slide { Fade, FromRight, FromLeft, Up, Down }

    const int TransitionMs = 200, SwitchMs = 140, IndicatorMs = 180;

    readonly System.Windows.Forms.Timer _motion = new() { Interval = 15 };
    readonly Dictionary<string, (bool On, long At)> _switches = new();
    Bitmap? _fromFrame;
    int _fromHeader, _fromFooter, _fromHeight, _toHeight;
    Slide _slide;
    long _transitionAt;
    /// <summary>Dónde termina la cabecera y empieza el pie en el último recorrido (para la transición).</summary>
    int _headerBottom, _footerTop;

    static bool AnimationsEnabled
    {
        get
        {
            try { return !SystemParametersInfo(0x1042, 0, out var on, 0) || on; } // SPI_GETCLIENTAREAANIMATION
            catch (Exception) { return true; }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(int action, int param, out bool value, int winIni);

    bool InTransition => _fromFrame is not null;

    double TransitionProgress => Math.Clamp((Environment.TickCount64 - _transitionAt) / (double)TransitionMs, 0, 1);

    static double Smooth(double p) => 1 - Math.Pow(1 - p, 3); // salida suave, sin rebote

    /// <summary>Guarda el cuadro de ahora: lo que cambie después entra con una transición.</summary>
    void BeginTransition(Slide slide)
    {
        if (!Visible || !IsHandleCreated || !AnimationsEnabled || ClientSize.Height <= 0) return;
        var from = RenderFrame(ClientSize.Height, out var header, out var footer);
        _fromFrame?.Dispose();
        _fromFrame = from;
        _fromHeader = header;
        _fromFooter = ClientSize.Height - footer;
        _fromHeight = _toHeight = ClientSize.Height;
        _slide = slide;
        _transitionAt = Environment.TickCount64;
        StartMotion();
    }

    /// <summary>Hacia qué lado entra una vista: según el orden de las pestañas; Ajustes sube y al cerrarlo baja.</summary>
    Slide SlideFor(View from, View to)
    {
        if (to == View.Settings) return Slide.Up;
        if (from == View.Settings) return Slide.Down;
        int Order(View v) => _pinned.IndexOf(v) is var i && i >= 0 ? i : 3;
        return Order(to) >= Order(from) ? Slide.FromRight : Slide.FromLeft;
    }

    void StartMotion()
    {
        if (!_motion.Enabled) _motion.Start();
    }

    void MotionTick()
    {
        var now = Environment.TickCount64;
        if (_fromFrame is not null)
        {
            var p = TransitionProgress;
            var height = (int)Math.Round(_fromHeight + (_toHeight - _fromHeight) * Smooth(p));
            if (height != ClientSize.Height) ResizeKeepingBottom(height);
            if (p >= 1)
            {
                _fromFrame.Dispose();
                _fromFrame = null;
                if (ClientSize.Height != _toHeight) ResizeKeepingBottom(_toHeight);
            }
        }
        Invalidate();
        var moving = _fromFrame is not null || now - _tabAt < IndicatorMs || _switches.Values.Any(s => now - s.At < SwitchMs);
        if (!moving) _motion.Stop();
    }

    /// <summary>
    /// Otro alto con el borde de abajo quieto, en un solo movimiento, y repintado enseguida: cambiar
    /// alto y lugar por separado, o esperar al próximo pintado, se ve a saltos.
    /// </summary>
    void ResizeKeepingBottom(int height)
    {
        var outer = height + (Height - ClientSize.Height);
        if (!Visible) { ClientSize = new Size(ClientSize.Width, height); return; }
        _resizing = true;
        try { Bounds = new Rectangle(Left, Bottom - outer, Width, outer); }
        finally { _resizing = false; }
        Invalidate();
        Update();
    }

    bool _resizing;

    /// <summary>
    /// Mientras la transición cambia el alto, Windows no copia el contenido viejo al nuevo lugar
    /// (SWP_NOCOPYBITS): si lo hiciera, por un instante se vería corrido hacia arriba.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        const int WmWindowPosChanging = 0x0046, NoCopyBits = 0x0100;
        if (m.Msg == WmWindowPosChanging && _resizing)
        {
            var pos = Marshal.PtrToStructure<WindowPos>(m.LParam);
            pos.Flags |= NoCopyBits;
            Marshal.StructureToPtr(pos, m.LParam, false);
        }
        base.WndProc(ref m);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowPos
    {
        public IntPtr Hwnd, InsertAfter;
        public int X, Y, Cx, Cy;
        public int Flags;
    }

    /// <summary>El cuadro completo con el estado actual, en un mapa de bits; también dice dónde terminan la cabecera y empieza el pie.</summary>
    Bitmap RenderFrame(int height, out int header, out int footer)
    {
        var bmp = new Bitmap(ClientSize.Width, Math.Max(1, height), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Compose(g, draw: true);
        }
        header = _headerBottom;
        footer = _footerTop;
        return bmp;
    }

    /// <summary>Pinta la transición: el cuadro viejo se va y el nuevo entra, por regiones.</summary>
    void PaintTransition(Graphics g, Bitmap from)
    {
        using var to = RenderFrame(Math.Max(_toHeight, 1), out var toHeader, out var toFooterTop);
        var toFooter = _toHeight - toFooterTop;
        var k = (float)Smooth(TransitionProgress);
        var width = ClientSize.Width;
        var height = ClientSize.Height;
        g.Clear(_t.Background);

        // Cabecera y pie: se funden en su lugar (el pie, pegado al borde de abajo).
        Blend(g, from, new Rectangle(0, 0, width, _fromHeader), 0, 0, 1 - k);
        Blend(g, to, new Rectangle(0, 0, width, toHeader), 0, 0, k);
        Blend(g, from, new Rectangle(0, _fromHeight - _fromFooter, width, _fromFooter), 0, height - _fromFooter, 1 - k);
        Blend(g, to, new Rectangle(0, _toHeight - toFooter, width, toFooter), 0, height - toFooter, k);

        // Contenido: el viejo se aparta un poco y se desvanece; el nuevo entra desde su lado.
        var top = (int)(_fromHeader + (toHeader - _fromHeader) * k);
        var bottom = height - (int)(_fromFooter + (toFooter - _fromFooter) * k);
        if (bottom <= top) return;
        var state = g.Save();
        g.SetClip(new Rectangle(0, top, width, bottom - top));
        var (inX, inY, outX, outY) = _slide switch
        {
            Slide.FromRight => (S(14), 0, -S(8), 0),
            Slide.FromLeft => (-S(14), 0, S(8), 0),
            Slide.Up => (0, S(16), 0, -S(6)),
            Slide.Down => (0, -S(10), 0, S(6)),
            _ => (0, 0, 0, 0),
        };
        Blend(g, from, new Rectangle(0, _fromHeader, width, _fromHeight - _fromFooter - _fromHeader),
            (int)(outX * k), _fromHeader + (int)(outY * k), 1 - k);
        Blend(g, to, new Rectangle(0, toHeader, width, _toHeight - toFooter - toHeader),
            (int)(inX * (1 - k)), toHeader + (int)(inY * (1 - k)), k);
        g.Restore(state);
    }

    static void Blend(Graphics g, Bitmap source, Rectangle part, int x, int y, float alpha)
    {
        if (part.Width <= 0 || part.Height <= 0 || alpha <= 0.01f) return;
        if (alpha >= 0.99f)
        {
            g.DrawImage(source, new Rectangle(x, y, part.Width, part.Height), part, GraphicsUnit.Pixel);
            return;
        }
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
        g.DrawImage(source, new Rectangle(x, y, part.Width, part.Height), part.X, part.Y, part.Width, part.Height, GraphicsUnit.Pixel, attributes);
    }

    /// <summary>Dónde está la perilla de un interruptor (0 apagado, 1 encendido): se desliza al cambiar.</summary>
    float SwitchPosition(string key, bool on)
    {
        var now = Environment.TickCount64;
        if (!_switches.TryGetValue(key, out var s))
        {
            _switches[key] = (on, 0);
            return on ? 1 : 0;
        }
        if (s.On != on)
        {
            _switches[key] = s = (on, AnimationsEnabled ? now : 0);
            StartMotion();
        }
        var k = (float)Smooth(Math.Clamp((now - s.At) / (double)SwitchMs, 0, 1));
        return on ? k : 1 - k;
    }

    static Color Mix(Color from, Color to, float k) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * k), (int)(from.G + (to.G - from.G) * k), (int)(from.B + (to.B - from.B) * k));
}
