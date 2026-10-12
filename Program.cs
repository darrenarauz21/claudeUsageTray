namespace ClaudeUsageTray;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // --dump: una consulta, texto a la salida estándar y salir (para comprobar sin abrir la bandeja).
        if (args.Contains("--dump"))
            return Dump.Run();

        // --diag: el mismo resumen que «Copiar diagnóstico», a la salida estándar.
        if (args.Contains("--diag"))
        {
            var settings = SettingsStore.Load();
            SettingsStore.DetectNew(settings);
            Console.Write(Diagnostics.Report(settings, new SessionMonitor().Scan(settings.Accounts), 0));
            return 0;
        }

        // --hook: lo ejecuta Claude Code (hook Notification); guarda el evento y termina.
        if (args.Contains("--hook"))
            return NoticeDetector.RunHook();

        // --preview: abre la ventana aunque ya haya otra instancia (para probar cambios).
        var preview = args.Contains("--preview");
        using var mutex = new Mutex(true, @"Local\ClaudeUsageTray", out var created);
        if (!created && !preview)
            return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(showOnStart: preview || args.Contains("--show"), sessionsView: args.Contains("--sessions"),
            activityView: args.Contains("--activity"), demo: args.Contains("--demo"),
            autoSetup: !preview || args.Contains("--setup")));
        return 0;
    }
}
