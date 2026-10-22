using System.Drawing;
using Microsoft.Win32;

namespace ClaudeUsageTray;

/// <summary>
/// Colores de Claude Desktop: marfil y gris cálido, con el acento de la paleta elegida en Ajustes
/// (la arcilla de Claude por defecto). El acento pinta barras, interruptores, enlaces y la mascota.
/// </summary>
sealed record Theme
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

    /// <summary>Paletas del acento, cada una con su tono para el modo claro y para el oscuro.</summary>
    public static readonly (string Id, string Label, Color Light, Color Dark)[] Palettes =
    [
        ("claude", "Claude", Hex(0xD97757), Hex(0xD97757)),
        ("oceano", "Océano", Hex(0x2F7FC1), Hex(0x5BA3E0)),
        ("bosque", "Bosque", Hex(0x3F8A57), Hex(0x6DB283)),
        ("lavanda", "Lavanda", Hex(0x7E62C4), Hex(0xA48BE0)),
        ("rosa", "Rosa", Hex(0xC4507F), Hex(0xE07CA4)),
        ("grafito", "Grafito", Hex(0x6E6C66), Hex(0xA8A69E)),
    ];

    /// <summary>La paleta elegida (Id de Palettes); si no existe, la de Claude.</summary>
    public static string SelectedPalette { get; set; } = "claude";

    /// <summary>Sigue el modo de las aplicaciones de Windows.</summary>
    public static Theme Current
    {
        get
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return ForDark(false);
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return ForDark(key?.GetValue("AppsUseLightTheme") is int light && light == 0);
            }
            catch (Exception) { return ForDark(false); }
        }
    }

    public static Theme ForDark(bool dark)
    {
        var palette = Palettes.FirstOrDefault(p => p.Id == SelectedPalette);
        if (palette.Id is null) palette = Palettes[0];
        return (dark ? DarkTheme : Light) with { Accent = dark ? palette.Dark : palette.Light };
    }

    public static string PaletteLabel(string id) => Palettes.FirstOrDefault(p => p.Id == id).Label ?? Palettes[0].Label;

    public Color For(double percent, string? severity = null) =>
        severity is "critical" or "exceeded" or "blocked" || percent >= 90 ? Danger : Accent;

    static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
