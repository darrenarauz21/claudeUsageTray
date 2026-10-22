using System.Text;
using System.Text.Json;

namespace ClaudeUsageTray;

/// <summary>Aviso que entra desde la derecha, abajo, sin quitarle el foco a lo que esté usando.</summary>
sealed class ToastForm : Form
{
    const int Lifetime = 6000, EnterMs = 260, ExitMs = 200;
    const TextFormatFlags Line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

    public event Action<ToastForm>? Clicked;
    public event Action<ToastForm>? Dismissed;
    public Notice Notice { get; }

    readonly Theme _t = Theme.Current;
    readonly float _scale;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    readonly Font _title, _subtitle, _detail, _glyph;
    readonly long _born = Environment.TickCount64;
    long _last = Environment.TickCount64;
    long _remaining = Lifetime;
    long? _closingSince;
    bool _hover, _hoverClose;
    Point _target;

    int S(int v) => (int)Math.Round(v * _scale);

    public ToastForm(Notice notice)
    {
        Notice = notice;
        _scale = DeviceDpi / 96f;
        _title = Fonts.Make("Segoe UI Variable Text Semibold", 10f, "Segoe UI Semibold");
        _subtitle = Fonts.Make("Segoe UI Variable Text", 9f, "Segoe UI");
        _detail = Fonts.Make("Segoe UI Variable Text", 8.5f, "Segoe UI");
        _glyph = Fonts.Make("Segoe Fluent Icons", 8f, "Segoe MDL2 Assets");

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        ClientSize = new Size(S(344), S(88));
        Cursor = Cursors.Hand;

        _timer.Tick += (_, _) => Tick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x8; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
            cp.ClassStyle |= 0x20000;              // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dwm.Apply(Handle, _t);
    }

    /// <summary>Entra desde fuera de la pantalla hasta su lugar.</summary>
    public void Present(Point target)
    {
        _target = target;
        Location = new Point(Screen.FromPoint(target).WorkingArea.Right + S(8), target.Y);
        Show();
        Refresh(); // pintado antes de entrar, para que no se vea vacío
        _timer.Start();
    }

    /// <summary>Se mueve a otro lugar de la pila (cuando llega o se va otro aviso).</summary>
    public void MoveTo(Point target) => _target = target;

    public void Dismiss()
    {
        _closingSince ??= Environment.TickCount64;
    }

    void Tick()
    {
        var now = Environment.TickCount64;
        var dt = now - _last;
        _last = now;

        if (_closingSince is { } since)
        {
            var k = Math.Min(1.0, (now - since) / (double)ExitMs);
            var right = Screen.FromPoint(_target).WorkingArea.Right + S(8);
            Location = new Point((int)(_target.X + (right - _target.X) * k * k), Location.Y);
            if (k >= 1)
            {
                _timer.Stop();
                Dismissed?.Invoke(this);
                Close();
                return;
            }
        }
        else
        {
            // Acercarse al lugar de destino con suavidad.
            var x = Location.X + (int)Math.Round((_target.X - Location.X) * Math.Min(1.0, dt / (double)EnterMs * 3));
            var y = Location.Y + (int)Math.Round((_target.Y - Location.Y) * Math.Min(1.0, dt / 120.0));
            if (Math.Abs(_target.X - x) <= 1) x = _target.X;
            if (Math.Abs(_target.Y - y) <= 1) y = _target.Y;
            if (x != Location.X || y != Location.Y) Location = new Point(x, y);

            if (!Notice.Sticky && !_hover)
            {
                _remaining -= dt;
                if (_remaining <= 0) Dismiss();
            }
        }
        Invalidate();
    }

    Rectangle CloseBounds => new(ClientSize.Width - S(30), S(6), S(24), S(24));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_t.Raised);
        var t = Environment.TickCount64 - _born;

        var art = new Rectangle(S(12), (ClientSize.Height - S(64)) / 2, S(78), S(64));
        if (Mascots.IsChibi) // asomado desde abajo, en todo el alto del aviso
            ToastArt.DrawChibi(g, new Rectangle(S(8), 0, S(86), ClientSize.Height), ChibiSprite.ForNotice(Notice.Kind), t);
        else
            ToastArt.Draw(g, art, Notice.Kind, t, _t.Accent);

        var x = art.Right + S(12);
        var width = ClientSize.Width - x - S(34);
        var titleColor = Notice.Kind switch
        {
            NoticeKind.Failed or NoticeKind.UsageCritical or NoticeKind.Closed => _t.Danger,
            NoticeKind.Question or NoticeKind.Permission or NoticeKind.UsageHigh or NoticeKind.Expired or NoticeKind.Retrying => _t.Warn,
            _ => _t.Success,
        };
        var y = S(16);
        TextRenderer.DrawText(g, Notice.Title, _title, new Rectangle(x, y, width, _title.Height), titleColor, Line);
        y += _title.Height + S(3);
        TextRenderer.DrawText(g, Notice.Subtitle, _subtitle, new Rectangle(x, y, width, _subtitle.Height), _t.Text, Line);
        y += _subtitle.Height + S(3);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(g, Notice.Detail, _detail, new Rectangle(x, y, width + S(20), _detail.Height * 2), _t.Tertiary, flags);

        if (_hover)
        {
            if (_hoverClose) Draw.Fill(g, _t.Hover, CloseBounds, S(6));
            TextRenderer.DrawText(g, "", _glyph, CloseBounds, _t.Secondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // Lo que le queda de vida, como una línea que se acorta.
        if (!Notice.Sticky && _closingSince is null)
        {
            var w = (int)(ClientSize.Width * Math.Clamp(_remaining / (double)Lifetime, 0, 1));
            using var brush = new SolidBrush(_t.Accent);
            g.FillRectangle(brush, 0, ClientSize.Height - S(2), w, S(2));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var onClose = CloseBounds.Contains(e.Location);
        if (_hover && onClose == _hoverClose) return;
        _hover = true;
        _hoverClose = onClose;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = _hoverClose = false;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && !CloseBounds.Contains(e.Location)) Clicked?.Invoke(this);
        Dismiss();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _title.Dispose(); _subtitle.Dispose(); _detail.Dispose(); _glyph.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Apila los avisos abajo a la derecha, el más nuevo abajo; como mucho cuatro a la vez.</summary>
sealed class ToastManager
{
    const int MaxVisible = 4;

    public event Action<Notice>? Clicked;
    public event Action? Shown;

    readonly List<ToastForm> _toasts = new();

    public void Show(Notice notice)
    {
        var toast = new ToastForm(notice);
        toast.Clicked += t => Clicked?.Invoke(t.Notice);
        toast.Dismissed += t =>
        {
            _toasts.Remove(t);
            Layout();
        };
        _toasts.Insert(0, toast);
        foreach (var old in _toasts.Skip(MaxVisible)) old.Dismiss();

        var targets = Targets();
        toast.Present(targets[0]);
        Layout();
        Shown?.Invoke();
    }

    public void CloseAll()
    {
        foreach (var toast in _toasts.ToList()) toast.Close();
        _toasts.Clear();
    }

    List<Point> Targets()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Point.Empty);
        var points = new List<Point>();
        var y = area.Bottom;
        foreach (var toast in _toasts)
        {
            var gap = toast.LogicalToDeviceUnits(12);
            y -= toast.Height + gap;
            points.Add(new Point(area.Right - toast.Width - gap, y));
            y += gap - toast.LogicalToDeviceUnits(8);
        }
        return points;
    }

    void Layout()
    {
        var targets = Targets();
        for (var i = 0; i < _toasts.Count; i++) _toasts[i].MoveTo(targets[i]);
    }
}
