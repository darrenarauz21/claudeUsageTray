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

    /// <summary>Script de PowerShell que deja la terminal en la carpeta y abre Claude Code.</summary>
    public static string Script(LaunchOptions o)
    {
        foreach (var value in new[] { o.Model, o.Effort, o.Advisor })
            if (!IsValid(value)) throw new ArgumentException($"Valor no válido: {value}");

        var script = new StringBuilder();
        if (!string.IsNullOrEmpty(o.Folder)) script.AppendLine($"Set-Location -LiteralPath {Quote(o.Folder)}");
        // La carpeta por defecto no lleva CLAUDE_CONFIG_DIR: se quita por si la terminal lo hereda.
        if (o.ConfigDir is null || Account.SamePath(o.ConfigDir, Account.DefaultDir))
            script.AppendLine("Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue");
        else
            script.AppendLine($"$env:CLAUDE_CONFIG_DIR = {Quote(o.ConfigDir)}");
        if (o.Advisor == NoAdvisor) script.AppendLine("$env:CLAUDE_CODE_DISABLE_ADVISOR_TOOL = '1'");

        var command = new StringBuilder("claude");
        if (o.Model is { } model) command.Append(" --model ").Append(Quote(model));
        if (o.Effort is { } effort) command.Append(" --effort ").Append(Quote(effort));
        if (o.Advisor is { } advisor && advisor != NoAdvisor) command.Append(" --advisor ").Append(Quote(advisor));
        script.AppendLine(command.ToString());
        return script.ToString();
    }

    /// <summary>Windows Terminal si está; si no, una ventana de PowerShell. La terminal queda abierta al salir de Claude Code.</summary>
    public static void Start(LaunchOptions o)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Por ahora solo en Windows.");

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
