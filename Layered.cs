using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// Ventanas con transparencia por pixel (WS_EX_LAYERED + UpdateLayeredWindow): bordes suaves, sombra
/// y cualquier forma, sin el borde dentado de una región. Se dibujan en un mapa de bits con GDI+ y se
/// entregan enteras; el mouse atraviesa los pixeles transparentes.
/// </summary>
static class Layered
{
    public const int ExLayered = 0x80000;

    /// <summary>Pone el mapa de bits (32 bits con alfa) como contenido de la ventana, en ese lugar.</summary>
    public static void Show(IntPtr hwnd, Bitmap bitmap, Point location, byte opacity = 255)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var hBitmap = IntPtr.Zero;
        var old = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(memory, hBitmap);
            var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var source = new POINT();
            var top = new POINT { x = location.X, y = location.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
            UpdateLayeredWindow(hwnd, screen, ref top, ref size, memory, ref source, 0, ref blend, 2); // ULW_ALPHA
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
            if (hBitmap != IntPtr.Zero)
            {
                SelectObject(memory, old);
                DeleteObject(hBitmap);
            }
            DeleteDC(memory);
        }
    }

    /// <summary>Un mapa de bits vacío y transparente para dibujar una ventana con alfa.</summary>
    public static Bitmap Canvas(int width, int height) => new(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppArgb);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
        ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}

/// <summary>
/// Un TextBox que muestra su texto de ayuda mientras está vacío, también con el foco y en varias
/// líneas (el PlaceholderText de Windows solo sale en una línea y sin foco).
/// </summary>
sealed class CueTextBox : TextBox
{
    string _cue = "";
    Color _cueColor = Color.Gray;

    public string Cue
    {
        get => _cue;
        set { _cue = value; Invalidate(); }
    }

    public Color CueColor
    {
        get => _cueColor;
        set { _cueColor = value; Invalidate(); }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (TextLength <= 1) Invalidate(); // aparece o se va la ayuda
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F || TextLength > 0 || _cue.Length == 0) return; // WM_PAINT
        using var g = CreateGraphics();
        TextRenderer.DrawText(g, _cue, Font, new Rectangle(1, 0, ClientSize.Width - 1, ClientSize.Height), _cueColor, BackColor,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}
