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

    readonly ChibiDirector _director;
    readonly Func<Color> _accent;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    readonly ContextMenuStrip _menu = new();
    readonly int _unit;
    (ChibiMove Move, int Frame) _shown = (ChibiMove.Sleep, -1);
    Point? _pressAt;
    Point _startLocation;
    bool _dragging;

    /// <summary>El chibi (true) o la clásica, que se pinta con el acento de la paleta.</summary>
    public bool Chibi { get; }

    public MascotWindow(ChibiDirector director, Point? location, bool chibi, Func<Color> accent)
    {
        _director = director;
        _accent = accent;
        Chibi = chibi;
        _unit = Math.Max(1, (int)Math.Round((chibi ? 3 : 4) * DeviceDpi / 96f));
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

        _menu.Items.Add("Abrir Uso de Claude", null, (_, _) => OpenRequested?.Invoke());
        // Después del clic: ocultar cierra esta ventana, y con ella el menú que está avisando.
        _menu.Items.Add("Ocultar del escritorio", null, (_, _) => BeginInvoke(() => HideRequested?.Invoke()));
        Menus.Apply(_menu, Theme.Current);

        _timer.Tick += (_, _) => Tick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x8; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
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
        _director.Gesture(ChibiMove.Wave); // saluda al aparecer
        _timer.Start();
    }

    /// <summary>Repinta solo cuando cambia el cuadro (la clásica se anima con el tiempo: cada vuelta del reloj).</summary>
    void Tick()
    {
        var (move, t) = _director.Now();
        var shown = (move, Chibi ? ChibiSprite.FrameIndex(move, t) : (int)(t / _timer.Interval));
        if (shown == _shown) return;
        _shown = shown;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Key);
        var (move, t) = _director.Now();
        if (Chibi) ToastArt.Pixels(e.Graphics, 0, 0, _unit, fill => ChibiSprite.Paint(fill, move, t));
        else ToastArt.Pixels(e.Graphics, 0, 0, _unit, fill => ToastSprite.PaintMove(fill, move, t, _accent()));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
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
        else _director.Poke(); // saluda; si lo siguen molestando, se enoja
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
