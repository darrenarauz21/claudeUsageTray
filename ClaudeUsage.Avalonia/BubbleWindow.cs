using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SD = System.Drawing;

namespace ClaudeUsageTray;

/// <summary>
/// La nubecita de la mascota del escritorio (opción «Hablar»), igual que en Windows: cuenta el aviso
/// a su manera (MascotLines) en una nube de pixeles (BubbleSprite) con la colita hacia la mascota.
/// Va arriba de ella o, si no cabe, abajo; sigue a la mascota si la arrastran. Se va sola a los 8 s;
/// un clic la cierra y abre lo mismo que el aviso.
/// </summary>
sealed class BubbleWindow : Window
{
    const double Unit = 2, TextWidth = 210;
    static readonly SD.Color Ink = SD.Color.FromArgb(0x2A, 0x1F, 0x1A);

    public event Action<Notice>? Clicked;

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    readonly BubbleView _view = new();
    Notice? _notice;

    public BubbleWindow()
    {
        Title = "Uso de Claude";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Content = _view;
        Cursor = new Cursor(StandardCursorType.Hand);
        _timer.Tick += (_, _) => { _timer.Stop(); Hide(); };
    }

    /// <summary>Dice el aviso junto a la mascota (sus límites en pantalla, en pixeles).</summary>
    public void Say(Notice notice, bool chibi, PixelRect mascot)
    {
        _notice = notice;
        var text = Gfx.Text(MascotLines.For(notice, chibi), Gfx.ToastBody, Gfx.B(Ink), TextWidth, maxLines: 4);
        const double pad = Unit * 5;
        var cols = (int)Math.Ceiling((text.WidthIncludingTrailingWhitespace + pad * 2) / Unit) + 1;
        var rows = (int)Math.Ceiling((text.Height + pad * 2) / Unit) + 1 + BubbleSprite.Tail;
        _view.Set(text, cols, rows);
        Width = cols * Unit;
        Height = rows * Unit;
        Follow(mascot);
        if (!IsVisible) Show();
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Arriba de la mascota con la colita hacia su cabeza (o abajo, si arriba no cabe), sin salirse de la pantalla.</summary>
    public void Follow(PixelRect mascot)
    {
        var screen = Screens.ScreenFromPoint(mascot.Center) ?? Placement.PrimaryScreen(this);
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1280, 800);
        var scale = screen?.Scaling ?? 1;
        var width = (int)Math.Ceiling(Width * scale);
        var height = (int)Math.Ceiling(Height * scale);
        var headX = mascot.X + mascot.Width / 2;
        var head = mascot.Y + mascot.Height / 20;
        var tailDown = head - height >= area.Y;
        var y = tailDown ? head - height : mascot.Bottom - mascot.Height / 20;
        var x = Math.Clamp(headX - width * 2 / 3, area.X, Math.Max(area.X, area.Right - width));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height));
        _view.Point((int)((headX - x) / scale / Unit), tailDown);
        Position = new PixelPoint(x, y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _timer.Stop();
        Hide();
        if (e.InitialPressMouseButton == MouseButton.Left && _notice is { } notice) Clicked?.Invoke(notice);
    }

    sealed class BubbleView : Control
    {
        FormattedText? _text;
        int _cols, _rows, _tailX;
        bool _tailDown = true;

        public void Set(FormattedText text, int cols, int rows) => (_text, _cols, _rows) = (text, cols, rows);

        public void Point(int tailX, bool tailDown)
        {
            (_tailX, _tailDown) = (tailX, tailDown);
            InvalidateVisual();
        }

        public override void Render(DrawingContext dc)
        {
            if (_text is null) return;
            Gfx.Pixels(dc, 0, 0, Unit, fill => BubbleSprite.Paint(fill, _cols, _rows, _tailX, _tailDown));
            var bodyTop = _tailDown ? 0 : BubbleSprite.Tail * Unit;
            var bodyHeight = (_rows - BubbleSprite.Tail) * Unit;
            dc.DrawText(_text, new Point((Bounds.Width - _text.WidthIncludingTrailingWhitespace) / 2, bodyTop + (bodyHeight - _text.Height) / 2));
        }
    }
}
