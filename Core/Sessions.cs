using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageTray;

/// <summary>El orden define el orden de la lista: lo que trabaja va primero.</summary>
enum SessionState { Working, Waiting, Failed, Done, Idle }

sealed class SessionInfo
{
    public required Account Account { get; init; }
    public required string SessionId { get; init; }
    public required string Name { get; init; }
    public required string Cwd { get; init; }
    public required SessionState State { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StatusAt { get; init; }
    public string? Title { get; init; }
    public string? LastPrompt { get; init; }
    public string? Action { get; init; }
    public bool AwaitingAnswer { get; init; }
    public int Turns { get; init; }
    public int Steps { get; init; }
    public int TodoDone { get; init; }
    public int TodoTotal { get; init; }
    public DateTimeOffset? TurnStartedAt { get; init; }
    public DateTimeOffset? TurnEndedAt { get; init; }
    public TimeSpan? LastTurn { get; init; }
    /// <summary>Último error de la API que Claude Code mostró en la sesión (ya sin reintentos).</summary>
    public DateTimeOffset? ErrorAt { get; init; }
    public string? ErrorText { get; init; }
    /// <summary>Último reintento automático tras un error de la API.</summary>
    public DateTimeOffset? RetryAt { get; init; }
    public int RetryAttempt { get; init; }
    public int MaxRetries { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Name : Title;
}

/// <summary>
/// Lee las sesiones abiertas de Claude Code: el registro sessions/&lt;pid&gt;.json de cada carpeta de
/// configuración y, de forma incremental, el .jsonl de cada sesión. Solo lectura.
/// </summary>
sealed class SessionMonitor
{
    /// <summary>Tras este tiempo sin actividad, una sesión terminada pasa a inactiva.</summary>
    static readonly TimeSpan DoneWindow = TimeSpan.FromMinutes(30);

    readonly Dictionary<string, Transcript> _transcripts = new(StringComparer.OrdinalIgnoreCase);

    public List<SessionInfo> Scan(IEnumerable<Account> accounts)
    {
        var result = new List<SessionInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in accounts)
        {
            var dir = Path.Combine(account.ConfigDir, "sessions");
            IEnumerable<string> files;
            try { files = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json").ToList() : []; }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                try
                {
                    using var doc = JsonDocument.Parse(Json.ReadShared(file));
                    var r = doc.RootElement;
                    var pid = r.TryGetProperty("pid", out var p) && p.TryGetInt32(out var value) ? value : 0;
                    var id = Json.Str(r, "sessionId");
                    if (pid == 0 || id is null || !IsAlive(pid, Json.Str(r, "procStart")) || !seen.Add(id)) continue;
                    result.Add(Build(account, r, id, Update(account, id)));
                }
                catch (Exception) { }
            }
        }

        foreach (var gone in _transcripts.Keys.Where(k => !seen.Contains(k)).ToList())
            _transcripts.Remove(gone);

        return result
            .OrderBy(s => s.State)
            .ThenByDescending(s => s.TurnEndedAt ?? s.StatusAt ?? s.StartedAt)
            .ToList();
    }

    static SessionInfo Build(Account account, JsonElement r, string id, Transcript t)
    {
        var status = Json.Str(r, "status") ?? "";
        var statusAt = Millis(r, "statusUpdatedAt") ?? Millis(r, "updatedAt");
        var awaiting = t.PendingTool == "AskUserQuestion";
        var lastActivity = t.TurnEndedAt ?? statusAt;
        // El turno terminó en error si el error es posterior al último pedido.
        var failed = t.ErrorAt is { } errorAt && (t.TurnStartedAt is null || errorAt >= t.TurnStartedAt)
            && DateTimeOffset.Now - errorAt < DoneWindow;

        var state = status switch
        {
            _ when awaiting => SessionState.Waiting,
            "busy" => SessionState.Working,
            "idle" when failed => SessionState.Failed,
            "idle" => lastActivity is { } at && DateTimeOffset.Now - at < DoneWindow ? SessionState.Done : SessionState.Idle,
            _ when status.Contains("wait") || status.Contains("permission") || status.Contains("input") => SessionState.Waiting,
            _ => SessionState.Idle,
        };

        return new SessionInfo
        {
            Account = account,
            SessionId = id,
            Name = Json.Str(r, "name") ?? id[..8],
            Cwd = Json.Str(r, "cwd") ?? "",
            State = state,
            StartedAt = Millis(r, "startedAt"),
            StatusAt = statusAt,
            Title = t.Title,
            LastPrompt = t.LastPrompt,
            Action = t.Action,
            AwaitingAnswer = awaiting,
            Turns = t.Turns,
            Steps = t.Steps,
            TodoDone = t.TodoDone,
            TodoTotal = t.TodoTotal,
            TurnStartedAt = t.TurnStartedAt,
            TurnEndedAt = t.TurnEndedAt,
            LastTurn = t.LastTurn,
            ErrorAt = t.ErrorAt,
            ErrorText = t.ErrorText,
            RetryAt = t.RetryAt,
            RetryAttempt = t.RetryAttempt,
            MaxRetries = t.MaxRetries,
        };
    }

    /// <summary>
    /// El pid sigue vivo y es el mismo proceso. procStart no está documentado: en Windows es la hora de
    /// inicio en FILETIME; en Linux y macOS se prueban también milisegundos y segundos Unix y los ticks
    /// de /proc/&lt;pid&gt;/stat. Si el formato no se reconoce, basta con que el proceso exista.
    /// </summary>
    internal static bool IsAlive(int pid, string? procStart)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (procStart is null || !long.TryParse(procStart, out var expected)) return true;

            DateTime started;
            try { started = process.StartTime.ToUniversalTime(); }
            catch (Exception) { return true; }

            if (expected > 100_000_000_000_000_000) // FILETIME (Windows)
                return Math.Abs(started.ToFileTimeUtc() - expected) < TimeSpan.TicksPerSecond;
            if (expected is > 1_000_000_000_000 and < 100_000_000_000_000) // milisegundos Unix
                return Math.Abs(new DateTimeOffset(started).ToUnixTimeMilliseconds() - expected) < 2000;
            if (OperatingSystem.IsLinux() && LinuxStartTicks(pid) is { } ticks && ticks == expected) return true;
            if (expected is > 1_000_000_000 and < 10_000_000_000) // segundos Unix
                return Math.Abs(new DateTimeOffset(started).ToUnixTimeSeconds() - expected) <= 2;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Campo 22 de /proc/&lt;pid&gt;/stat: inicio del proceso en ticks desde el arranque.</summary>
    static long? LinuxStartTicks(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return long.TryParse(fields[19], out var ticks) ? ticks : null;
        }
        catch (Exception) { return null; }
    }

    static DateTimeOffset? Millis(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    Transcript Update(Account account, string id)
    {
        if (!_transcripts.TryGetValue(id, out var t))
            _transcripts[id] = t = new Transcript();

        if (t.Path is null && DateTime.Now >= t.NextLookup)
        {
            t.Path = Find(account, id);
            t.NextLookup = DateTime.Now.AddSeconds(10);
        }
        if (t.Path is not null) t.ReadNew();
        return t;
    }

    static string? Find(Account account, string id)
    {
        var root = Path.Combine(account.ConfigDir, "projects");
        try
        {
            if (!Directory.Exists(root)) return null;
            foreach (var sub in Directory.EnumerateDirectories(root))
            {
                var file = Path.Combine(sub, id + ".jsonl");
                if (File.Exists(file)) return file;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Lo que se sabe de una sesión leyendo su registro; cada lectura sigue donde quedó la anterior.</summary>
    sealed class Transcript
    {
        public string? Path;
        public DateTime NextLookup;
        long _offset;

        public string? Title, LastPrompt, Action, PendingTool;
        public int Turns, Steps, TodoDone, TodoTotal;
        public DateTimeOffset? TurnStartedAt, TurnEndedAt, ErrorAt, RetryAt;
        public TimeSpan? LastTurn;
        public string? ErrorText;
        public int RetryAttempt, MaxRetries;

        public void ReadNew()
        {
            try
            {
                using var fs = new FileStream(Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < _offset) Reset();
                if (fs.Length == _offset) return;

                fs.Seek(_offset, SeekOrigin.Begin);
                var buffer = new byte[fs.Length - _offset];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = fs.Read(buffer, read, buffer.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                // Solo líneas completas: la última puede estar a medio escribir.
                var end = read == 0 ? -1 : Array.LastIndexOf(buffer, (byte)'\n', read - 1);
                if (end < 0) return;
                _offset += end + 1;

                foreach (var line in Encoding.UTF8.GetString(buffer, 0, end).Split('\n'))
                    if (line.Length > 1) Apply(line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void Reset()
        {
            _offset = 0;
            Title = LastPrompt = Action = PendingTool = null;
            Turns = Steps = TodoDone = TodoTotal = 0;
            TurnStartedAt = TurnEndedAt = ErrorAt = RetryAt = null;
            LastTurn = null;
            ErrorText = null;
            RetryAttempt = MaxRetries = 0;
        }

        void Apply(string line)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { return; }

            using (doc)
            {
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) return;
                if (r.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return;

                switch (Json.Str(r, "type"))
                {
                    case "ai-title":
                        Title = Json.Str(r, "aiTitle") ?? Title;
                        break;
                    case "last-prompt":
                        LastPrompt = Json.Str(r, "lastPrompt") ?? LastPrompt;
                        break;
                    case "system" when Json.Str(r, "subtype") == "turn_duration":
                        TurnEndedAt = Json.Date(r, "timestamp");
                        LastTurn = TimeSpan.FromMilliseconds(Json.Num(r, "durationMs"));
                        PendingTool = null;
                        break;
                    case "system" when Json.Str(r, "subtype") == "api_error":
                        RetryAt = Json.Date(r, "timestamp");
                        RetryAttempt = (int)Json.Num(r, "retryAttempt");
                        MaxRetries = (int)Json.Num(r, "maxRetries");
                        break;
                    case "user":
                        ApplyUser(r);
                        break;
                    case "assistant":
                        ApplyAssistant(r);
                        break;
                }
            }
        }

        void ApplyUser(JsonElement r)
        {
            if (r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return;
            if (!r.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object
                || !m.TryGetProperty("content", out var content)) return;

            if (content.ValueKind == JsonValueKind.String)
            {
                if (content.GetString()?.StartsWith("<local-command", StringComparison.Ordinal) == true) return;
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                var hasText = false;
                foreach (var item in content.EnumerateArray())
                {
                    var type = Json.Str(item, "type");
                    if (type == "tool_result") { PendingTool = null; return; } // llegó el resultado de una herramienta
                    if (type == "text") hasText = true;
                }
                if (!hasText) return;
            }
            else return;

            // Un pedido nuevo de la persona: empieza un turno.
            Turns++;
            Steps = 0;
            Action = null;
            PendingTool = null;
            TurnStartedAt = Json.Date(r, "timestamp");
        }

        void ApplyAssistant(JsonElement r)
        {
            if (!r.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object
                || !m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;

            // Error que Claude Code mostró tras agotar los reintentos.
            if (r.TryGetProperty("isApiErrorMessage", out var apiError) && apiError.ValueKind == JsonValueKind.True)
            {
                ErrorAt = Json.Date(r, "timestamp");
                var text = content.EnumerateArray().Select(i => Json.Str(i, "text")).FirstOrDefault(s => s is not null);
                ErrorText = (text ?? Json.Str(r, "error") ?? "Error de la API").ReplaceLineEndings(" ");
                if (ErrorText.Length > 160) ErrorText = ErrorText[..160] + "…";
                PendingTool = null;
                return;
            }

            foreach (var item in content.EnumerateArray())
            {
                if (Json.Str(item, "type") != "tool_use") continue;
                var name = Json.Str(item, "name") ?? "herramienta";
                item.TryGetProperty("input", out var input);
                Steps++;
                PendingTool = name;
                Action = Describe(name, input);

                if (name == "TodoWrite" && input.ValueKind == JsonValueKind.Object
                    && input.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array)
                {
                    TodoTotal = todos.GetArrayLength();
                    TodoDone = todos.EnumerateArray().Count(td => Json.Str(td, "status") == "completed");
                }
            }
        }
    }

    static string Describe(string tool, JsonElement input)
    {
        string? detail = null;
        if (input.ValueKind == JsonValueKind.Object)
        {
            var file = Json.Str(input, "file_path") ?? Json.Str(input, "notebook_path");
            detail = Json.Str(input, "description")
                ?? (file is null ? null : System.IO.Path.GetFileName(file))
                ?? Json.Str(input, "pattern")
                ?? Json.Str(input, "query")
                ?? Json.Str(input, "skill")
                ?? Json.Str(input, "url");
        }

        var verb = tool switch
        {
            "Bash" or "PowerShell" => "Ejecutando",
            "Read" => "Leyendo",
            "Edit" or "NotebookEdit" => "Editando",
            "Write" => "Escribiendo",
            "Grep" or "Glob" => "Buscando",
            "WebSearch" => "Buscando en la web",
            "WebFetch" => "Consultando",
            "Agent" or "Task" => "Delegando",
            "Skill" => "Usando la skill",
            "AskUserQuestion" => "Esperando su respuesta",
            "TodoWrite" => "Actualizando tareas",
            _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => "Usando " + tool.Split("__").Last().Replace('_', ' '),
            _ => tool,
        };
        if (tool == "AskUserQuestion") return verb;
        return detail is null ? verb : $"{verb} · {detail.ReplaceLineEndings(" ")}";
    }
}
