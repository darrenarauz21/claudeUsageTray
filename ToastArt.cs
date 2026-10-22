namespace ClaudeUsageTray;

/// <summary>Dibuja en Windows la mascota de los avisos (ToastSprite) o el chibi (ChibiSprite).</summary>
static class ToastArt
{
    public const int Columns = ToastSprite.Columns, Rows = ToastSprite.Rows;

    /// <summary>La mascota clásica, escalada al rectángulo pedido.</summary>
    public static void Draw(Graphics g, Rectangle bounds, NoticeKind kind, long t, Color body)
    {
        var unit = Math.Max(1, Math.Min(bounds.Width / Columns, bounds.Height / Rows));
        var x0 = bounds.X + (bounds.Width - Columns * unit) / 2;
        var y0 = bounds.Y + (bounds.Height - Rows * unit) / 2;
        Pixels(g, x0, y0, unit, fill => ToastSprite.Paint(fill, kind, t, body));
    }

    /// <summary>
    /// El chibi asomado desde abajo en la columna pedida (todo el alto del aviso): se ve de la cabeza
    /// al ruedo y, cuando salta, también las piernas.
    /// </summary>
    public static void DrawChibi(Graphics g, Rectangle column, ChibiMove move, long t)
    {
        var unit = Math.Max(1, column.Height / ChibiSprite.ToastRows);
        var x0 = column.X + (column.Width - ChibiSprite.Columns * unit) / 2;
        var y0 = column.Bottom - ChibiSprite.HemRow * unit;
        var state = g.Save();
        g.SetClip(column);
        try { Pixels(g, x0, y0, unit, fill => ChibiSprite.Paint(fill, move, t)); }
        finally { g.Restore(state); }
    }

    /// <summary>Pinta un dibujo de pixeles con casillas de tamaño unit desde (x0, y0).</summary>
    public static void Pixels(Graphics g, int x0, int y0, int unit, Action<PixelFill> paint)
    {
        var brushes = new Dictionary<Color, SolidBrush>();
        try
        {
            paint((color, x, y, w, h) =>
            {
                if (!brushes.TryGetValue(color, out var brush)) brushes[color] = brush = new SolidBrush(color);
                g.FillRectangle(brush, x0 + x * unit, y0 + y * unit, w * unit, h * unit);
            });
        }
        finally
        {
            foreach (var brush in brushes.Values) brush.Dispose();
        }
    }
}
