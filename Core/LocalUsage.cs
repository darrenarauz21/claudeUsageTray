using System.Text.Json;

namespace ClaudeUsageTray;

sealed class LocalTotals
{
    public long Input, Output, CacheWrite, CacheRead;
    public int Responses;
    public Dictionary<string, long> OutputByModel { get; } = new();
    /// <summary>Dos o más cuentas apuntan a la misma carpeta projects (junction o enlace).</summary>
    public bool Shared;
}

/// <summary>
/// Suma los tokens de hoy a partir de los registros .jsonl de Claude Code. Es lo que se gastó
/// en este equipo, no el uso de la cuenta (que incluye claude.ai y otros equipos).
/// </summary>
static class LocalUsage
{
    /// <summary>Las carpetas projects reales, sin repetir las que son un junction a otra.</summary>
    public static (HashSet<string> Roots, bool Shared) ProjectRoots(IEnumerable<string> configDirs)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = 0;
        foreach (var dir in configDirs)
        {
            var projects = new DirectoryInfo(Path.Combine(dir, "projects"));
            if (!projects.Exists) continue;
            candidates++;
            string real;
            try { real = projects.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? projects.FullName; }
            catch (Exception) { real = projects.FullName; }
            roots.Add(Account.Normalize(real));
        }
        return (roots, candidates > roots.Count);
    }

    public static LocalTotals ScanToday(IEnumerable<string> configDirs)
    {
        var totals = new LocalTotals();
        var (roots, shared) = ProjectRoots(configDirs);
        totals.Shared = shared;

        var since = DateTime.Today;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        foreach (var root in roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.jsonl", options).ToList(); }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                try { if (File.GetLastWriteTime(file) < since) continue; }
                catch (Exception) { continue; }
                ScanFile(file, since, seen, totals);
            }
        }
        return totals;
    }

    static void ScanFile(string path, DateTime since, HashSet<string> seen, LocalTotals totals)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!line.Contains("\"usage\"", StringComparison.Ordinal) || !line.Contains("\"assistant\"", StringComparison.Ordinal))
                    continue;
                try { Add(line, since, seen, totals); }
                catch (Exception) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void Add(string line, DateTime since, HashSet<string> seen, LocalTotals totals)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        if (Json.Str(r, "type") != "assistant") return;
        if (Json.Date(r, "timestamp") is not { } when || when.LocalDateTime < since) return;
        if (!r.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return;
        if (!m.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;

        // Una respuesta se escribe en varias líneas (una por bloque) con el mismo id: contarla una vez.
        var key = Json.Str(m, "id") + ":" + Json.Str(r, "requestId");
        if (key != ":" && !seen.Add(key)) return;

        var model = Json.Str(m, "model") ?? "?";
        if (model.StartsWith('<')) return; // <synthetic>: mensajes locales sin llamada al modelo

        var output = (long)Json.Num(u, "output_tokens");
        totals.Input += (long)Json.Num(u, "input_tokens");
        totals.Output += output;
        totals.CacheWrite += (long)Json.Num(u, "cache_creation_input_tokens");
        totals.CacheRead += (long)Json.Num(u, "cache_read_input_tokens");
        totals.Responses++;

        var shortName = model.StartsWith("claude-", StringComparison.Ordinal) ? model[7..] : model;
        totals.OutputByModel[shortName] = totals.OutputByModel.GetValueOrDefault(shortName) + output;
    }
}
