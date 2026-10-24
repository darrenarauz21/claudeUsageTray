using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>Una tecla con sus modificadores, como se guarda en Ajustes (KeyNames: «Ctrl+Alt+Space»).</summary>
readonly record struct KeyCombo(Keys Key, bool Ctrl = false, bool Alt = false, bool Shift = false, bool Win = false)
{
    /// <summary>La tecla Copilot de los teclados nuevos: Windows la recibe como Win+Mayús+F23.</summary>
    public static readonly KeyCombo Copilot = new(Keys.F23, Shift: true, Win: true);

    public static bool TryParse(string? text, out KeyCombo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool ctrl = false, alt = false, shift = false, win = false;
        Keys? key = null;
        foreach (var part in text.Split('+'))
            switch (part)
            {
                case "Ctrl": ctrl = true; break;
                case "Alt": alt = true; break;
                case "Shift": shift = true; break;
                case "Win": win = true; break;
                default:
                    if (key is not null || !Enum.TryParse<Keys>(part, out var k) || k == Keys.None) return false;
                    key = k;
                    break;
            }
        if (key is not { } found) return false;
        combo = new KeyCombo(found, ctrl, alt, shift, win);
        return true;
    }

    public override string ToString() =>
        (Win ? "Win+" : "") + (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + Key;

    public string Label => KeyNames.Label(ToString());

    /// <summary>Los modificadores pulsados ahora mismo son exactamente los suyos.</summary>
    public bool ModifiersDown() =>
        Ctrl == Down(0x11) && Alt == Down(0x12) && Shift == Down(0x10) && Win == (Down(0x5B) || Down(0x5C));

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
}

/// <summary>
/// Atajos de teclado globales (RegisterHotKey), los mismos que coucou en Windows: Ctrl+Alt+Espacio
/// abre el chat (se puede grabar otro), Ctrl+Alt+A va al permiso pendiente, Ctrl+Alt+T trae la terminal
/// de la sesión, Ctrl+Alt+N abre o pliega la isla y Ctrl+Alt+S silencia. Ninguno escribe un carácter con
/// AltGr en el teclado español. Si otra app ya tiene uno, ese queda «en uso» y no se registra.
/// </summary>
sealed class Hotkeys : NativeWindow, IDisposable
{
    public sealed record Shortcut(string Keys, string What, bool Registered);

    const int WmHotkey = 0x0312, ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    readonly Dictionary<int, Action> _actions = new();
    readonly List<Shortcut> _list = new();

    public IReadOnlyList<Shortcut> List => _list;

    public Hotkeys() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // ventana solo de mensajes

    /// <summary>Ctrl+Alt+key.</summary>
    public void Add(Keys key, string what, Action action) => Add(new KeyCombo(key, Ctrl: true, Alt: true), what, action);

    public void Add(KeyCombo combo, string what, Action action)
    {
        var id = _actions.Count + 1;
        var modifiers = (combo.Alt ? ModAlt : 0) | (combo.Ctrl ? ModControl : 0) | (combo.Shift ? ModShift : 0)
            | (combo.Win ? ModWin : 0) | ModNoRepeat;
        var ok = RegisterHotKey(Handle, id, modifiers, (int)combo.Key);
        if (ok) _actions[id] = action;
        _list.Add(new Shortcut(combo.Label, what, ok));
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

/// <summary>
/// Teclas o combinaciones que la app se queda, con un gancho de teclado de bajo nivel: no llegan a la ventana
/// activa ni a Windows (así la tecla Copilot no abre Copilot). Avisa al bajar y al subir, sin repeticiones, en
/// el hilo de la interfaz. Si la combinación lleva Win, Windows vería Win pulsada y soltada sola y abriría
/// Inicio: antes de soltarla se «toca» una tecla sin uso (0xE8, como hace AutoHotkey).
/// </summary>
sealed class KeyHook : IDisposable
{
    public event Action<KeyCombo, bool>? Changed;

    const int MaskKey = 0xE8, KeyUp = 0x2, Extended = 0x1;
    readonly LowLevelKeyboard.Proc _proc;
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    IntPtr _hook;
    KeyCombo? _held;
    bool _maskWin;

    public IReadOnlyList<KeyCombo> Combos { get; set; } = [];

    public KeyHook()
    {
        _proc = Callback;
        _hook = LowLevelKeyboard.Install(_proc);
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            var down = (int)wParam is 0x100 or 0x104; // WM_KEYDOWN, WM_SYSKEYDOWN
            if (_held is { } held && vk == (int)held.Key)
            {
                if (!down) { _held = null; Raise(held, false); }
                return 1; // ni la repetición ni la subida llegan a nadie
            }
            if (down && _held is null && Combos.FirstOrDefault(c => (int)c.Key == vk && c.ModifiersDown()) is { Key: not Keys.None } combo)
            {
                _held = combo;
                _maskWin |= combo.Win;
                Raise(combo, true);
                return 1;
            }
            if (!down && _maskWin && vk is 0x5B or 0x5C)
            {
                _maskWin = false;
                keybd_event(MaskKey, 0, 0, IntPtr.Zero);
                keybd_event(MaskKey, 0, KeyUp, IntPtr.Zero);
                keybd_event((byte)vk, 0, KeyUp | Extended, IntPtr.Zero); // y ahora sí, Win arriba
                return 1;
            }
        }
        return LowLevelKeyboard.Next(_hook, code, wParam, lParam);
    }

    void Raise(KeyCombo combo, bool down)
    {
        if (_ui is null) Changed?.Invoke(combo, down);
        else _ui.Post(_ => Changed?.Invoke(combo, down), null); // el gancho tiene que volver enseguida
    }

    public void Dispose()
    {
        LowLevelKeyboard.Remove(_hook);
        _hook = IntPtr.Zero;
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, int flags, IntPtr extra);
}

/// <summary>
/// Pasar al frente para escribir en el chat: Windows solo deja ponerse delante a la app que recibió la última
/// tecla, y una que se quedó KeyHook quizá no cuente; sin eso, lo que se escribe iría a otra ventana. Se une un
/// momento a la cola de entrada de la ventana del frente, que sí puede.
/// </summary>
static class Foreground
{
    public static void Take(IntPtr window)
    {
        var front = GetForegroundWindow();
        var theirs = GetWindowThreadProcessId(front, IntPtr.Zero);
        var mine = GetCurrentThreadId();
        var attached = theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
        try { SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(mine, theirs, false); }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr process);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
}

/// <summary>El gancho de teclado de bajo nivel (WH_KEYBOARD_LL), compartido por KeyHook y KeyRecorder.</summary>
static class LowLevelKeyboard
{
    public delegate IntPtr Proc(int code, IntPtr wParam, IntPtr lParam);

    public static IntPtr Install(Proc proc) => SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);

    public static IntPtr Next(IntPtr hook, int code, IntPtr wParam, IntPtr lParam) => CallNextHookEx(hook, code, wParam, lParam);

    public static void Remove(IntPtr hook)
    {
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
    }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, Proc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}

/// <summary>
/// «Grabar…»: una ventanita que espera la tecla o la combinación. Mientras está abierta se queda con todas las
/// teclas (así Win o la tecla Copilot no hacen lo suyo) y se cierra cuando se sueltan. Esc cancela. Las teclas
/// que escriben solo valen con Ctrl, Alt o Win; para hablar valen también Ctrl, Alt o Mayús derechos, solos.
/// </summary>
sealed class KeyRecorder : Form
{
    readonly bool _talk;
    readonly Theme _theme;
    readonly string _title;
    readonly LowLevelKeyboard.Proc _proc;
    readonly HashSet<int> _down = new();
    readonly Font _font = Fonts.Make("Segoe UI Variable Text Semibold", 10.5f, "Segoe UI Semibold");
    readonly Font _small = Fonts.Make("Segoe UI Variable Text", 9f, "Segoe UI");
    IntPtr _hook;
    int _lone;        // un modificador que se pulsó solo (puede ser la tecla, si se suelta sin otra)
    string _shown = "", _note = "";
    bool _done;

    /// <summary>La combinación (KeyNames), o null si se canceló.</summary>
    public string? Result { get; private set; }

    KeyRecorder(Theme theme, bool talk, string title)
    {
        _theme = theme;
        _talk = talk;
        _title = title;
        _proc = Callback;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = theme.Background;
        DoubleBuffered = true;
        var k = DeviceDpi / 96f;
        ClientSize = new Size((int)(360 * k), (int)(132 * k));
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
    }

    public static string? Record(Theme theme, bool talk, string title)
    {
        using var recorder = new KeyRecorder(theme, talk, title);
        recorder.ShowDialog();
        return recorder.Result;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dwm.Apply(Handle, _theme);
        _hook = LowLevelKeyboard.Install(_proc);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        LowLevelKeyboard.Remove(_hook);
        _hook = IntPtr.Zero;
        base.OnFormClosed(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_done) Close(); // clic en otro lado: cancela
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return LowLevelKeyboard.Next(_hook, code, wParam, lParam);
        var vk = Marshal.ReadInt32(lParam);
        var down = (int)wParam is 0x100 or 0x104;
        if (down) { if (_down.Add(vk)) BeginInvoke(() => Pressed(vk)); }
        else if (_down.Remove(vk)) BeginInvoke(() => Released(vk));
        return 1; // nada llega a la ventana activa ni a Windows mientras se graba
    }

    static bool IsModifier(int vk) => vk is >= 0xA0 and <= 0xA5 or 0x5B or 0x5C or 0x10 or 0x11 or 0x12;

    void Pressed(int vk)
    {
        if (_done) return;
        if (IsModifier(vk))
        {
            _lone = _down.Count == 1 ? vk : 0;
            var modifiers = Current(Keys.None).ToString(); // «Win+Shift+None»: lo que va pulsado, mientras se arma
            _shown = KeyNames.Label(modifiers[..^"None".Length]) + "…";
            _note = "";
            Invalidate();
            return;
        }
        _lone = 0;
        var combo = Current((Keys)vk);
        if (vk == (int)Keys.Escape && !combo.Ctrl && !combo.Alt && !combo.Shift && !combo.Win)
        {
            _done = true;
            Close();
            return;
        }
        Finish(combo.ToString(), Problem(combo));
    }

    void Released(int vk)
    {
        if (_done)
        {
            if (_down.Count == 0) Close();
            return;
        }
        if (vk != _lone) return;
        _lone = 0;
        var key = (Keys)vk;
        if (_talk && MicKeys.IsHold(key.ToString())) Finish(key.ToString(), null);
        else Finish(key.ToString(), _talk ? "Sola, vale Ctrl, Alt o Mayús del lado derecho." : "Combínela con otra tecla.");
    }

    /// <summary>La tecla con los modificadores pulsados ahora.</summary>
    KeyCombo Current(Keys key) => new(key,
        Ctrl: _down.Overlaps([0xA2, 0xA3, 0x11]), Alt: _down.Overlaps([0xA4, 0xA5, 0x12]),
        Shift: _down.Overlaps([0xA0, 0xA1, 0x10]), Win: _down.Overlaps([0x5B, 0x5C]));

    void Finish(string combo, string? problem)
    {
        _shown = KeyNames.Label(combo);
        _note = problem ?? "";
        if (problem is null)
        {
            Result = combo;
            _done = true;
            if (_down.Count == 0) BeginInvoke(Close);
        }
        Invalidate();
    }

    /// <summary>Por qué no sirve esa combinación (null: sirve).</summary>
    string? Problem(KeyCombo c)
    {
        if (c == KeyCombo.Copilot && !_talk) return "La tecla Copilot tiene su propio interruptor en Ajustes.";
        if (!_talk && c is { Ctrl: true, Alt: true, Shift: false, Win: false, Key: Keys.A or Keys.T or Keys.N or Keys.S })
            return "Ese atajo ya lo usa la isla.";
        if (c.Ctrl || c.Alt || c.Win) return null;
        var alone = c.Key is >= Keys.F13 and <= Keys.F24 or Keys.Pause or Keys.Scroll
            or >= Keys.BrowserSearch and <= Keys.BrowserHome or >= Keys.LaunchMail and <= Keys.LaunchApplication2;
        return alone ? null : "Esa tecla tiene otro uso: combínela con Ctrl, Alt o Win.";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var k = DeviceDpi / 96f;
        var pad = (int)(18 * k);
        var width = ClientSize.Width - pad * 2;
        TextRenderer.DrawText(g, _title, _font, new Rectangle(pad, pad, width, (int)(22 * k)), _theme.Text, TextFormatFlags.Left);
        var box = new RectangleF(pad, pad + 28 * k, width, 36 * k);
        Draw.Fill(g, _theme.Raised, box, 8 * k);
        TextRenderer.DrawText(g, _shown.Length > 0 ? _shown : "Pulse la tecla o la combinación…", _font, Rectangle.Round(box),
            _shown.Length > 0 ? _theme.Text : _theme.Tertiary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var note = _note.Length > 0 ? _note : "Esc cancela";
        TextRenderer.DrawText(g, note, _small, new Rectangle(pad, (int)(box.Bottom + 10 * k), width, (int)(20 * k)),
            _note.Length > 0 ? _theme.Danger : _theme.Secondary, TextFormatFlags.Left);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _font.Dispose(); _small.Dispose(); }
        base.Dispose(disposing);
    }
}
