using System.Text;
using System.Text.Json;

namespace ClaudeUsageTray;

enum NoticeKind { Done, Question, Permission, Failed, Retrying, Closed, UsageHigh, UsageCritical, UsageReset, Expired }

sealed record Notice(NoticeKind Kind, string Title, string Subtitle, string Detail, string? SessionId = null, string? AccountDir = null)
{
    /// <summary>Lo que necesita su atención se queda hasta que lo cierre; lo demás se va a los 6 s.</summary>
    public bool Sticky => Kind is NoticeKind.Question or NoticeKind.Permission or NoticeKind.Failed
        or NoticeKind.Closed or NoticeKind.Expired;
}

/// <summary>
/// Compara cada lectura con la anterior y devuelve los avisos de lo que cambió. La primera lectura
/// solo fija la referencia: al arrancar no avisa de lo que ya pasó.
/// </summary>
sealed class NoticeDetector
{
    /// <summary>Archivo donde el hook de Claude Code («--hook») deja los eventos de notificación.</summary>
    public static readonly string EventsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray", "events.jsonl");

    Dictionary<string, SessionInfo>? _sessions;
    readonly Dictionary<string, double> _percents = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _expired = new(StringComparer.OrdinalIgnoreCase);
    bool _usageReady;
    long _eventsOffset = -1;

    /// <summary>Olvida la referencia (al encender las notificaciones, para no avisar de lo viejo).</summary>
    public void Reset()
    {
        _sessions = null;
        _eventsOffset = -1;
    }

    public List<Notice> Sessions(IReadOnlyList<SessionInfo> now)
    {
        var notices = new List<Notice>();
        var previous = _sessions;
        _sessions = now.ToDictionary(s => s.SessionId, StringComparer.OrdinalIgnoreCase);
        if (previous is null) return notices;

        foreach (var s in now)
        {
            if (!previous.TryGetValue(s.SessionId, out var before)) continue;
            var title = s.DisplayTitle;
            var where = $"{s.Account.Label} · {Path.GetFileName(s.Cwd.TrimEnd('\\', '/'))}";

            if (s.ErrorAt is not null && s.ErrorAt != before.ErrorAt)
                notices.Add(new Notice(NoticeKind.Failed, "Falló", title, s.ErrorText ?? "Error de la API", s.SessionId));
            else if (s.TurnEndedAt is not null && s.TurnEndedAt != before.TurnEndedAt)
            {
                var detail = new List<string>();
                if (s.LastTurn is { } d) detail.Add(Format.Duration(d));
                if (s.Steps > 0) detail.Add(s.Steps == 1 ? "1 paso" : $"{s.Steps} pasos");
                detail.Add(s.Account.Label);
                notices.Add(new Notice(NoticeKind.Done, "Terminó", title, string.Join(" · ", detail), s.SessionId));
            }

            if (s.AwaitingAnswer && !before.AwaitingAnswer)
                notices.Add(new Notice(NoticeKind.Question, "Le está preguntando", title, where, s.SessionId));

            // Un reintento aislado es normal; se avisa una vez cuando ya van tres.
            if (s.RetryAt != before.RetryAt && s.RetryAttempt >= 3 && (before.RetryAttempt < 3 || before.RetryAt is null))
                notices.Add(new Notice(NoticeKind.Retrying, "Reintentando", title,
                    $"Intento {s.RetryAttempt} de {Math.Max(s.RetryAttempt, s.MaxRetries)} · {s.Account.Label}", s.SessionId));
        }

        foreach (var gone in previous.Values.Where(p => p.State == SessionState.Working && !_sessions.ContainsKey(p.SessionId)))
            notices.Add(new Notice(NoticeKind.Closed, "Se cerró mientras trabajaba", gone.DisplayTitle,
                $"{gone.Account.Label} · {Path.GetFileName(gone.Cwd.TrimEnd('\\', '/'))}", gone.SessionId));

        return notices;
    }

    public List<Notice> Usage(IEnumerable<Account> accounts, IReadOnlyDictionary<string, UsageSnapshot> snapshots)
    {
        var notices = new List<Notice>();
        foreach (var account in accounts)
        {
            if (!snapshots.TryGetValue(account.ConfigDir, out var snap)) continue;

            var expired = snap.Error?.StartsWith("Sesión vencida", StringComparison.Ordinal) == true;
            if (expired && _expired.Add(account.ConfigDir) && _usageReady)
                notices.Add(new Notice(NoticeKind.Expired, "Sesión vencida", account.Label,
                    "Abra Claude Code con esta cuenta", AccountDir: account.ConfigDir));
            if (!expired) _expired.Remove(account.ConfigDir);
            if (snap.Error is not null) continue;

            foreach (var limit in snap.Limits.Where(l => l.Kind is "session" or "weekly_all"))
            {
                var key = account.ConfigDir + "|" + limit.Kind;
                var window = limit.Kind == "session" ? "Sesión de 5 h" : "Semana";
                var subtitle = $"{window} · {account.Label}";
                if (_usageReady && _percents.TryGetValue(key, out var before))
                {
                    if (before >= 95 && limit.Percent < before - 20)
                        notices.Add(new Notice(NoticeKind.UsageReset, "Límite reiniciado", subtitle,
                            "Ya puede volver a usarlo", AccountDir: account.ConfigDir));
                    else if (limit.Percent >= 95 && before < 95)
                        notices.Add(new Notice(NoticeKind.UsageCritical, $"Uso al {limit.Percent:0} %", subtitle,
                            Format.Reset(limit.ResetsAt), AccountDir: account.ConfigDir));
                    else if (limit.Percent >= 80 && before < 80)
                        notices.Add(new Notice(NoticeKind.UsageHigh, $"Uso al {limit.Percent:0} %", subtitle,
                            Format.Reset(limit.ResetsAt), AccountDir: account.ConfigDir));
                }
                _percents[key] = limit.Percent;
            }
        }
        _usageReady = true;
        return notices;
    }

    /// <summary>Eventos nuevos del hook Notification de Claude Code (avisos de permisos).</summary>
    public List<Notice> HookEvents()
    {
        var notices = new List<Notice>();
        try
        {
            if (!File.Exists(EventsPath)) { _eventsOffset = Math.Max(_eventsOffset, 0); return notices; }
            using var fs = new FileStream(EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (_eventsOffset < 0 || _eventsOffset > fs.Length) { _eventsOffset = fs.Length; return notices; }
            if (fs.Length == _eventsOffset) return notices;
            fs.Seek(_eventsOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var end = text.LastIndexOf('\n');
            if (end < 0) return notices;
            _eventsOffset += Encoding.UTF8.GetByteCount(text[..(end + 1)]);

            foreach (var line in text[..end].Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var message = Json.Str(r, "message") ?? "";
                var type = Json.Str(r, "notification_type") ?? "";
                var isPermission = type.Contains("permission", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("permission", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("permiso", StringComparison.OrdinalIgnoreCase);
                if (!isPermission) continue; // «esperando su respuesta» ya lo cubren Terminó y Le pregunta

                var id = Json.Str(r, "session_id");
                var title = id is not null && _sessions?.TryGetValue(id, out var s) == true
                    ? s.DisplayTitle
                    : Path.GetFileName((Json.Str(r, "cwd") ?? "Claude Code").TrimEnd('\\', '/'));
                notices.Add(new Notice(NoticeKind.Permission, "Necesita su permiso", title, message, id));
            }
        }
        catch (Exception) { }
        return notices;
    }

    /// <summary>Lo que ejecuta Claude Code como hook: guarda el evento y termina, sin escribir nada.</summary>
    public static int RunHook()
    {
        try
        {
            var input = Console.In.ReadToEnd();
            using var doc = JsonDocument.Parse(input);
            var r = doc.RootElement;
            var line = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["at"] = DateTimeOffset.Now.ToString("O"),
                ["event"] = Json.Str(r, "hook_event_name"),
                ["notification_type"] = Json.Str(r, "notification_type"),
                ["message"] = Json.Str(r, "message"),
                ["session_id"] = Json.Str(r, "session_id"),
                ["cwd"] = Json.Str(r, "cwd"),
            });
            Directory.CreateDirectory(Path.GetDirectoryName(EventsPath)!);
            using var fs = new FileStream(EventsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            fs.Write(bytes, 0, bytes.Length);
        }
        catch (Exception) { }
        return 0;
    }

    /// <summary>Un aviso de cada tipo, para verlos sin esperar a que pasen.</summary>
    public static IEnumerable<Notice> Samples() =>
    [
        new(NoticeKind.Done, "Terminó", "Monitor de uso de Claude", "2 min 25 s · 14 pasos · personal"),
        new(NoticeKind.Question, "Le está preguntando", "Agregar pruebas al inicio de sesión", "principal · mi-proyecto"),
        new(NoticeKind.Permission, "Necesita su permiso", "Ordenar las ramas del repositorio", "Claude needs your permission to use Bash"),
        new(NoticeKind.Failed, "Falló", "Resumen del informe mensual", "API Error: Can't reach the API server — check your internet or DNS"),
        new(NoticeKind.Retrying, "Reintentando", "Resumen del informe mensual", "Intento 3 de 10 · principal"),
        new(NoticeKind.Closed, "Se cerró mientras trabajaba", "Corregir el cálculo de totales", "principal · mi-proyecto"),
        new(NoticeKind.UsageHigh, "Uso al 82 %", "Sesión de 5 h · personal", "Reinicia en 1 h 20 min"),
        new(NoticeKind.UsageCritical, "Uso al 96 %", "Semana · principal", "Reinicia el jueves, 6:00 a. m."),
        new(NoticeKind.UsageReset, "Límite reiniciado", "Sesión de 5 h · personal", "Ya puede volver a usarlo"),
        new(NoticeKind.Expired, "Sesión vencida", "personal", "Abra Claude Code con esta cuenta"),
    ];
}
