namespace ClaudeUsageTray;

static class Dump
{
    public static int Run()
    {
        var settings = SettingsStore.Load();
        SettingsStore.DetectNew(settings);
        foreach (var a in settings.Accounts)
        {
            var p = UsageClient.ReadProfile(a);
            var s = UsageClient.FetchAsync(a).GetAwaiter().GetResult();
            Console.WriteLine($"== {a.Label} ({a.ConfigDir}) {p.Email} · {p.Plan}");
            if (s.Error is not null)
                Console.WriteLine($"   error: {s.Error}");
            foreach (var l in s.Limits)
                Console.WriteLine($"   {l.Label}: {l.Percent:0} % · {Format.Reset(l.ResetsAt)}");
            if (s.Breakdown.Count > 0)
                Console.WriteLine("   " + Format.Breakdown(s.Breakdown));
        }
        foreach (var s in new SessionMonitor().Scan(settings.Accounts))
            Console.WriteLine($"== Sesión [{s.State}] {s.DisplayTitle} ({s.Account.Label}) turnos={s.Turns} pasos={s.Steps} "
                + $"acción={s.Action} fin={Format.Ago(s.TurnEndedAt)} tareas={s.TodoDone}/{s.TodoTotal}");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var activity = new ActivityTracker().Scan(settings.Accounts.Select(a => a.ConfigDir));
        var today = DateOnly.FromDateTime(DateTime.Today);
        var stats = ActivityStats.Compute(activity, today.AddMonths(-6), today);
        Console.WriteLine($"== Actividad ({watch.ElapsedMilliseconds} ms): {activity.Days.Count} días desde {activity.FirstDay} · "
            + $"{Format.Tokens(stats.Tokens)} tokens · activos {stats.ActiveDays} · racha {stats.CurrentStreak}/{stats.LongestStreak} · "
            + $"día {stats.BusiestDay} · modelo {stats.FavoriteModel}");
        foreach (var (day, record) in activity.Days.OrderByDescending(kv => kv.Key).Take(5))
            Console.WriteLine($"   {day:yyyy-MM-dd}: {Format.Tokens(record.Tokens)} tokens · {record.Responses} resp · {record.Prompts} pedidos · {record.Sessions} sesiones · {record.ToolCalls} herramientas");
        var local = LocalUsage.ScanToday(settings.Accounts.Select(a => a.ConfigDir));
        Console.WriteLine("== Local: " + Format.Local(local));
        return 0;
    }
}
