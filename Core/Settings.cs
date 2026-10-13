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
