using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageTray;

/// <summary>Una cuenta es una carpeta de configuración de Claude Code (la de CLAUDE_CONFIG_DIR).</summary>
sealed class Account
{
    public string Label { get; set; } = "";
    public string ConfigDir { get; set; } = "";


    // La carpeta por defecto guarda su perfil fuera, en ~/.claude.json; las demás, dentro.
    [JsonIgnore]
    public string ProfilePath => SamePath(ConfigDir, DefaultDir)
        ? Path.Combine(Home, ".claude.json")
        : Path.Combine(ConfigDir, ".claude.json");

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string DefaultDir => Path.Combine(Home, ".claude");

    public static string LabelFor(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (name.Equals(".claude", StringComparison.OrdinalIgnoreCase)) return "principal";
        if (name.StartsWith(".claude-", StringComparison.OrdinalIgnoreCase)) return name[8..];
        return name.TrimStart('.');
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');
}

sealed class AppSettings
{
    public List<Account> Accounts { get; set; } = new();
    /// <summary>Carpetas quitadas a mano: la detección automática no las vuelve a agregar.</summary>
    public List<string> Ignored { get; set; } = new();
    public string? Selected { get; set; }
    public int RefreshMinutes { get; set; } = 5;
    public bool Notifications { get; set; } = true;
    public bool Sound { get; set; } = true;
    public string SoundPack { get; set; } = "clasico";
    /// <summary>Paleta del acento (Theme.Palettes).</summary>
    public string Palette { get; set; } = "claude";
    /// <summary>Animación al actualizar (RefreshSprite.Styles). Se elige junto con Mascot, en la opción «Mascota» (Mascots).</summary>
    public string Animation { get; set; } = "mascota";
    /// <summary>Mascota de los avisos y de la animación «mascota»: Mascots.Classic o Mascots.Chibi.</summary>
    public string Mascot { get; set; } = Mascots.Classic;
    /// <summary>La mascota (el chibi o la clásica) en su propia ventana sobre el escritorio. Se enciende al elegir el chibi.</summary>
    public bool DesktopMascot { get; set; }
    /// <summary>«Hablar»: la mascota del escritorio cuenta los avisos en una nubecita, además del aviso de siempre.</summary>
    public bool Speak { get; set; } = true;
    /// <summary>Dónde quedó la ventana de la mascota al arrastrarla, en pixeles de pantalla; null: junto a los avisos.</summary>
    public int? DesktopMascotX { get; set; }
    public int? DesktopMascotY { get; set; }
    /// <summary>Tamaño de la mascota del escritorio (MascotSizes).</summary>
    public string MascotSize { get; set; } = MascotSizes.Normal;
    /// <summary>La última pestaña abierta de Ajustes.</summary>
    public string SettingsTab { get; set; } = "Avisos";

    /// <summary>Chat con la mascota: el clic del saludo abre un campo en la nubecita.</summary>
    public bool Chat { get; set; }
    /// <summary>Modelo de la mascota (alias de Claude Code): uno liviano, para respuestas cortas.</summary>
    public string MascotModel { get; set; } = "haiku";
    /// <summary>Modelo de la isla; null: el de sus sesiones (Claude Code usa el predeterminado de la cuenta).</summary>
    public string? IslandModel { get; set; }
    /// <summary>Cuenta que responde el chat (su carpeta de configuración); null: la elegida en Uso.</summary>
    public string? AssistantAccount { get; set; }
    /// <summary>Carpeta donde se abren las tareas que se le piden a la mascota; null: la última de «Nueva sesión».</summary>
    public string? TaskFolder { get; set; }
    /// <summary>Un archivo soltado sobre la mascota o la isla se analiza.</summary>
    public bool AnalyzeDrops { get; set; } = true;
    /// <summary>Hablarle con el micrófono (mantener MicKey).</summary>
    public bool Mic { get; set; }
    /// <summary>La tecla para hablar: una que se mantiene (nombre de System.Windows.Forms.Keys) o una combinación grabada (MicKeys).</summary>
    public string MicKey { get; set; } = "RControlKey";
    /// <summary>El atajo que abre el chat (KeyNames: «Ctrl+Alt+Space»).</summary>
    public string ChatKey { get; set; } = KeyNames.DefaultChat;
    /// <summary>La tecla Copilot (Win+Mayús+F23) abre el chat y escucha, en vez de abrir Copilot.</summary>
    public bool CopilotKey { get; set; } = true;
    /// <summary>Leer las respuestas en voz alta con una voz del sistema.</summary>
    public bool ReadAloud { get; set; }

    /// <summary>La isla del borde de arriba de la pantalla.</summary>
    public bool Island { get; set; }
    /// <summary>La isla se abre con el cursor encima (si no, con un clic).</summary>
    public bool IslandHover { get; set; } = true;
    /// <summary>La isla queda compacta a la vista mientras una sesión trabaja o espera.</summary>
    public bool IslandWhileBusy { get; set; } = true;
    /// <summary>El resumen de la semana, los lunes, en la isla.</summary>
    public bool WeeklyRecap { get; set; } = true;
    /// <summary>El lunes del último resumen semanal mostrado.</summary>
    public DateOnly? RecapShown { get; set; }
    /// <summary>El asistente de configuración ya se mostró una vez.</summary>
    public bool SetupDone { get; set; }
    /// <summary>Lo último elegido en «Nueva sesión».</summary>
    public LaunchOptions Launch { get; set; } = new();
    /// <summary>Las 3 vistas fijas como pestañas, en orden. Las demás se abren desde el selector.</summary>
    public List<string> PinnedViews { get; set; } = [.. DefaultPinned];

    public static readonly string[] DefaultPinned = ["Usage", "Sessions", "Activity"];
}

static class SettingsStore
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception) { }
        return new();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception) { }
    }

    /// <summary>Agrega las carpetas ~/.claude* con credenciales (archivo o Llavero) que aún no están en la lista.</summary>
    public static bool DetectNew(AppSettings settings)
    {
        var changed = false;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(Account.Home, ".claude*").OrderBy(d => d).ToList(); }
        catch (Exception) { return false; }

        foreach (var dir in dirs)
        {
            if (!Credentials.Exists(dir)) continue;
            if (settings.Accounts.Any(a => Account.SamePath(a.ConfigDir, dir))) continue;
            if (settings.Ignored.Any(i => Account.SamePath(i, dir))) continue;
            settings.Accounts.Add(new Account { Label = Account.LabelFor(dir), ConfigDir = dir });
            changed = true;
        }
        return changed;
    }
}
