namespace ClaudeUsageTray;

/// <summary>Dibuja en Windows la mascota chica (MascotSprite): en el pie de la ventana y como ícono.</summary>
static class Mascot
{
    public const int Columns = MascotSprite.Columns, Rows = MascotSprite.Rows;

    public static void Draw(Graphics g, int x, int y, int unit, MascotFrame frame)
    {
        var brushes = new Dictionary<Color, SolidBrush>();
        try
        {
            MascotSprite.Paint((color, cx, cy, w, h) =>
            {
                if (!brushes.TryGetValue(color, out var brush)) brushes[color] = brush = new SolidBrush(color);
                g.FillRectangle(brush, x + cx * unit, y + cy * unit, w * unit, h * unit);
            }, frame);
        }
        finally
        {
            foreach (var brush in brushes.Values) brush.Dispose();
        }
    }

    public static Icon Icon(MascotFrame frame)
    {
        var size = SystemInformation.SmallIconSize.Width;
        var unit = Math.Max(1, size / Columns);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
            Draw(g, (size - Columns * unit) / 2, (size - Rows * unit) / 2, unit, frame);
        var handle = bmp.GetHicon();
        try { return (Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
