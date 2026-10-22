namespace ClaudeUsageTray;

/// <summary>
/// La nubecita de la mascota del escritorio (opción «Hablar»): cuenta el aviso a su manera
/// (MascotLines) en una nube de pixeles (BubbleSprite) con la colita hacia la mascota. Va arriba de
/// ella o, si no cabe, abajo; sigue a la mascota si la arrastran. Se va sola a los 8 s; un clic la
/// cierra y abre lo mismo que el aviso.
/// </summary>
sealed class BubbleForm : Form
{
    const int Lifetime = 8000;
    static readonly Color Key = Color.FromArgb(255, 0, 255); // el fondo: Windows lo deja transparente
    static readonly Color Ink = Color.FromArgb(0x2A, 0x1F, 0x1A);
    const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    public event Action<Notice>? Clicked;

    readonly Font _font = Fonts.Make("Segoe UI Variable Text", 9.5f, "Segoe UI");
    readonly System.Windows.Forms.Timer _timer = new() { Interval = Lifetime };
    readonly int _unit;
    Notice? _notice;
    string _text = "";
    Size _textSize;
    int _cols, _rows, _tailX;
    bool _tailDown = true;

    public BubbleForm()
    {
        _unit = Math.Max(1, (int)Math.Round(2 * DeviceDpi / 96f));
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Key;
        TransparencyKey = Key;
        Cursor = Cursors.Hand;
        _timer.Tick += (_, _) => Hide();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW (encima: TopMost, ver MascotWindow)
            return cp;
        }
    }

    /// <summary>Dice el aviso junto a la mascota (sus límites en pantalla).</summary>
    public void Say(Notice notice, bool chibi, Rectangle mascot)
    {
        _notice = notice;
        _text = MascotLines.For(notice, chibi);
        var pad = _unit * 5;
        _textSize = TextRenderer.MeasureText(_text, _font, new Size(LogicalToDeviceUnits(210), 0), Wrap);
        _cols = (_textSize.Width + pad * 2) / _unit + 1;
        _rows = (_textSize.Height + pad * 2) / _unit + 1 + BubbleSprite.Tail;
        ClientSize = new Size(_cols * _unit, _rows * _unit);
        Follow(mascot);
        if (!Visible) Show();
        Invalidate();
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>
    /// Se pone arriba de la mascota con la colita hacia su cabeza (o abajo, si arriba no cabe), sin
    /// salirse de la pantalla.
    /// </summary>
    public void Follow(Rectangle mascot)
    {
        var area = Screen.FromRectangle(mascot).WorkingArea;
        var headX = mascot.X + mascot.Width / 2;
        _tailDown = mascot.Y + mascot.Height / 20 - Height >= area.Top;
        var y = _tailDown ? mascot.Y + mascot.Height / 20 - Height : mascot.Bottom - mascot.Height / 20;
        var x = Math.Clamp(headX - Width * 2 / 3, area.Left, Math.Max(area.Left, area.Right - Width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height));
        _tailX = (headX - x) / _unit;
        Location = new Point(x, y);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Key);
        ToastArt.Pixels(g, 0, 0, _unit, fill => BubbleSprite.Paint(fill, _cols, _rows, _tailX, _tailDown));
        var top = (_tailDown ? 0 : BubbleSprite.Tail * _unit) + (_rows - BubbleSprite.Tail) * _unit / 2 - _textSize.Height / 2;
        var left = (ClientSize.Width - _textSize.Width) / 2;
        TextRenderer.DrawText(g, _text, _font, new Rectangle(left, top, _textSize.Width, _textSize.Height), Ink, Color.White, Wrap);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Hide();
        _timer.Stop();
        if (e.Button == MouseButtons.Left && _notice is { } notice) Clicked?.Invoke(notice);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _font.Dispose();
        }
        base.Dispose(disposing);
    }
}
