using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeUsageTray;

/// <summary>Opciones de una sesión nueva. Null es «lo que diga la configuración de la cuenta».</summary>
sealed class LaunchOptions
{
    public string? ConfigDir { get; set; }
    public string? Folder { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? Advisor { get; set; }
    /// <summary>El primer mensaje de la sesión (una tarea que se le pidió a la mascota). No se guarda.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Prompt { get; set; }
}

/// <summary>
/// Abre Claude Code en una terminal con la cuenta, el modelo, el esfuerzo y el asesor elegidos. Solo
/// valen para esa sesión: --model, --effort y --advisor no tocan el settings.json de la cuenta.
/// </summary>
static class Launcher
{
    /// <summary>Asesor apagado: --advisor no lo acepta, se apaga con CLAUDE_CODE_DISABLE_ADVISOR_TOOL.</summary>
    public const string NoAdvisor = "none";

    public static readonly string[] Models = ["opus", "opus[1m]", "sonnet", "sonnet[1m]", "fable", "haiku", "opusplan"];
    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    public static readonly string[] Advisors = ["opus", "sonnet", "fable"];

    // El modelo se puede escribir a mano: solo alias o IDs, nada que rompa el comando.
    static readonly Regex Safe = new(@"^[A-Za-z0-9][A-Za-z0-9._\-\[\]]*$");

    public static bool IsValid(string? value) => value is null || Safe.IsMatch(value);

    static void Validate(LaunchOptions o)
    {
        foreach (var value in new[] { o.Model, o.Effort, o.Advisor })
            if (!IsValid(value)) throw new ArgumentException($"Valor no válido: {value}");
    }

    static bool IsDefaultDir(LaunchOptions o) => o.ConfigDir is null || Account.SamePath(o.ConfigDir, Account.DefaultDir);

    /// <summary>Script de PowerShell que deja la terminal en la carpeta y abre Claude Code.</summary>
    public static string Script(LaunchOptions o)
    {
        Validate(o);
        var script = new StringBuilder();
        if (!string.IsNullOrEmpty(o.Folder)) script.AppendLine($"Set-Location -LiteralPath {Quote(o.Folder)}");
        // La carpeta por defecto no lleva CLAUDE_CONFIG_DIR: se quita por si la terminal lo hereda.
        if (IsDefaultDir(o))
            script.AppendLine("Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue");
        else
            script.AppendLine($"$env:CLAUDE_CONFIG_DIR = {Quote(o.ConfigDir!)}");
        if (o.Advisor == NoAdvisor) script.AppendLine("$env:CLAUDE_CODE_DISABLE_ADVISOR_TOOL = '1'");

        var command = new StringBuilder("claude");
        if (o.Model is { } model) command.Append(" --model ").Append(Quote(model));
        if (o.Effort is { } effort) command.Append(" --effort ").Append(Quote(effort));
        if (o.Advisor is { } advisor && advisor != NoAdvisor) command.Append(" --advisor ").Append(Quote(advisor));
        if (!string.IsNullOrWhiteSpace(o.Prompt)) command.Append(' ').Append(Quote(o.Prompt));
        script.AppendLine(command.ToString());
        return script.ToString();
    }

    /// <summary>El mismo script para sh, bash o zsh (Linux y macOS), en una sola línea.</summary>
    public static string PosixScript(LaunchOptions o)
    {
        Validate(o);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(o.Folder)) parts.Add($"cd {PosixQuote(o.Folder)}");
        parts.Add(IsDefaultDir(o) ? "unset CLAUDE_CONFIG_DIR" : $"export CLAUDE_CONFIG_DIR={PosixQuote(o.ConfigDir!)}");
        if (o.Advisor == NoAdvisor) parts.Add("export CLAUDE_CODE_DISABLE_ADVISOR_TOOL=1");

        var command = new StringBuilder("claude");
        if (o.Model is { } model) command.Append(" --model ").Append(PosixQuote(model));
        if (o.Effort is { } effort) command.Append(" --effort ").Append(PosixQuote(effort));
        if (o.Advisor is { } advisor && advisor != NoAdvisor) command.Append(" --advisor ").Append(PosixQuote(advisor));
        if (!string.IsNullOrWhiteSpace(o.Prompt)) command.Append(' ').Append(PosixQuote(o.Prompt));
        parts.Add(command.ToString());
        return string.Join("; ", parts);
    }

    /// <summary>Abre la terminal del sistema. Queda abierta al salir de Claude Code.</summary>
    public static void Start(LaunchOptions o)
    {
        if (OperatingSystem.IsMacOS()) StartMac(o);
        else if (OperatingSystem.IsWindows()) StartWindows(o);
        else StartLinux(o);
    }

    /// <summary>Terminal de macOS: abre una ventana con el shell de inicio de la persona (su PATH incluido).</summary>
    static void StartMac(LaunchOptions o)
    {
        var script = PosixScript(o).Replace("\\", "\\\\").Replace("\"", "\\\"");
        var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var line in new[] { "tell application \"Terminal\"", "activate", $"do script \"{script}\"", "end tell" })
        {
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add(line);
        }
        using var _ = Process.Start(start);
    }

    /// <summary>
    /// La primera terminal conocida que esté instalada. Corre un bash de inicio de sesión (para que
    /// claude esté en el PATH) y, al salir, deja el shell de la persona abierto.
    /// </summary>
    static void StartLinux(LaunchOptions o)
    {
        var command = new[] { "bash", "-lc", PosixScript(o) + "; exec \"${SHELL:-bash}\"" };
        (string Program, string[] Prefix)[] terminals =
        [
            ("gnome-terminal", ["--"]), ("konsole", ["-e"]), ("xfce4-terminal", ["-x"]), ("kitty", []),
            ("alacritty", ["-e"]), ("wezterm", ["start", "--"]), ("tilix", ["-e"]), ("xterm", ["-e"]), ("x-terminal-emulator", ["-e"]),
        ];
        foreach (var (program, prefix) in terminals)
        {
            if (FindInPath(program) is not { } path) continue;
            var start = new ProcessStartInfo(path) { UseShellExecute = false };
            foreach (var arg in prefix.Concat(command)) start.ArgumentList.Add(arg);
            if (Directory.Exists(o.Folder)) start.WorkingDirectory = o.Folder;
            using var _ = Process.Start(start);
            return;
        }
        throw new InvalidOperationException("No se encontró una terminal (gnome-terminal, konsole, xfce4-terminal, kitty, alacritty, wezterm, tilix o xterm).");
    }

    static string? FindInPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, program)).FirstOrDefault(File.Exists);

    static string PosixQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>Windows Terminal si está; si no, una ventana de PowerShell.</summary>
    static void StartWindows(LaunchOptions o)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(o)));
        var folder = Directory.Exists(o.Folder) ? o.Folder! : Account.Home;
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");

        var start = File.Exists(wt)
            ? new ProcessStartInfo(wt) { ArgumentList = { "-d", folder, "powershell.exe" } }
            : new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder };
        start.UseShellExecute = false;
        start.ArgumentList.Add("-NoExit");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(encoded);
        using var _ = Process.Start(start);
    }

    static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
