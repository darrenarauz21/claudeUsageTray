using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClaudeUsageTray;

/// <summary>
/// La mascota sobre el escritorio (el chibi o la clásica), sin marco ni fondo y siempre encima (igual
/// que en Windows). Hace lo que diga ChibiDirector; se arrastra para cambiarla de lugar (sin salirse
/// de la pantalla), un clic la hace saludar (y muchos, enojarse) y el clic derecho abre su menú.
/// </summary>
sealed class MascotWindow : Window
{

    public event Action? OpenRequested;
    public event Action? HideRequested;
    /// <summary>Se soltó en otro lugar (esquina de arriba a la izquierda, en pixeles de pantalla).</summary>
    public event Action<PixelPoint>? Moved;

    readonly ChibiDirector _director;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly MascotView _view;
    readonly PixelPoint? _saved;
    PixelPoint? _pressAt;
    PixelPoint _startPosition;
    bool _dragging;

    /// <summary>El chibi (true) o la clásica, que se pinta con el acento de la paleta.</summary>
    public bool Chibi { get; }

    /// <summary>Dónde está en la pantalla, en pixeles (para la nubecita).</summary>
    public PixelRect ScreenBounds => new(Position, PixelSize.FromSize(Bounds.Size, RenderScaling));

    public MascotWindow(ChibiDirector director, PixelPoint? saved, bool chibi, Func<System.Drawing.Color> accent)
    {
        _director = director;
        Chibi = chibi;
        _saved = saved;
        Title = "Uso de Claude";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        var unit = chibi ? 3 : 4;
        Width = (chibi ? ChibiSprite.Columns : ToastSprite.Columns) * unit;
        Height = (chibi ? ChibiSprite.Rows : ToastSprite.Rows) * unit;
        _view = new MascotView(director, chibi, unit, accent) { Cursor = new Cursor(StandardCursorType.Hand) };
        Content = _view;

        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Abrir Uso de Claude" };
        open.Click += (_, _) => OpenRequested?.Invoke();
        var hide = new MenuItem { Header = "Ocultar del escritorio" };
        // Después del clic: ocultar cierra esta ventana, y con ella el menú que está avisando.
        hide.Click += (_, _) => Dispatcher.UIThread.Post(() => HideRequested?.Invoke());
        menu.Items.Add(open);
        menu.Items.Add(hide);
        ContextMenu = menu;

        _timer.Tick += (_, _) => _view.Tick();
        Opened += (_, _) =>
        {
            Position = Fit(_saved ?? DefaultPosition());
            _director.Gesture(ChibiMove.Wave); // saluda al aparecer
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>La posición más cercana con la ventana entera dentro de la pantalla en que cae: nunca se pierde por un borde.</summary>
    PixelPoint Fit(PixelPoint at)
    {
        var screen = Screens.ScreenFromPoint(at) ?? Placement.PrimaryScreen(this);
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1280, 800);
        var scale = screen?.Scaling ?? 1;
        var width = (int)Math.Ceiling(Width * scale);
        var height = (int)Math.Ceiling(Height * scale);
        return new PixelPoint(Math.Clamp(at.X, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(at.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    /// <summary>Abajo a la derecha, a la izquierda de donde salen los avisos.</summary>
    PixelPoint DefaultPosition()
    {
        var screen = Placement.PrimaryScreen(this);
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1280, 800);
        var scale = screen?.Scaling ?? 1;
        var width = (int)Math.Ceiling(Width * scale);
        var height = (int)Math.Ceiling(Height * scale);
        return new PixelPoint(area.Right - (int)((ToastWindow.ToastWidth + 24) * scale) - width, area.Bottom - height);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressAt = this.PointToScreen(e.GetPosition(this));
        _startPosition = Position;
        _dragging = false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressAt is not { } press) return;
        var now = this.PointToScreen(e.GetPosition(this));
        var (dx, dy) = (now.X - press.X, now.Y - press.Y);
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < 4 * RenderScaling) return;
        _dragging = true;
        Position = Fit(new PixelPoint(_startPosition.X + dx, _startPosition.Y + dy));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || _pressAt is null) return;
        _pressAt = null;
        if (_dragging) Moved?.Invoke(Position);
        else _director.Poke(); // saluda; si lo siguen molestando, se enoja
    }

    sealed class MascotView(ChibiDirector director, bool chibi, double unit, Func<System.Drawing.Color> accent) : Control
    {
        (ChibiMove Move, int Frame) _shown = (ChibiMove.Sleep, -1);

        /// <summary>Repinta solo cuando cambia el cuadro (la clásica se anima con el tiempo: cada vuelta del reloj).</summary>
        public void Tick()
        {
            var (move, t) = director.Now();
            var shown = (move, chibi ? ChibiSprite.FrameIndex(move, t) : (int)(t / 40));
            if (shown == _shown) return;
            _shown = shown;
            InvalidateVisual();
        }

        public override void Render(DrawingContext dc)
        {
            var (move, t) = director.Now();
            if (chibi) Gfx.Pixels(dc, 0, 0, unit, fill => ChibiSprite.Paint(fill, move, t));
            else Gfx.Pixels(dc, 0, 0, unit, fill => ToastSprite.PaintMove(fill, move, t, accent()));
        }
    }
}
