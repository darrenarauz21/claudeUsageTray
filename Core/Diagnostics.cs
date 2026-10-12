using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageTray;

/// <summary>
/// Resumen para saber por qué no salen avisos en otro equipo. Sin correos, tokens ni texto de las
/// conversaciones: solo carpetas, conteos y versiones.
/// </summary>
static class Diagnostics
{
    public static string Report(AppSettings settings, IReadOnlyList<SessionInfo>? sessions, int noticesShown)
    {
        var sb = new StringBuilder();
        var version = typeof(Diagnostics).Assembly.GetName().Version?.ToString(3) ?? "?";
        sb.AppendLine($"Uso de Claude {version} · {RuntimeInformation.OSDescription} · {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Notificaciones: {(settings.Notifications ? "encendidas" : "apagadas")} · avisos mostrados desde que se abrió: {noticesShown}");
        var envDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        sb.AppendLine($"CLAUDE_CONFIG_DIR: {(string.IsNullOrEmpty(envDir) ? "(sin definir)" : envDir)}");

        sb.AppendLine("Carpetas de Claude Code:");
        var dirs = new List<string>();
        try { dirs.AddRange(Directory.EnumerateDirectories(Account.Home, ".claude*")); } catch (Exception) { }
        dirs.AddRange(settings.Accounts.Select(a => a.ConfigDir));
        if (!string.IsNullOrEmpty(envDir)) dirs.Add(envDir);
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d))
            sb.AppendLine("  " + DescribeDir(dir, settings));
        if (dirs.Count == 0) sb.AppendLine("  (ninguna)");

        sb.AppendLine($"Cuentas en la app: {(settings.Accounts.Count == 0 ? "ninguna" : string.Join(", ", settings.Accounts.Select(a => $"{a.Label} ({a.ConfigDir.Replace(Account.Home, "~")})")))}");

        if (sessions is null) sb.AppendLine("Sesiones leídas por la app: todavía ninguna lectura");
        else
        {
            var states = sessions.GroupBy(s => s.State).Select(g => $"{g.Key} {g.Count()}");
            sb.AppendLine($"Sesiones leídas por la app: {sessions.Count}{(sessions.Count == 0 ? "" : " (" + string.Join(", ", states) + ")")}");
        }

        sb.AppendLine($"Eventos del hook: {(File.Exists(NoticeDetector.EventsPath) ? "hay archivo de eventos" : "ninguno recibido")}");
        return sb.ToString();
    }

    static string DescribeDir(string dir, AppSettings settings)
    {
        var name = dir.Replace(Account.Home, "~");
        if (!Directory.Exists(dir)) return $"{name}: no existe";

        var credentials = Credentials.Describe(dir);
        var ignored = settings.Ignored.Any(i => Account.SamePath(i, dir)) ? " · quitada de la lista" : "";

        var sessionsDir = Path.Combine(dir, "sessions");
        string sessions;
        string? claudeVersion = null;
        if (!Directory.Exists(sessionsDir)) sessions = "sin carpeta sessions";
        else
        {
            var files = Directory.EnumerateFiles(sessionsDir, "*.json").ToList();
            var alive = 0;
            foreach (var file in files)
            {
                try
                {
                    using var doc = JsonDocument.Parse(Json.ReadShared(file));
                    var r = doc.RootElement;
                    var pid = r.TryGetProperty("pid", out var p) && p.TryGetInt32(out var value) ? value : 0;
                    if (pid != 0 && SessionMonitor.IsAlive(pid, Json.Str(r, "procStart"))) alive++;
                    claudeVersion ??= Json.Str(r, "version");
                }
                catch (Exception) { }
            }
            sessions = $"{files.Count} registradas, {alive} vivas";
        }

        var hook = "sin settings.json";
        var settingsFile = Path.Combine(dir, "settings.json");
        if (File.Exists(settingsFile))
        {
            try
            {
                var text = Json.ReadShared(settingsFile);
                hook = text.Contains("ClaudeUsageTray", StringComparison.OrdinalIgnoreCase) && text.Contains("--hook")
                    ? "con hook" : "sin hook";
            }
            catch (Exception) { hook = "settings.json ilegible"; }
        }

        var projects = Directory.Exists(Path.Combine(dir, "projects")) ? "sí" : "no";
        return $"{name}: credenciales {credentials} · sesiones {sessions} · projects {projects} · {hook}"
            + (claudeVersion is null ? "" : $" · Claude Code {claudeVersion}") + ignored;
    }
}
