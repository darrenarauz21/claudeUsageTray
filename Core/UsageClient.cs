using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsageTray;

sealed record LimitRow(string Kind, string Label, double Percent, string? Severity, DateTimeOffset? ResetsAt);
sealed record BreakdownRow(string Name, double Percent);
sealed record Profile(string? Email, string? Organization, string Plan, bool IsTeam);

sealed class UsageSnapshot
{
    public List<LimitRow> Limits { get; init; } = new();
    public List<BreakdownRow> Breakdown { get; init; } = new();
    public bool ExtraUsageEnabled { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }

    public LimitRow? Session => Limits.FirstOrDefault(l => l.Kind == "session");
    public LimitRow? Weekly => Limits.FirstOrDefault(l => l.Kind == "weekly_all");

    /// <summary>Conserva los últimos datos buenos y marca el error de la consulta nueva.</summary>
    public UsageSnapshot WithError(string error) => new()
    {
        Limits = Limits, Breakdown = Breakdown, ExtraUsageEnabled = ExtraUsageEnabled,
        FetchedAt = FetchedAt, Error = error,
    };
}

/// <summary>
/// Consulta el mismo endpoint que usa /usage en Claude Code. No está documentado: si cambia,
/// la app muestra el error y sigue con los tokens locales.
/// </summary>
static class UsageClient
{
    const string Url = "https://api.anthropic.com/api/oauth/usage";
    const string Expired = "Sesión vencida. Abra Claude Code con esta cuenta.";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<UsageSnapshot> FetchAsync(Account account, CancellationToken ct = default)
    {
        string token;
        long expiresAt;
        try
        {
            var raw = Credentials.ReadJson(account.ConfigDir) ?? throw new FileNotFoundException();
            using var doc = JsonDocument.Parse(raw);
            var oauth = doc.RootElement.GetProperty("claudeAiOauth");
            token = oauth.GetProperty("accessToken").GetString() ?? "";
            expiresAt = oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
        }
        catch (FileNotFoundException) { return Fail("Sin credenciales para esta carpeta."); }
        catch (Exception) { return Fail("No se pudieron leer las credenciales."); }

        if (token.Length == 0) return Fail(Expired);
        // Claude Code renueva el token mientras se usa; esta app no lo renueva para no pisar su sesión.
        if (expiresAt > 0 && DateTimeOffset.FromUnixTimeMilliseconds(expiresAt) < DateTimeOffset.UtcNow)
            return Fail(Expired);

        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("claude-usage-tray/1.0");

        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return Fail(Expired);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return Fail("Demasiadas consultas; se reintentará.");
            if (!response.IsSuccessStatusCode) return Fail($"Error {(int)response.StatusCode} del servicio.");
            return Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (TaskCanceledException) { return Fail("Sin respuesta del servicio."); }
        catch (HttpRequestException) { return Fail("Sin conexión."); }
        catch (JsonException) { return Fail("Respuesta con formato desconocido."); }
    }

    static UsageSnapshot Fail(string message) => new() { Error = message };

    internal static UsageSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var limits = new List<LimitRow>();

        if (root.TryGetProperty("limits", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in list.EnumerateArray())
            {
                var kind = Json.Str(l, "kind") ?? "";
                limits.Add(new(kind, LimitLabel(kind, l), Json.Num(l, "percent"), Json.Str(l, "severity"), Json.Date(l, "resets_at")));
            }
        }

        // Formato anterior, por si «limits» desaparece.
        if (limits.Count == 0)
        {
            foreach (var (key, kind, label) in new[]
            {
                ("five_hour", "session", "Sesión"),
                ("seven_day", "weekly_all", "Semana"),
                ("seven_day_opus", "weekly_scoped", "Semana · Opus"),
                ("seven_day_sonnet", "weekly_scoped", "Semana · Sonnet"),
            })
            {
                if (root.TryGetProperty(key, out var w) && w.ValueKind == JsonValueKind.Object)
                    limits.Add(new(kind, label, Json.Num(w, "utilization"), null, Json.Date(w, "resets_at")));
            }
        }

        var breakdown = new List<BreakdownRow>();
        if (root.TryGetProperty("seven_day_breakdown", out var b) && b.ValueKind == JsonValueKind.Object
            && b.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
                breakdown.Add(new(Json.Str(row, "display_name") ?? Json.Str(row, "key") ?? "?", Json.Num(row, "percent")));
        }

        var extra = root.TryGetProperty("extra_usage", out var x) && x.ValueKind == JsonValueKind.Object
            && x.TryGetProperty("is_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;

        return new UsageSnapshot
        {
            Limits = limits, Breakdown = breakdown, ExtraUsageEnabled = extra, FetchedAt = DateTimeOffset.Now,
        };
    }

    static string LimitLabel(string kind, JsonElement limit)
    {
        string? model = null;
        if (limit.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
            && scope.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object)
            model = Json.Str(m, "display_name");

        return kind switch
        {
            "session" => "Sesión",
            "weekly_all" => "Semana",
            "weekly_scoped" => "Semana · " + (model ?? "modelo"),
            _ => kind.Replace('_', ' '),
        };
    }

    public static Profile ReadProfile(Account account)
    {
        string? email = null, org = null, sub = null, tier = null;
        try
        {
            using var doc = JsonDocument.Parse(Json.ReadShared(account.ProfilePath));
            if (doc.RootElement.TryGetProperty("oauthAccount", out var o) && o.ValueKind == JsonValueKind.Object)
            {
                email = Json.Str(o, "emailAddress");
                org = Json.Str(o, "organizationName");
            }
        }
        catch (Exception) { }
        try
        {
            using var doc = JsonDocument.Parse(Credentials.ReadJson(account.ConfigDir) ?? "{}");
            var o = doc.RootElement.GetProperty("claudeAiOauth");
            sub = Json.Str(o, "subscriptionType");
            tier = Json.Str(o, "rateLimitTier");
        }
        catch (Exception) { }

        var plan = string.IsNullOrEmpty(sub) ? "" : char.ToUpperInvariant(sub[0]) + sub[1..];
        if (tier?.Contains("max_20x") == true) plan += " · Max 20x";
        else if (tier?.Contains("max_5x") == true) plan += " · Max 5x";
        var isTeam = sub is "team" or "enterprise";
        return new Profile(email, org, plan, isTeam);
    }
}

static class Json
{
    /// <summary>Lee sin bloquear: Claude Code puede estar escribiendo el archivo.</summary>
    public static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    public static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    public static DateTimeOffset? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var d) ? d : null;
}
