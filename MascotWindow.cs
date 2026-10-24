namespace ClaudeUsageTray;

/// <summary>
/// La mascota sobre el escritorio (el chibi o la clásica), sin marco ni fondo, siempre encima y sin
/// quitar el foco. Hace lo que diga ChibiDirector; se arrastra para cambiarla de lugar (sin salirse
/// de la pantalla), un clic la hace saludar (y muchos, enojarse) y el clic derecho abre su menú.
/// </summary>
sealed class MascotWindow : Form
{
    static readonly Color Key = Color.FromArgb(255, 0, 255); // el fondo: Windows lo deja transparente

    public event Action? OpenRequested;
    public event Action? HideRequested;
    /// <summary>Se soltó en otro lugar (esquina de arriba a la izquierda, en pixeles de pantalla).</summary>
    public event Action<Point>? Moved;
    /// <summary>Un clic encima (después de que ChibiDirector decidió el gesto).</summary>
    public event Action? Poked;
    /// <summary>Le soltaron archivos encima.</summary>
    public event Action<string[]>? FilesDropped;
    /// <summary>Un archivo pasa por encima (true) o se va sin soltarlo (false).</summary>
    public event Action<bool>? FileHover;

    /// <summary>Mientras lleva un archivo (camina, lanza): no se arrastra ni se le sueltan otros.</summary>
    public bool Busy { get; set; }

    readonly ChibiDirector _director;
    readonly Func<Color> _accent;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    readonly ContextMenuStrip _menu = new();
    int _unit;
    (ChibiMove Move, int Frame, long Beat) _shown = (ChibiMove.Sleep, -1, -1);
    Point? _pressAt;
    Point _startLocation;
    bool _dragging;

    /// <summary>El chibi (true) o la clásica, que se pinta con el acento de la paleta.</summary>
    public bool Chibi { get; }

    public MascotWindow(ChibiDirector director, Point? location, bool chibi, Func<Color> accent, string size)
    {
        _director = director;
        _accent = accent;
        Chibi = chibi;
        _unit = MascotSizes.Unit(size, chibi, DeviceDpi / 96f);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Key;
        TransparencyKey = Key;
        ClientSize = chibi
            ? new Size(ChibiSprite.Columns * _unit, ChibiSprite.Rows * _unit)
            : new Size(ToastSprite.Columns * _unit, ToastSprite.Rows * _unit);
        Location = Fit(location ?? DefaultLocation());
        Cursor = Cursors.Hand;
        AllowDrop = true;

        _menu.Items.Add("Abrir Uso de Claude", null, (_, _) => OpenRequested?.Invoke());
        // Después del clic: ocultar cierra esta ventana, y con ella el menú que está avisando.
        _menu.Items.Add("Ocultar del escritorio", null, (_, _) => BeginInvoke(() => HideRequested?.Invoke()));
        Menus.Apply(_menu, Theme.Current);

        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Pixeles por casilla (el tamaño elegido en Ajustes, con la escala de la pantalla).</summary>
    public int Unit => _unit;

    /// <summary>
    /// Otro tamaño: crece o se achica desde los pies, en el mismo lugar, y da un saltito para que se
    /// note el cambio.
    /// </summary>
    public void SetSize(int unit)
    {
        if (unit == _unit) return;
        var feet = new Point(Left + Width / 2, Bottom);
        _unit = unit;
        ClientSize = Chibi
            ? new Size(ChibiSprite.Columns * _unit, ChibiSprite.Rows * _unit)
            : new Size(ToastSprite.Columns * _unit, ToastSprite.Rows * _unit);
        Location = Fit(new Point(feet.X - Width / 2, feet.Y - Height));
        _shown = (ChibiMove.Sleep, -1, -1);
        _director.Gesture(ChibiMove.Cheer, 700);
        Invalidate();
        Moved?.Invoke(Location);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Sin WS_EX_TOPMOST al crearla (lo pone TopMost después): con él, Windows no convierte la
            // posición al DPI de una segunda pantalla con otra escala y la ventana nace fuera de ella.
            cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    /// <summary>La posición más cercana con la ventana entera dentro de la pantalla en que cae: nunca se pierde por un borde.</summary>
    Point Fit(Point at)
    {
        var area = Screen.FromRectangle(new Rectangle(at, Size)).WorkingArea;
        return new Point(Math.Clamp(at.X, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(at.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    /// <summary>Parado sobre la barra de tareas, a la izquierda de donde salen los avisos.</summary>
    Point DefaultLocation()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Point.Empty);
        return new Point(area.Right - LogicalToDeviceUnits(344 + 24) - Width, area.Bottom - Height);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _director.Appear(); // saluda al aparecer
        _timer.Start();
    }

    /// <summary>Repinta solo cuando cambia el cuadro o, con música, el tiempo del compás (la clásica se anima con el tiempo: cada vuelta del reloj).</summary>
    void Tick()
    {
        var (move, t, music) = _director.Now();
        var shown = (move, Chibi ? ChibiSprite.FrameIndex(move, t) : (int)(t / _timer.Interval), music ? t / ChibiSprite.Beat : -1);
        if (shown == _shown) return;
        _shown = shown;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Key);
        var (move, t, music) = _director.Now();
        if (Chibi) ToastArt.Pixels(e.Graphics, 0, 0, _unit, fill => ChibiSprite.Paint(fill, move, t, music));
        else ToastArt.Pixels(e.Graphics, 0, 0, _unit, fill => ToastSprite.PaintMove(fill, move, t, _accent(), music));
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (Busy || e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return;
        e.Effect = DragDropEffects.Copy;
        FileHover?.Invoke(true);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        FileHover?.Invoke(false);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (!Busy && e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) FilesDropped?.Invoke(files);
        else FileHover?.Invoke(false);
    }

    /// <summary>Camina hasta x (a su misma altura) en ms milisegundos, con un arranque y una llegada suaves.</summary>
    public async Task WalkToAsync(int x, int ms)
    {
        var from = Left;
        var start = Environment.TickCount64;
        while (true)
        {
            var k = Math.Clamp((Environment.TickCount64 - start) / (double)Math.Max(1, ms), 0, 1);
            var ease = k < 0.1 ? k * k * 5 : k > 0.9 ? 1 - (1 - k) * (1 - k) * 5 : k - 0.05; // casi constante, sin tirones
            Left = (int)Math.Round(from + (x - from) * Math.Clamp(ease, 0, 1));
            if (k >= 1) break;
            await Task.Delay(15);
        }
        Left = x;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || Busy) return;
        _pressAt = Cursor.Position;
        _startLocation = Location;
        _dragging = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressAt is not { } press) return;
        var dx = Cursor.Position.X - press.X;
        var dy = Cursor.Position.Y - press.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < LogicalToDeviceUnits(4)) return;
        _dragging = true;
        Location = Fit(new Point(_startLocation.X + dx, _startLocation.Y + dy));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right) { _menu.Show(Cursor.Position); return; }
        if (_pressAt is null) return;
        _pressAt = null;
        if (_dragging) Moved?.Invoke(Location);
        else
        {
            _director.Poke(); // saluda; si lo siguen molestando, se enoja
            Poked?.Invoke();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
