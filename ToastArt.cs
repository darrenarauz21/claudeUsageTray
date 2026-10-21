namespace ClaudeUsageTray;

/// <summary>Dibuja en Windows la mascota de los avisos (ToastSprite) escalada al rectángulo pedido.</summary>
static class ToastArt
{
    public const int Columns = ToastSprite.Columns, Rows = ToastSprite.Rows;

    public static void Draw(Graphics g, Rectangle bounds, NoticeKind kind, long t, Color body)
    {
        var unit = Math.Max(1, Math.Min(bounds.Width / Columns, bounds.Height / Rows));
        var x0 = bounds.X + (bounds.Width - Columns * unit) / 2;
        var y0 = bounds.Y + (bounds.Height - Rows * unit) / 2;
        var brushes = new Dictionary<Color, SolidBrush>();
        try
        {
            ToastSprite.Paint((color, x, y, w, h) =>
            {
                if (!brushes.TryGetValue(color, out var brush)) brushes[color] = brush = new SolidBrush(color);
                g.FillRectangle(brush, x0 + x * unit, y0 + y * unit, w * unit, h * unit);
            }, kind, t, body);
        }
        finally
        {
            foreach (var brush in brushes.Values) brush.Dispose();
        }
    }
}
