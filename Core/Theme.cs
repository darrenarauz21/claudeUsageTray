using System.Drawing;
using Microsoft.Win32;

namespace ClaudeUsageTray;

/// <summary>Colores de Claude Desktop: marfil y gris cálido, con el acento arcilla.</summary>
sealed class Theme
{
    public required bool Dark { get; init; }
    public required Color Background { get; init; }
    public required Color Raised { get; init; }
    public required Color Hover { get; init; }
    public required Color Border { get; init; }
    public required Color Text { get; init; }
    public required Color Secondary { get; init; }
    public required Color Tertiary { get; init; }
    public required Color Track { get; init; }
    public required Color Accent { get; init; }
    public required Color Danger { get; init; }
    public required Color Success { get; init; }
    public required Color Warn { get; init; }

    public static readonly Theme Light = new()
    {
        Dark = false,
        Background = Hex(0xFAF9F5), Raised = Hex(0xFFFFFF), Hover = Hex(0xF0EEE6), Border = Hex(0xE3E0D6),
        Text = Hex(0x1F1E1D), Secondary = Hex(0x5E5D59), Tertiary = Hex(0x8F8D86), Track = Hex(0xEAE7DE),
        Accent = Hex(0xD97757), Danger = Hex(0xC6473B), Success = Hex(0x5E7A3A), Warn = Hex(0xB7791F),
    };

    public static readonly Theme DarkTheme = new()
    {
        Dark = true,
        Background = Hex(0x262624), Raised = Hex(0x343432), Hover = Hex(0x2E2E2C), Border = Hex(0x3D3D3A),
        Text = Hex(0xF5F4EF), Secondary = Hex(0xBFBDB5), Tertiary = Hex(0x8C8A83), Track = Hex(0x3A3936),
        Accent = Hex(0xD97757), Danger = Hex(0xE0695C), Success = Hex(0x8FA46C), Warn = Hex(0xE0A84B),
    };

    /// <summary>Sigue el modo de las aplicaciones de Windows.</summary>
    public static Theme Current
    {
        get
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return Light;
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0 ? DarkTheme : Light;
            }
            catch (Exception) { return Light; }
        }
    }

    public static Theme ForDark(bool dark) => dark ? DarkTheme : Light;

    public Color For(double percent, string? severity = null) =>
        severity is "critical" or "exceeded" or "blocked" || percent >= 90 ? Danger : Accent;

    static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
