using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeUsageTray;

static class Fonts
{
    public static Font Make(string family, float size, string fallback, FontStyle style = FontStyle.Regular)
    {
        var font = new Font(family, size, style);
        if (font.Name.Equals(family, StringComparison.OrdinalIgnoreCase)) return font;
        font.Dispose();
        return new Font(fallback, size, style);
    }
}

static class Draw
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Fill(Graphics g, Color color, RectangleF r, float radius)
    {
        using var brush = new SolidBrush(color);
        using var path = Rounded(r, radius);
        g.FillPath(brush, path);
    }

    public static void Outline(Graphics g, Color color, RectangleF r, float radius)
    {
        using var pen = new Pen(color);
        using var path = Rounded(r, radius);
        g.DrawPath(pen, path);
    }

    /// <summary>Ícono de la bandeja: el porcentaje de la sesión sobre un cuadro redondeado.</summary>
    public static Icon TrayIcon(double? percent, Color color)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Fill(g, color, new RectangleF(0, 0, size, size), size / 4f);

            var text = percent is null ? "·" : percent >= 100 ? "!" : Math.Round(percent.Value).ToString("0");
            var em = size * (text.Length == 1 ? 0.72f : 0.6f);
            using var font = new Font("Segoe UI Semibold", em, FontStyle.Regular, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, size, size + 1), format);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>Esquinas redondeadas, borde y modo oscuro de Windows 11 para una ventana sin marco.</summary>
static class Dwm
{
    const int UseImmersiveDarkMode = 20, WindowCornerPreference = 33, BorderColor = 34, Round = 2;

    public static void Apply(IntPtr hwnd, Theme theme)
    {
        try
        {
            var dark = theme.Dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref dark, sizeof(int));
            var corner = Round;
            DwmSetWindowAttribute(hwnd, WindowCornerPreference, ref corner, sizeof(int));
            var border = ColorTranslator.ToWin32(theme.Border);
            DwmSetWindowAttribute(hwnd, BorderColor, ref border, sizeof(int));
        }
        catch (Exception) { } // Windows 10: sin esquinas redondeadas.
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>Menús contextuales con los colores del tema.</summary>
static class Menus
{
    public static void Apply(ContextMenuStrip menu, Theme theme)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors(theme)) { RoundedEdges = false };
        menu.BackColor = theme.Raised;
        menu.ForeColor = theme.Text;
        foreach (ToolStripItem item in menu.Items) item.ForeColor = theme.Text;
        menu.ShowImageMargin = false;
    }

    sealed class MenuColors(Theme t) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => t.Raised;
        public override Color MenuBorder => t.Border;
        public override Color MenuItemBorder => t.Hover;
        public override Color MenuItemSelected => t.Dark ? Color.FromArgb(0x40, 0x40, 0x3D) : t.Hover;
        public override Color SeparatorDark => t.Border;
        public override Color SeparatorLight => t.Border;
        public override Color ImageMarginGradientBegin => t.Raised;
        public override Color ImageMarginGradientMiddle => t.Raised;
        public override Color ImageMarginGradientEnd => t.Raised;
        public override Color CheckBackground => t.Hover;
        public override Color CheckSelectedBackground => t.Hover;
        public override Color CheckPressedBackground => t.Hover;
    }
}

static class Prompt
{
    public static string? Ask(IWin32Window? owner, string title, string label, string value)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = false, TopMost = true,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        var box = new TextBox { Text = value, Width = form.LogicalToDeviceUnits(280) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = box.Width };
        var ok = new Button { Text = "Aceptar", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        layout.Controls.AddRange(new Control[] { new Label { Text = label, AutoSize = true }, box, buttons });
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        var result = owner is null ? form.ShowDialog() : form.ShowDialog(owner);
        return result == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }
}
