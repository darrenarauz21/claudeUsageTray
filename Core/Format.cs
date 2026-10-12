namespace ClaudeUsageTray;

static class Format
{
    public static string Reset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null) return "";
        var local = resetsAt.Value.LocalDateTime;
        var span = local - DateTime.Now;
        if (span <= TimeSpan.Zero) return "Reinicia ahora";
        if (span.TotalHours < 24)
            return "Reinicia en " + (span.TotalHours >= 1
                ? $"{(int)span.TotalHours} h {span.Minutes} min"
                : $"{Math.Max(1, span.Minutes)} min");
        return $"Reinicia el {local:dddd}, {local:t}";
    }

    public static string Ago(DateTimeOffset? at)
    {
        if (at is null) return "";
        var span = DateTimeOffset.Now - at.Value;
        if (span.TotalMinutes < 1) return "ahora";
        if (span.TotalMinutes < 60) return $"hace {(int)span.TotalMinutes} min";
        if (span.TotalHours < 24) return $"hace {(int)span.TotalHours} h";
        return $"hace {(int)span.TotalDays} d";
    }

    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return $"{Math.Max(0, (int)span.TotalSeconds)} s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min {span.Seconds:00} s";
        return $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    public static string Tokens(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.#} G",
        >= 1_000_000 => $"{n / 1e6:0.#} M",
        >= 1_000 => $"{n / 1e3:0.#} k",
        _ => n.ToString(),
    };

    public static string Breakdown(IEnumerable<BreakdownRow> rows) =>
        string.Join(" · ", rows.Where(r => r.Percent > 0).OrderByDescending(r => r.Percent).Select(r => $"{r.Name} {r.Percent:0} %"));

    public static string Local(LocalTotals? t) => t switch
    {
        null => "",
        { Responses: 0 } => "Hoy: sin actividad",
        _ => $"Hoy: {t.Responses} respuestas · {Tokens(t.Output)} tokens",
    };
}
