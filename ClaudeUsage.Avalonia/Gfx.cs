using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using SD = System.Drawing;

namespace ClaudeUsageTray;

sealed record UiFont(Typeface Typeface, double Size)
{
    double? _lineHeight;
    public double LineHeight => _lineHeight ??= Gfx.Measure("Ág", this).Height;
}

enum Glyph { Refresh, Add, Check, Close, Bell, BellOff, ChevronDown, ChevronUp }

/// <summary>Fuentes, colores, texto, íconos vectoriales y pixeles para la interfaz de Avalonia.</summary>
static class Gfx
{
    // Las mismas fuentes que en Windows y, si no están, las equivalentes de macOS y Linux.
    static readonly FontFamily Sans = new("Segoe UI Variable Text, Segoe UI, SF Pro Text, Helvetica Neue, Ubuntu, Cantarell, Noto Sans, DejaVu Sans, Arial");
    static readonly FontFamily Serif = new("Georgia, Charter, Cambria, DejaVu Serif, Liberation Serif, Times New Roman");
    static readonly FontFamily Symbols = new("Segoe UI Symbol, Apple Symbols, DejaVu Sans, Noto Sans Symbols2, Noto Sans Symbols");

    public static readonly UiFont Title = new(new Typeface(Serif), 17.3);
    public static readonly UiFont Body = new(new Typeface(Sans), 13);
    public static readonly UiFont BodyStrong = new(new Typeface(Sans, FontStyle.Normal, FontWeight.SemiBold), 13);
    public static readonly UiFont Small = new(new Typeface(Sans), 11.7);
    public static readonly UiFont ToastTitle = new(new Typeface(Sans, FontStyle.Normal, FontWeight.SemiBold), 13.3);
    public static readonly UiFont ToastBody = new(new Typeface(Sans), 12);
    public static readonly UiFont ToastSmall = new(new Typeface(Sans), 11.3);
    public static readonly UiFont Spinner = new(new Typeface(Symbols), 14.6);

    static readonly Dictionary<int, IImmutableSolidColorBrush> BrushCache = new();

    public static Color C(SD.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public static IImmutableSolidColorBrush B(SD.Color c)
    {
        var key = c.ToArgb();
        if (!BrushCache.TryGetValue(key, out var brush)) BrushCache[key] = brush = new ImmutableSolidColorBrush(C(c));
        return brush;
    }

    public static IPen P(SD.Color c, double thickness = 1) =>
        new ImmutablePen(B(c), thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    public static FormattedText Text(string text, UiFont font, IBrush brush, double maxWidth = 0, int maxLines = 1,
        TextAlignment align = TextAlignment.Left)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, font.Typeface, font.Size, brush);
        if (maxWidth > 0)
        {
            ft.MaxTextWidth = maxWidth;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        ft.MaxLineCount = maxLines;
        ft.TextAlignment = align;
        return ft;
    }

    public static Size Measure(string text, UiFont font)
    {
        var ft = Text(text, font, Brushes.Black);
        return new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
    }

    /// <summary>Altura que ocupa un texto con salto de línea en un ancho dado (hasta maxLines líneas).</summary>
    public static double MeasureWrapped(string text, UiFont font, double width, int maxLines) =>
        Text(text, font, Brushes.Black, width, maxLines).Height;

    /// <summary>Escribe en el rectángulo; sin contexto (solo medir) no hace nada.</summary>
    public static void Write(DrawingContext? dc, string text, UiFont font, SD.Color color, Rect r,
        TextAlignment align = TextAlignment.Left, bool center = false, int maxLines = 1)
    {
        if (dc is null || text.Length == 0) return;
        var ft = Text(text, font, B(color), r.Width, maxLines, align);
        var y = center ? r.Y + (r.Height - ft.Height) / 2 : r.Y;
        using (dc.PushClip(r.Inflate(new Thickness(0, 2))))
            dc.DrawText(ft, new Point(r.X, y));
    }

    public static void Fill(DrawingContext dc, SD.Color color, Rect r, double radius) =>
        dc.DrawRectangle(B(color), null, r, radius, radius);

    public static void Outline(DrawingContext dc, SD.Color color, Rect r, double radius) =>
        dc.DrawRectangle(null, P(color), r.Deflate(0.5), radius, radius);

    // Íconos en una caja de 16 × 16, dibujados con trazo.
    static readonly Dictionary<Glyph, Geometry> Paths = new()
    {
        [Glyph.Refresh] = Geometry.Parse("M13.5,8 A5.5,5.5 0 1 1 12.2,4.5 M12.6,1.6 L12.3,4.7 L9.2,4.4"),
        [Glyph.Add] = Geometry.Parse("M8,3 L8,13 M3,8 L13,8"),
        [Glyph.Check] = Geometry.Parse("M3.5,8.5 L6.5,11.5 L12.5,4.5"),
        [Glyph.Close] = Geometry.Parse("M4,4 L12,12 M12,4 L4,12"),
        [Glyph.Bell] = Geometry.Parse("M4,11.5 L4,7.5 A4,4 0 0 1 12,7.5 L12,11.5 L13,12.5 L3,12.5 Z M6.5,14.5 L9.5,14.5"),
        [Glyph.BellOff] = Geometry.Parse("M4,11.5 L4,7.5 A4,4 0 0 1 12,7.5 L12,11.5 L13,12.5 L3,12.5 Z M6.5,14.5 L9.5,14.5 M2,2 L14,14"),
        [Glyph.ChevronDown] = Geometry.Parse("M4,6 L8,10 L12,6"),
        [Glyph.ChevronUp] = Geometry.Parse("M4,10 L8,6 L12,10"),
    };

    public static void Icon(DrawingContext? dc, Glyph glyph, SD.Color color, Rect box, double size = 16)
    {
        if (dc is null) return;
        var scale = size / 16;
        var x = box.X + (box.Width - size) / 2;
        var y = box.Y + (box.Height - size) / 2;
        using (dc.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, y)))
            dc.DrawGeometry(null, P(color, 1.4), Paths[glyph]);
    }

    /// <summary>Pinta un dibujo de pixeles (mascota) con casillas de tamaño unit, sin suavizar bordes.</summary>
    public static void Pixels(DrawingContext dc, double x0, double y0, double unit, Action<PixelFill> paint)
    {
        using (dc.PushRenderOptions(new RenderOptions { EdgeMode = EdgeMode.Aliased }))
            paint((color, x, y, w, h) => dc.FillRectangle(B(color), new Rect(x0 + x * unit, y0 + y * unit, w * unit, h * unit)));
    }

    // ── Íconos de la bandeja ────────────────────────────────────────────────────────────────

    const int IconSize = 32;

    public static WindowIcon PercentIcon(double? percent, SD.Color color)
    {
        using var bmp = new RenderTargetBitmap(new PixelSize(IconSize, IconSize), new Vector(96, 96));
        using (var dc = bmp.CreateDrawingContext())
        {
            dc.DrawRectangle(B(color), null, new Rect(0, 0, IconSize, IconSize), 8, 8);
            var text = percent is null ? "·" : percent >= 100 ? "!" : Math.Round(percent.Value).ToString("0");
            var size = text.Length == 1 ? 23 : 19;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Sans, FontStyle.Normal, FontWeight.Bold), size, Brushes.White);
            dc.DrawText(ft, new Point((IconSize - ft.Width) / 2, (IconSize - ft.Height) / 2));
        }
        return ToIcon(bmp);
    }

    public static WindowIcon MascotIcon(MascotFrame frame)
    {
        using var bmp = new RenderTargetBitmap(new PixelSize(IconSize, IconSize), new Vector(96, 96));
        using (var dc = bmp.CreateDrawingContext())
        {
            var unit = IconSize / MascotSprite.Columns; // 2
            Pixels(dc, (IconSize - MascotSprite.Columns * unit) / 2.0, (IconSize - MascotSprite.Rows * unit) / 2.0, unit,
                fill => MascotSprite.Paint(fill, frame));
        }
        return ToIcon(bmp);
    }

    static WindowIcon ToIcon(Bitmap bmp)
    {
        var stream = new MemoryStream();
        bmp.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    // ── Sistema ─────────────────────────────────────────────────────────────────────────────

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        var program = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        try { Process.Start(new ProcessStartInfo(program) { ArgumentList = { path }, UseShellExecute = false }); }
        catch (Exception) { }
    }

    public static SD.Color Blend(SD.Color from, SD.Color to, double amount) => SD.Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));
}
