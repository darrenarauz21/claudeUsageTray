using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageTray;

/// <summary>Resumen de un día. Solo cifras: nunca guarda texto de las conversaciones.</summary>
sealed class DayRecord
{
    /// <summary>Entrada + escritura en caché + salida. Las lecturas de caché van aparte.</summary>
    public long Tokens { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public int Responses { get; set; }
    public int Prompts { get; set; }
    public int ToolCalls { get; set; }
    public int Sessions { get; set; }
    public Dictionary<string, long> Models { get; set; } = new();
}

sealed class ActivitySummary
{
    public required IReadOnlyDictionary<DateOnly, DayRecord> Days { get; init; }

    public DayRecord? Get(DateOnly day) => Days.TryGetValue(day, out var record) ? record : null;
    public DateOnly? FirstDay => Days.Count == 0 ? null : Days.Keys.Min();
}

sealed record ActivityStats(long Tokens, int ActiveDays, int CurrentStreak, int LongestStreak, DateOnly? BusiestDay, string? FavoriteModel)
{
    public static ActivityStats Compute(ActivitySummary summary, DateOnly from, DateOnly to)
    {
        var range = summary.Days.Where(kv => kv.Key >= from && kv.Key <= to && kv.Value.Tokens > 0).ToList();
        var models = new Dictionary<string, long>();
        foreach (var (_, day) in range)
            foreach (var (model, tokens) in day.Models)
                models[model] = models.GetValueOrDefault(model) + tokens;

        // Racha actual: si hoy aún no hay actividad, cuenta desde ayer.
        bool Active(DateOnly d) => summary.Get(d)?.Tokens > 0;
        var cursor = Active(to) ? to : to.AddDays(-1);
        var current = 0;
        while (Active(cursor)) { current++; cursor = cursor.AddDays(-1); }

        var longest = 0;
        var run = 0;
        DateOnly? previous = null;
        foreach (var day in summary.Days.Where(kv => kv.Value.Tokens > 0).Select(kv => kv.Key).OrderBy(d => d))
        {
            run = previous is { } p && p.AddDays(1) == day ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = day;
        }

        return new ActivityStats(
            range.Sum(kv => kv.Value.Tokens),
            range.Count,
            current,
            longest,
            range.Count == 0 ? null : range.MaxBy(kv => kv.Value.Tokens).Key,
            models.Count == 0 ? null : models.MaxBy(kv => kv.Value).Key);
    }
}

/// <summary>
/// Suma la actividad por día leyendo todos los .jsonl de Claude Code, de forma incremental, y la
/// guarda en %APPDATA%\ClaudeUsageTray\history.json para no perder los días cuyos registros
/// borra Claude Code (por defecto, a los 30 días).
/// </summary>
sealed class ActivityTracker
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray", "history.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly Dictionary<DateOnly, Day> _computed = new();
    Dictionary<DateOnly, DayRecord> _stored = Load();

    sealed class Day
    {
        public long Tokens, Output, CacheRead;
        public int Responses, Prompts, ToolCalls;
        public readonly HashSet<string> Sessions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Models = new();

        public DayRecord ToRecord() => new()
        {
            Tokens = Tokens, Output = Output, CacheRead = CacheRead, Responses = Responses, Prompts = Prompts,
            ToolCalls = ToolCalls, Sessions = Sessions.Count, Models = new Dictionary<string, long>(Models),
        };
    }

    public ActivitySummary Scan(IEnumerable<string> configDirs)
    {
        var (roots, _) = LocalUsage.ProjectRoots(configDirs);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in roots)
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(root, "*.jsonl", options).ToList(); }
            catch (Exception) { continue; }
            foreach (var file in files) ReadNew(file);
        }

        // Lo recalculado manda, salvo que el registro ya se haya borrado en parte (queda lo guardado).
        var merged = new Dictionary<DateOnly, DayRecord>(_stored);
        var changed = false;
        foreach (var (day, acc) in _computed)
        {
            var fresh = acc.ToRecord();
            if (merged.TryGetValue(day, out var old) && (fresh.Tokens < old.Tokens
                || (fresh.Tokens == old.Tokens && fresh.Responses <= old.Responses && fresh.Sessions <= old.Sessions)))
                continue;
            merged[day] = fresh;
            changed = true;
        }
        if (changed)
        {
            _stored = merged;
            Save(merged);
        }
        return new ActivitySummary { Days = merged };
    }

    void ReadNew(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = _offsets.GetValueOrDefault(path);
            if (fs.Length < offset) offset = 0; // reescrito: se lee de nuevo; los duplicados los descarta _seen
            if (fs.Length == offset) return;

            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - offset];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            var end = read == 0 ? -1 : Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (end < 0) return;
            _offsets[path] = offset + end + 1;

            foreach (var line in Encoding.UTF8.GetString(buffer, 0, end).Split('\n'))
            {
                if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)) Apply(line, assistant: true);
                // Los resultados de herramientas pesan mucho y no son pedidos: ni se analizan.
                else if (line.Contains("\"type\":\"user\"", StringComparison.Ordinal)
                    && !line.Contains("\"tool_result\"", StringComparison.Ordinal)) Apply(line, assistant: false);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void Apply(string line, bool assistant)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }

        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Json.Date(r, "timestamp") is not { } at) return;
            if (!r.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return;

            var date = DateOnly.FromDateTime(at.LocalDateTime);
            if (!_computed.TryGetValue(date, out var day)) _computed[date] = day = new Day();
            var session = Json.Str(r, "sessionId");

            if (assistant) ApplyAssistant(r, m, day, session);
            else ApplyUser(r, m, day, session);
        }
    }

    void ApplyAssistant(JsonElement r, JsonElement m, Day day, string? session)
    {
        // Una respuesta se escribe en varias líneas (una por bloque): las herramientas se cuentan
        // por su id y los tokens una sola vez por respuesta.
        if (m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var item in content.EnumerateArray())
                if (Json.Str(item, "type") == "tool_use" && Json.Str(item, "id") is { } toolId && _seen.Add("t:" + toolId))
                    day.ToolCalls++;

        if (!m.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
        var model = Json.Str(m, "model") ?? "?";
        if (model.StartsWith('<')) return;
        var key = "m:" + Json.Str(m, "id") + ":" + Json.Str(r, "requestId");
        if (key != "m::" && !_seen.Add(key)) return;

        var output = (long)Json.Num(u, "output_tokens");
        var tokens = (long)Json.Num(u, "input_tokens") + (long)Json.Num(u, "cache_creation_input_tokens") + output;
        day.Tokens += tokens;
        day.Output += output;
        day.CacheRead += (long)Json.Num(u, "cache_read_input_tokens");
        day.Responses++;
        var shortName = model.StartsWith("claude-", StringComparison.Ordinal) ? model[7..] : model;
        day.Models[shortName] = day.Models.GetValueOrDefault(shortName) + tokens;
        if (session is not null) day.Sessions.Add(session);
    }

    void ApplyUser(JsonElement r, JsonElement m, Day day, string? session)
    {
        if (r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return;
        if (r.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return;
        if (!m.TryGetProperty("content", out var content)) return;
        var isPrompt = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString()?.StartsWith("<local-command", StringComparison.Ordinal) != true,
            JsonValueKind.Array => content.EnumerateArray().Any(i => Json.Str(i, "type") == "text"),
            _ => false,
        };
        if (!isPrompt || Json.Str(r, "uuid") is not { } uuid || !_seen.Add("u:" + uuid)) return;
        day.Prompts++;
        if (session is not null) day.Sessions.Add(session);
    }

    static Dictionary<DateOnly, DayRecord> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var raw = JsonSerializer.Deserialize<Dictionary<string, DayRecord>>(File.ReadAllText(FilePath), Options);
            var result = new Dictionary<DateOnly, DayRecord>();
            foreach (var (key, value) in raw ?? new())
                if (DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    result[day] = value;
            return result;
        }
        catch (Exception) { return new(); }
    }

    static void Save(Dictionary<DateOnly, DayRecord> days)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var raw = days.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), kv => kv.Value);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(raw, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception) { }
    }
}
