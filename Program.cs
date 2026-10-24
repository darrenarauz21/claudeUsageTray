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

        // --descargar-voz: baja la voz de Claupet como desde Ajustes, con el avance a la salida estándar.
        if (args.Contains("--descargar-voz"))
        {
            var last = -1;
            ClaupetVoiceModel.DownloadAsync(new Progress<double>(p =>
            {
                var percent = (int)(p * 100);
                if (percent / 10 != last / 10) Console.WriteLine($"{percent} %");
                last = percent;
            }), CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(ClaupetVoiceModel.Ready ? "lista" : "incompleta");
            return ClaupetVoiceModel.Ready ? 0 : 1;
        }

        // --decir <F4|M1> <texto>: lo dice con la voz de Claupet (ya descargada), como al leer una respuesta, y dice cuánto tardó.
        if (Array.IndexOf(args, "--decir") is var t and >= 0 && t + 2 < args.Length)
        {
            if (!ClaupetVoiceModel.Ready) { Console.WriteLine("Falta descargar la voz (Ajustes › Asistente)."); return 1; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var engine = new Supertonic(ClaupetVoiceModel.Folder);
            var voice = Supertonic.LoadVoice(ClaupetVoiceModel.VoiceFile(args[t + 1]));
            Console.WriteLine($"carga: {clock.ElapsedMilliseconds} ms");
            using var output = new WaveOut(engine.SampleRate);
            foreach (var chunk in Supertonic.Chunks(args[t + 2]))
            {
                clock.Restart();
                var samples = engine.Speak(chunk, voice);
                Console.WriteLine($"«{chunk}»: {samples.Length / (double)engine.SampleRate:0.0} s de audio en {clock.ElapsedMilliseconds} ms");
                output.Play(samples);
            }
            output.Drain(CancellationToken.None);
            return 0;
        }

        // --transcribir archivo.wav (16 kHz, mono, 16 bits): lo pasa a texto como el micrófono, con el modelo ya descargado.
        if (Array.IndexOf(args, "--transcribir") is var w and >= 0 && w + 1 < args.Length)
        {
            if (!VoiceModel.Ready) { Console.WriteLine("Falta descargar el modelo del micrófono (Ajustes › Asistente)."); return 1; }
            if (Transcriber.ReadWav(File.ReadAllBytes(args[w + 1])) is not { } pcm) { Console.WriteLine("El WAV no tiene audio."); return 1; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var transcriber = new Transcriber();
            var heard = transcriber.TranscribeAsync(pcm).GetAwaiter().GetResult();
            Console.WriteLine($"«{heard}» en {clock.ElapsedMilliseconds} ms");
            return heard.Length > 0 ? 0 : 1;
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
