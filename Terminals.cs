using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// Trae al frente la ventana donde corre una sesión de Claude Code: sube por los procesos padres del
/// de claude hasta el primero que tiene ventana (Windows Terminal, PowerShell, VS Code…). Si la sesión
/// es una pestaña, aparece la ventana, no siempre esa pestaña.
/// </summary>
static class Terminals
{
    public static bool Focus(int pid)
    {
        try
        {
            var current = pid;
            for (var depth = 0; depth < 8 && current > 4; depth++)
            {
                using var process = Process.GetProcessById(current);
                var window = process.MainWindowHandle;
                if (window != IntPtr.Zero)
                {
                    if (IsIconic(window)) ShowWindow(window, 9); // SW_RESTORE
                    SetForegroundWindow(window);
                    return true;
                }
                current = ParentOf(process.Handle);
            }
        }
        catch (Exception) { }
        return false;
    }

    static int ParentOf(IntPtr handle)
    {
        var info = new ProcessBasicInformation();
        return NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf(info), out _) == 0
            ? (int)info.InheritedFromUniqueProcessId : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
}
