using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace ClaudeUsageTray;

static class Program
{
    public static string[] Args { get; private set; } = [];

    [STAThread]
    static int Main(string[] args)
    {
        Args = args;
        // Mismos modos de línea de comandos que la app de Windows.
        if (args.Contains("--dump")) return Dump.Run();
        if (args.Contains("--hook")) return args.Contains("--permiso") ? Approvals.RunHook() : NoticeDetector.RunHook();
        if (args.Contains("--diag"))
        {
            var settings = SettingsStore.Load();
            SettingsStore.DetectNew(settings);
            Console.Write(Diagnostics.Report(settings, new SessionMonitor().Scan(settings.Accounts), 0));
            return 0;
        }

        if (OperatingSystem.IsLinux() && MissingLinuxLibraries() is { Length: > 0 } missing)
        {
            Console.Error.WriteLine($"Uso de Claude no puede abrir su ventana: faltan bibliotecas del sistema ({missing}).");
            Console.Error.WriteLine("Instálelas y vuelva a abrir la app:");
            Console.Error.WriteLine("  Ubuntu, Debian, Mint:  sudo apt install libice6 libsm6 libfontconfig1");
            Console.Error.WriteLine("  Fedora:                sudo dnf install libICE libSM fontconfig");
            Console.Error.WriteLine("  Arch:                  sudo pacman -S libice libsm fontconfig");
            return 1;
        }

        // --preview: abre la ventana aunque ya haya otra instancia (para probar cambios).
        var preview = args.Contains("--preview");
        using var mutex = new Mutex(true, @"Local\UsoDeClaude", out var created);
        if (!created && !preview) return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>
    /// En Linux, Avalonia 11 carga siempre libICE y libSM (gestión de sesión de X11) y usa fontconfig.
    /// Vienen en los escritorios habituales, pero no en instalaciones mínimas.
    /// </summary>
    static string MissingLinuxLibraries()
    {
        var missing = new List<string>();
        foreach (var library in new[] { "libICE.so.6", "libSM.so.6", "libfontconfig.so.1" })
        {
            if (System.Runtime.InteropServices.NativeLibrary.TryLoad(library, out var handle))
                System.Runtime.InteropServices.NativeLibrary.Free(handle);
            else missing.Add(library);
        }
        return string.Join(", ", missing);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}

sealed class App : Application
{
    TrayController? _controller;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default; // sigue el tema del sistema
        Name = "Uso de Claude";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var args = Program.Args;
        var preview = args.Contains("--preview");
        _controller = new TrayController(this,
            showOnStart: preview || args.Contains("--show"),
            sessionsView: args.Contains("--sessions"),
            activityView: args.Contains("--activity"),
            demo: args.Contains("--demo"),
            autoSetup: !preview || args.Contains("--setup"));
        base.OnFrameworkInitializationCompleted();
    }
}
