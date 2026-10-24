using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// Si suena algo en el control multimedia de Windows (Spotify, YouTube en el navegador…), para que la
/// mascota cabecee o baile. Se le pregunta a GlobalSystemMediaTransportControlsSessionManager de WinRT
/// por COM, sin el SDK de Windows (que suma decenas de MB a la app), en un hilo propio cada 2 s y solo
/// mientras alguien lo pida con Watch. Solo se mira si alguna sesión está reproduciendo, no qué suena.
/// En Linux y macOS, por ahora, nunca suena nada.
/// </summary>
static class Music
{
    const int Every = 2000;
    const string Manager = "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";
    static readonly Guid StaticsIid = new("2050C4EE-11A0-57DE-AED7-C97C70338245");
    static readonly Guid ManagerIid = new("CACE8EAC-E86E-504A-AB31-5FF8FF1BCE49");
    static readonly Guid PlaybackInfoIid = new("94B4B6CF-E8BA-51AD-87A7-C10ADE106127");
    static readonly Guid AsyncInfoIid = new("00000036-0000-0000-C000-000000000046");
    const int Completed = 1, Playing = 4; // AsyncStatus.Completed y PlaybackStatus.Playing

    static readonly AutoResetEvent Wake = new(false);
    static volatile bool _watching, _playing;
    static Thread? _thread;

    /// <summary>Algo se está reproduciendo (al día cada 2 s, mientras se mire).</summary>
    public static bool IsPlaying => _watching && _playing;

    /// <summary>Empieza o deja de mirar: con «Bailar con música» y la mascota en el escritorio.</summary>
    public static void Watch(bool on)
    {
        if (!OperatingSystem.IsWindows() || on == _watching) return;
        _watching = on;
        if (on && _thread is null)
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Música" };
            _thread.Start();
        }
        Wake.Set();
    }

    static void Loop()
    {
        RoInitialize(1); // multihilo
        var manager = IntPtr.Zero;
        while (true)
        {
            if (!_watching)
            {
                _playing = false;
                Wake.WaitOne();
                continue;
            }
            try
            {
                if (manager == IntPtr.Zero) manager = RequestManager();
                _playing = manager != IntPtr.Zero && AnyPlaying(manager);
            }
            catch (Exception)
            {
                _playing = false;
            }
            Wake.WaitOne(Every);
        }
    }

    /// <summary>El administrador de sesiones multimedia (RequestAsync, esperando a que termine), o cero si no hay.</summary>
    static IntPtr RequestManager()
    {
        if (WindowsCreateString(Manager, Manager.Length, out var name) < 0) return IntPtr.Zero;
        IntPtr statics;
        try
        {
            if (RoGetActivationFactory(name, StaticsIid, out statics) < 0) return IntPtr.Zero;
        }
        finally { WindowsDeleteString(name); }
        try
        {
            if (Call(statics, 6, out var operation) < 0) return IntPtr.Zero; // RequestAsync
            try
            {
                if (Marshal.QueryInterface(operation, in AsyncInfoIid, out var info) < 0) return IntPtr.Zero;
                try
                {
                    var status = 0;
                    for (var i = 0; i < 100 && status == 0; i++) // hasta 5 s
                    {
                        if (Read(info, 7, out status) < 0) return IntPtr.Zero; // Status
                        if (status == 0) Thread.Sleep(50);
                    }
                    if (status != Completed) return IntPtr.Zero;
                }
                finally { Marshal.Release(info); }
                if (Call(operation, 8, out var result) < 0 || result == IntPtr.Zero) return IntPtr.Zero; // GetResults
                // Se queda con la interfaz pedida: si no lo es, no se llama nada más.
                var ok = Marshal.QueryInterface(result, in ManagerIid, out var manager);
                Marshal.Release(result);
                return ok < 0 ? IntPtr.Zero : manager;
            }
            finally { Marshal.Release(operation); }
        }
        finally { Marshal.Release(statics); }
    }

    static bool AnyPlaying(IntPtr manager)
    {
        if (Call(manager, 7, out var sessions) < 0) return false; // GetSessions: IVectorView de sesiones
        try
        {
            if (Read(sessions, 7, out var size) < 0) return false; // Size
            for (uint i = 0; i < (uint)size; i++)
            {
                if (GetAt(sessions, i, out var session) < 0) continue;
                try
                {
                    if (Call(session, 9, out var playback) < 0) continue; // GetPlaybackInfo
                    try
                    {
                        if (Marshal.QueryInterface(playback, in PlaybackInfoIid, out var info) < 0) continue;
                        try
                        {
                            if (Read(info, 7, out var status) >= 0 && status == Playing) return true; // PlaybackStatus
                        }
                        finally { Marshal.Release(info); }
                    }
                    finally { Marshal.Release(playback); }
                }
                finally { Marshal.Release(session); }
            }
            return false;
        }
        finally { Marshal.Release(sessions); }
    }

    // Las interfaces de WinRT empiezan con las 6 entradas de IInspectable; las que siguen son las de cada una.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int Getter(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int Indexer(IntPtr self, uint index, out IntPtr value);

    static T Slot<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    /// <summary>Un método sin argumentos que devuelve un objeto.</summary>
    static int Call(IntPtr obj, int slot, out IntPtr value) => Slot<Getter>(obj, slot)(obj, out value);

    /// <summary>Una propiedad entera de 32 bits: se recibe en un puntero, para que nada se pise si el método escribiera más.</summary>
    static int Read(IntPtr obj, int slot, out int value)
    {
        var hr = Slot<Getter>(obj, slot)(obj, out var raw);
        value = (int)(raw.ToInt64() & 0xFFFFFFFF);
        return hr;
    }

    static int GetAt(IntPtr vector, uint index, out IntPtr value) => Slot<Indexer>(vector, 6)(vector, index, out value);

    [DllImport("combase.dll")]
    static extern int RoInitialize(int type);

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);
}
