using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsageTray;

/// <summary>
/// Pone y quita los hooks de esta app en el settings.json de una carpeta de Claude Code, sin tocar el
/// resto del archivo: Notification (avisos de permisos) y PermissionRequest (permisos y preguntas
/// respondidos desde la isla, que espera hasta 120 s). Antes de cambiarlo guarda un respaldo junto a él.
/// </summary>
static class HookInstaller
{
    public enum State { Missing, Installed, Outdated, Unreadable }

    static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Command(string exePath) => $"\"{exePath}\" --hook";

    /// <summary>El hook de los permisos y las preguntas en la isla.</summary>
    public static string ApprovalsCommand(string exePath) => $"\"{exePath}\" --hook --permiso";

    const string ApprovalsEvent = "PermissionRequest";

    /// <summary>Lo mismo que Status, para el hook de los permisos en la isla.</summary>
    public static State ApprovalsStatus(string configDir, string exePath)
    {
        var path = SettingsPath(configDir);
        if (!File.Exists(path)) return State.Missing;
        try
        {
            var commands = OurCommands(Parse(File.ReadAllText(path)), ApprovalsEvent).ToList();
            if (commands.Count == 0) return State.Missing;
            return commands.Any(c => string.Equals(c, ApprovalsCommand(exePath), StringComparison.OrdinalIgnoreCase))
                ? State.Installed : State.Outdated;
        }
        catch (Exception) { return State.Unreadable; }
    }

    /// <summary>Deja un único hook de permisos de la app apuntando a este .exe, con 120 s para que la persona responda.</summary>
    public static string? InstallApprovals(string configDir, string exePath) => Edit(configDir, root =>
    {
        RemoveOurs(root, ApprovalsEvent);
        if (root["hooks"] is not JsonObject hooks) root["hooks"] = hooks = new JsonObject();
        if (hooks[ApprovalsEvent] is not JsonArray groups) hooks[ApprovalsEvent] = groups = new JsonArray();
        groups.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command", ["command"] = ApprovalsCommand(exePath), ["timeout"] = Approvals.DecisionSeconds + 10,
            }),
        });
    });

    public static string SettingsPath(string configDir) => Path.Combine(configDir, "settings.json");

    /// <summary>Installed: apunta a este .exe. Outdated: hay un hook de la app, pero a otra ruta.</summary>
    public static State Status(string configDir, string exePath)
    {
        var path = SettingsPath(configDir);
        if (!File.Exists(path)) return State.Missing;
        try
        {
            var commands = OurCommands(Parse(File.ReadAllText(path)), "Notification").ToList();
            if (commands.Count == 0) return State.Missing;
            return commands.Any(c => string.Equals(c, Command(exePath), StringComparison.OrdinalIgnoreCase))
                ? State.Installed : State.Outdated;
        }
        catch (Exception) { return State.Unreadable; }
    }

    /// <summary>Deja un único hook de la app apuntando a este .exe (corrige uno viejo si lo hay).</summary>
    public static string? Install(string configDir, string exePath) => Edit(configDir, root =>
    {
        RemoveOurs(root, "Notification");
        if (root["hooks"] is not JsonObject hooks) root["hooks"] = hooks = new JsonObject();
        if (hooks["Notification"] is not JsonArray groups) hooks["Notification"] = groups = new JsonArray();
        groups.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Command(exePath) }),
        });
    });

    /// <summary>Quita solo el hook de los permisos en la isla.</summary>
    public static string? UninstallApprovals(string configDir) =>
        File.Exists(SettingsPath(configDir)) ? Edit(configDir, root => RemoveOurs(root, ApprovalsEvent)) : null;

    /// <summary>Quita los dos hooks de la app (avisos y permisos en la isla).</summary>
    public static string? Uninstall(string configDir) =>
        File.Exists(SettingsPath(configDir)) ? Edit(configDir, root => { RemoveOurs(root, "Notification"); RemoveOurs(root, ApprovalsEvent); }) : null;

    /// <summary>Aplica el cambio con respaldo y escritura atómica. Devuelve la ruta del respaldo.</summary>
    static string? Edit(string configDir, Action<JsonObject> change)
    {
        var path = SettingsPath(configDir);
        var exists = File.Exists(path);
        var root = (exists ? Parse(File.ReadAllText(path)) : new JsonObject()) as JsonObject
            ?? throw new InvalidDataException("settings.json no contiene un objeto JSON.");

        change(root);
        var output = root.ToJsonString(WriteOptions);
        Parse(output); // si el resultado no es JSON válido, no se escribe nada

        string? backup = null;
        if (exists)
        {
            backup = $"{path}.respaldo-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: true);
        }
        Directory.CreateDirectory(configDir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, output + Environment.NewLine);
        File.Move(temp, path, overwrite: true);
        return backup;
    }

    static void RemoveOurs(JsonObject root, string hookEvent)
    {
        if (root["hooks"] is not JsonObject hooks || hooks[hookEvent] is not JsonArray groups) return;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i]?["hooks"] is not JsonArray list) continue;
            for (var j = list.Count - 1; j >= 0; j--)
                if (IsOurs(list[j])) list.RemoveAt(j);
            if (list.Count == 0) groups.RemoveAt(i);
        }
        if (groups.Count == 0) hooks.Remove(hookEvent);
        if (hooks.Count == 0) root.Remove("hooks");
    }

    static IEnumerable<string> OurCommands(JsonNode? root, string hookEvent)
    {
        if (root?["hooks"]?[hookEvent] is not JsonArray groups) yield break;
        foreach (var group in groups)
            if (group?["hooks"] is JsonArray list)
                foreach (var hook in list)
                    if (IsOurs(hook)) yield return CommandOf(hook)!;
    }

    static bool IsOurs(JsonNode? hook) =>
        CommandOf(hook) is { } command
        && command.Contains("ClaudeUsageTray", StringComparison.OrdinalIgnoreCase)
        && command.Contains("--hook", StringComparison.Ordinal);

    static string? CommandOf(JsonNode? hook) =>
        hook?["command"] is JsonValue value && value.TryGetValue<string>(out var command) ? command : null;

    static JsonNode? Parse(string text) => JsonNode.Parse(text, documentOptions: ReadOptions);
}
