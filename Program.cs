namespace ClaudeUsageTray;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // --dump: una consulta, texto a la salida estándar y salir (para comprobar sin abrir la bandeja).
        if (args.Contains("--dump"))
            return Dump.Run();

        // --accion <acción> <valor>: hace esa acción de la mascota, sin pasar por Claude, y dice cómo le fue.
        if (Array.IndexOf(args, "--accion") is var a and >= 0 && a + 2 < args.Length)
        {
            var problem = SystemActions.RunAsync(new AssistantAction(args[a + 1], args[a + 2]),
                palette => Console.WriteLine($"color de la app: {palette}")).GetAwaiter().GetResult();
            Console.WriteLine(problem ?? "listo");
            return problem is null ? 0 : 1;
        }

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
            return args.Contains("--permiso") ? Approvals.RunHook() : NoticeDetector.RunHook();

        // --preview: abre la ventana aunque ya haya otra instancia (para probar cambios).
        var preview = args.Contains("--preview");
        using var mutex = new Mutex(true, @"Local\ClaudeUsageTray", out var created);
        if (!created && !preview)
            return 0;

        ApplicationConfiguration.Initialize();
        // Un error inesperado queda anotado (y la app sigue, si puede): sirve para el diagnóstico.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write(e.ExceptionObject as Exception);
        Application.Run(new TrayApp(showOnStart: preview || args.Contains("--show"), sessionsView: args.Contains("--sessions"),
            activityView: args.Contains("--activity"), settingsView: args.Contains("--settings"), demo: args.Contains("--demo"),
            autoSetup: !preview || args.Contains("--setup"), preview: preview,
            demoDrop: Array.IndexOf(args, "--soltar") is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null,
            // --hablar archivo.wav: la voz entera con ese audio en vez del micrófono (con «Micrófono» encendido).
            demoVoice: Array.IndexOf(args, "--hablar") is var v and >= 0 && v + 1 < args.Length ? args[v + 1] : null));
        return 0;
    }
}
