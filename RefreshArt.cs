namespace ClaudeUsageTray;

/// <summary>Dibuja en Windows la animación al actualizar (RefreshSprite): en el pie de la ventana y como ícono.</summary>
static class RefreshArt
{
    public static void Draw(Graphics g, int x, int y, int unit, string style, RefreshFrame frame, Color color)
    {
        var brushes = new Dictionary<Color, SolidBrush>();
        try
        {
            RefreshSprite.Paint((c, cx, cy, w, h) =>
            {
                if (!brushes.TryGetValue(c, out var brush)) brushes[c] = brush = new SolidBrush(c);
                g.FillRectangle(brush, x + cx * unit, y + cy * unit, w * unit, h * unit);
            }, style, frame, color);
        }
        finally
        {
            foreach (var brush in brushes.Values) brush.Dispose();
        }
    }

    public static Icon Icon(string style, RefreshFrame frame, Color color)
    {
        var size = SystemInformation.SmallIconSize.Width;
        style = RefreshSprite.ForIcon(style, size); // en 16 o 24 px, la cabeza chica del chibi
        int columns = RefreshSprite.Columns(style), rows = RefreshSprite.Rows(style);
        var unit = Math.Max(1, size / columns);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
            Draw(g, (size - columns * unit) / 2, (size - rows * unit) / 2, unit, style, frame, color);
        var handle = bmp.GetHicon();
        try { return (Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
