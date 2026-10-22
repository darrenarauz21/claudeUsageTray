using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClaudeUsageTray;

/// <summary>
/// El chibi sobre el escritorio, sin marco ni fondo y siempre encima (igual que en Windows). Hace
/// lo que diga ChibiDirector; se arrastra para cambiarlo de lugar, un clic lo hace saludar y el
/// clic derecho abre su menú.
/// </summary>
sealed class MascotWindow : Window
{
    const double Unit = 3;

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

    public MascotWindow(ChibiDirector director, PixelPoint? saved)
    {
        _director = director;
        _saved = saved;
        Title = "Uso de Claude";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Width = ChibiSprite.Columns * Unit;
        Height = ChibiSprite.Rows * Unit;
        _view = new MascotView(director) { Cursor = new Cursor(StandardCursorType.Hand) };
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
            Position = _saved is { } p && Screens.All.Any(s => s.WorkingArea.Contains(p)) ? p : DefaultPosition();
            _director.Gesture(ChibiMove.Wave); // saluda al aparecer
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
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
        Position = new PixelPoint(_startPosition.X + dx, _startPosition.Y + dy);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || _pressAt is null) return;
        _pressAt = null;
        if (_dragging) Moved?.Invoke(Position);
        else _director.Gesture(ChibiMove.Wave);
    }

    sealed class MascotView(ChibiDirector director) : Control
    {
        (ChibiMove Move, int Frame) _shown = (ChibiMove.Sleep, -1);

        /// <summary>Repinta solo cuando cambia el cuadro.</summary>
        public void Tick()
        {
            var (move, t) = director.Now();
            var shown = (move, ChibiSprite.FrameIndex(move, t));
            if (shown == _shown) return;
            _shown = shown;
            InvalidateVisual();
        }

        public override void Render(DrawingContext dc)
        {
            var (move, t) = director.Now();
            Gfx.Pixels(dc, 0, 0, Unit, fill => ChibiSprite.Paint(fill, move, t));
        }
    }
}
