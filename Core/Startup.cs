using System.Security;
using Microsoft.Win32;

namespace ClaudeUsageTray;

/// <summary>
/// Iniciar con el sistema, sin permisos de administrador: en Windows una entrada en
/// HKCU\...\Run, en Linux un .desktop en ~/.config/autostart y en macOS un LaunchAgent.
/// </summary>
static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "ClaudeUsageTray";
    const string MacLabel = "local.usodeclaude";

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string LinuxFile => Path.Combine(Home, ".config", "autostart", "uso-de-claude.desktop");
    static string MacFile => Path.Combine(Home, "Library", "LaunchAgents", MacLabel + ".plist");
    static string Exe => Environment.ProcessPath ?? "";

    public static bool IsEnabled
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValue) is string;
            }
            return File.Exists(OperatingSystem.IsMacOS() ? MacFile : LinuxFile);
        }
    }

    public static void Set(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(RunValue, $"\"{Exe}\"");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
            return;
        }

        var file = OperatingSystem.IsMacOS() ? MacFile : LinuxFile;
        if (!enabled)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, OperatingSystem.IsMacOS() ? LaunchAgent() : DesktopEntry());
    }

    static string DesktopEntry() =>
        "[Desktop Entry]\n"
        + "Type=Application\n"
        + "Name=Uso de Claude\n"
        + $"Exec=\"{Exe}\"\n"
        + "Icon=uso-de-claude\n"
        + "X-GNOME-Autostart-enabled=true\n";

    static string LaunchAgent() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        + "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
        + "<plist version=\"1.0\">\n<dict>\n"
        + $"  <key>Label</key><string>{MacLabel}</string>\n"
        + $"  <key>ProgramArguments</key><array><string>{SecurityElement.Escape(Exe)}</string></array>\n"
        + "  <key>RunAtLoad</key><true/>\n"
        + "  <key>ProcessType</key><string>Interactive</string>\n"
        + "</dict>\n</plist>\n";
}
