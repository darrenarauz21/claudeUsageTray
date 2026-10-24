using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// Atajos de teclado globales (RegisterHotKey), los mismos que coucou en Windows: Ctrl+Alt+Espacio
/// abre el chat, Ctrl+Alt+A va al permiso pendiente, Ctrl+Alt+T trae la terminal de la sesión,
/// Ctrl+Alt+N abre o pliega la isla y Ctrl+Alt+S silencia. Ninguno escribe un carácter con AltGr en el
/// teclado español. Si otra app ya tiene uno, ese queda «en uso» y no se registra.
/// </summary>
sealed class Hotkeys : NativeWindow, IDisposable
{
    public sealed record Shortcut(string Keys, string What, bool Registered);

    const int WmHotkey = 0x0312, ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
    readonly Dictionary<int, Action> _actions = new();
    readonly List<Shortcut> _list = new();

    public IReadOnlyList<Shortcut> List => _list;

    public Hotkeys() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // ventana solo de mensajes

    public void Add(Keys key, string keysLabel, string what, Action action)
    {
        var id = _actions.Count + 1;
        var ok = RegisterHotKey(Handle, id, ModControl | ModAlt | ModNoRepeat, (int)key);
        if (ok) _actions[id] = action;
        _list.Add(new Shortcut(keysLabel, what, ok));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && _actions.TryGetValue((int)m.WParam, out var action))
        {
            try { action(); } catch (Exception ex) { ErrorLog.Write(ex); }
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(Handle, id);
        _actions.Clear();
        DestroyHandle();
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
