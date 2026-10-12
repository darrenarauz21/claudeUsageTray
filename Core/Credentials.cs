using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeUsageTray;

/// <summary>
/// Credenciales de Claude Code de una carpeta de configuración. En Windows y Linux están en
/// .credentials.json; en macOS, en el Llavero: «Claude Code-credentials» para la carpeta por
/// defecto y, desde Claude Code 2.1.144, «Claude Code-credentials-&lt;8 primeros caracteres del
/// SHA-256 de la ruta&gt;» para cada CLAUDE_CONFIG_DIR. La app solo las lee.
/// </summary>
static class Credentials
{
    const string KeychainService = "Claude Code-credentials";

    // Consultar el Llavero lanza un proceso: se guarda el resultado un minuto por carpeta.
    static readonly Dictionary<string, (DateTime At, string? Json)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string FilePath(string configDir) => Path.Combine(configDir, ".credentials.json");

    public static string? ReadJson(string configDir)
    {
        var file = FilePath(configDir);
        if (File.Exists(file)) return Json.ReadShared(file);
        return OperatingSystem.IsMacOS() ? Keychain(configDir) : null;
    }

    public static bool Exists(string configDir) =>
        File.Exists(FilePath(configDir)) || (OperatingSystem.IsMacOS() && Keychain(configDir) is not null);

    /// <summary>Dónde están, para el diagnóstico: «archivo», «Llavero» o «no».</summary>
    public static string Describe(string configDir) =>
        File.Exists(FilePath(configDir)) ? "archivo"
        : OperatingSystem.IsMacOS() && Keychain(configDir) is not null ? "Llavero"
        : "no";

    static string? Keychain(string configDir)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(configDir, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(1))
                return hit.Json;
        }

        string? json = null;
        foreach (var service in Services(configDir))
        {
            json = FindPassword(service);
            if (json is not null) break;
        }
        lock (Cache) Cache[configDir] = (DateTime.UtcNow, json);
        return json;
    }

    /// <summary>La carpeta por defecto usa el nombre simple; las demás, el nombre con el hash (y el simple
    /// como respaldo, porque antes de 2.1.144 todas compartían esa entrada).</summary>
    static IEnumerable<string> Services(string configDir)
    {
        var path = Path.GetFullPath(configDir).TrimEnd('/');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8].ToLowerInvariant();
        var isDefault = Account.SamePath(path, Account.DefaultDir);
        if (isDefault) { yield return KeychainService; yield return $"{KeychainService}-{hash}"; }
        else { yield return $"{KeychainService}-{hash}"; yield return KeychainService; }
    }

    static string? FindPassword(string service)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/security")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            start.ArgumentList.Add("find-generic-password");
            start.ArgumentList.Add("-s");
            start.ArgumentList.Add(service);
            var user = Environment.GetEnvironmentVariable("USER");
            if (!string.IsNullOrEmpty(user)) { start.ArgumentList.Add("-a"); start.ArgumentList.Add(user); }
            start.ArgumentList.Add("-w");

            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000)) { process.Kill(); return null; }
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception) { return null; }
    }
}
