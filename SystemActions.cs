using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeUsageTray;

/// <summary>
/// Lo que la mascota hace en el equipo cuando se lo piden (la respuesta de Claude empieza con
/// [ACCION]): una lista cerrada, para que una frase mal entendida no pueda hacer otra cosa. Lo que no
/// está aquí se le encarga a una sesión de Claude Code. Cada acción devuelve null si salió bien o lo
/// que falló, dicho en corto para la nubecita.
/// </summary>
static class SystemActions
{
    public static async Task<string?> RunAsync(AssistantAction action, Action<string> setPalette)
    {
        try
        {
            return action.Kind switch
            {
                "fondo" => await WallpaperAsync(action.Value),
                "tema" => Mode(action.Value),
                "color" => Palette(action.Value, setPalette),
                "volumen" => Volume(action.Value),
                "musica" or "música" => await MediaAsync(action.Value),
                "abrir" => Open(action.Value),
                _ => "Eso todavía no lo sé hacer.",
            };
        }
        catch (Exception ex) { return $"No pude: {ex.Message}"; }
    }

    static bool Same(string a, string b) =>
        string.Compare(a.Trim(), b.Trim(), CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    // ── Fondo de pantalla ───────────────────────────────────────────────────────────────────

    /// <summary>Los fondos bajados de internet; quedan los últimos KeepWallpapers.</summary>
    static readonly string WallpaperDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray", "fondos");

    const int KeepWallpapers = 5;
    const long MaxImage = 25 * 1024 * 1024;
    static readonly string[] ImageTypes = [".jpg", ".jpeg", ".png", ".bmp"];
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Una imagen del equipo o una URL https de una imagen (se baja a WallpaperDir).</summary>
    static async Task<string?> WallpaperAsync(string value)
    {
        string file;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (uri.Scheme != Uri.UriSchemeHttps) return "Solo bajo imágenes de enlaces https.";
            var downloaded = await DownloadImageAsync(uri);
            if (downloaded is null) return "Ese enlace no es una imagen que pueda poner de fondo.";
            file = downloaded;
        }
        else
        {
            file = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim('"', ' ')));
            if (!File.Exists(file)) return "No encontré esa imagen.";
            if (!ImageTypes.Contains(Path.GetExtension(file).ToLowerInvariant())) return "Eso no es una imagen (.jpg, .png o .bmp).";
        }
        const int SetDeskWallpaper = 0x0014, UpdateIniFile = 0x01, SendChange = 0x02;
        return SystemParametersInfo(SetDeskWallpaper, 0, file, UpdateIniFile | SendChange) ? null : "Windows no aceptó esa imagen.";
    }

    /// <summary>La baja si es JPEG, PNG o BMP de hasta MaxImage y se puede abrir como imagen; si no, null.</summary>
    static async Task<string?> DownloadImageAsync(Uri uri)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) return null;
        var extension = response.Content.Headers.ContentType?.MediaType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/bmp" => ".bmp",
            _ => null,
        };
        if (extension is null || response.Content.Headers.ContentLength > MaxImage) return null;

        Directory.CreateDirectory(WallpaperDir);
        var file = Path.Combine(WallpaperDir, $"fondo-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");
        var ok = false;
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var target = File.Create(file))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > MaxImage) return null;
                    await target.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            using (Image.FromFile(file)) { } // que de verdad sea una imagen
            ok = true;
        }
        catch (Exception) { return null; }
        finally
        {
            if (!ok) try { File.Delete(file); } catch (Exception) { }
        }

        foreach (var old in new DirectoryInfo(WallpaperDir).GetFiles("fondo-*").OrderByDescending(f => f.Name).Skip(KeepWallpapers))
            try { old.Delete(); } catch (Exception) { }
        return file;
    }

    // ── Modo claro u oscuro y color de la app ───────────────────────────────────────────────

    static string? Mode(string value)
    {
        int light;
        if (Same(value, "claro")) light = 1;
        else if (Same(value, "oscuro")) light = 0;
        else return "Solo sé poner el modo claro o el oscuro.";
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
        {
            key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
        }
        // Así se enteran las ventanas abiertas, la barra de tareas incluida.
        const int WmSettingChange = 0x001A, AbortIfHung = 0x0002;
        SendMessageTimeout(new IntPtr(0xFFFF), WmSettingChange, IntPtr.Zero, "ImmersiveColorSet", AbortIfHung, 1000, out _);
        return null;
    }

    static string? Palette(string value, Action<string> setPalette)
    {
        var palette = Theme.Palettes.FirstOrDefault(p => Same(p.Label, value) || Same(p.Id, value));
        if (palette.Id is null)
            return $"Ese color no lo tengo: hay {string.Join(", ", Theme.Palettes.Select(p => p.Label))}.";
        setPalette(palette.Id);
        return null;
    }

    // ── Volumen y música ────────────────────────────────────────────────────────────────────

    /// <summary>Un número de 0 a 100, subir o bajar (de a 10), silenciar o activar; en la salida de audio predeterminada.</summary>
    static string? Volume(string value)
    {
        var endpoint = DefaultEndpoint();
        try
        {
            var context = Guid.Empty;
            endpoint.GetMasterVolumeLevelScalar(out var level);
            float? target = null;
            if (int.TryParse(value.TrimEnd('%', ' '), out var percent)) target = Math.Clamp(percent, 0, 100) / 100f;
            else if (Same(value, "subir")) target = Math.Min(1f, level + 0.1f);
            else if (Same(value, "bajar")) target = Math.Max(0f, level - 0.1f);
            else if (Same(value, "silenciar")) endpoint.SetMute(true, ref context);
            else if (Same(value, "activar")) endpoint.SetMute(false, ref context);
            else return "No entendí cuánto volumen.";
            if (target is { } t)
            {
                endpoint.SetMasterVolumeLevelScalar(t, ref context);
                if (t > 0) endpoint.SetMute(false, ref context);
            }
            return null;
        }
        finally { Marshal.ReleaseComObject(endpoint); }
    }

    static IAudioEndpointVolume DefaultEndpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            enumerator.GetDefaultAudioEndpoint(0, 1, out var device); // eRender, eMultimedia
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref iid, 0x17, IntPtr.Zero, out var endpoint); // CLSCTX_ALL
                return (IAudioEndpointVolume)endpoint;
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    /// <summary>Con las teclas multimedia: lo que suene en Windows (Spotify, YouTube en el navegador…).</summary>
    static async Task<string?> MediaAsync(string value)
    {
        const byte Next = 0xB0, Previous = 0xB1, PlayPause = 0xB3;
        if (Same(value, "siguiente")) Tap(Next);
        else if (Same(value, "anterior")) Tap(Previous);
        else if (Same(value, "reproducir") || Same(value, "pausar"))
        {
            // La tecla solo alterna: se mira antes si algo suena, para no reanudar al pedir una pausa.
            var play = Same(value, "reproducir");
            var playing = await Task.Run(Music.PlayingNow);
            if (playing != play) Tap(PlayPause);
        }
        else return "No entendí qué hacer con la música.";
        return null;
    }

    static void Tap(byte key)
    {
        const int Extended = 0x1, KeyUp = 0x2;
        keybd_event(key, 0, Extended, IntPtr.Zero);
        keybd_event(key, 0, Extended | KeyUp, IntPtr.Zero);
    }

    // ── Abrir ───────────────────────────────────────────────────────────────────────────────

    static readonly string[] Schemes = ["https", "http", "spotify", "ms-settings"];

    /// <summary>Una URL de los tipos de Schemes o una app del menú Inicio, por su nombre.</summary>
    static string? Open(string value)
    {
        value = value.Trim().Trim('"');
        if (value.Length == 0) return "No me dijeron qué abrir.";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            if (!Schemes.Contains(uri.Scheme)) return "Ese tipo de enlace no lo abro.";
            Start(value);
            return null;
        }
        if (FindShortcut(value) is not { } shortcut) return $"No encontré «{value}» en el menú Inicio.";
        Start(shortcut);
        return null;
    }

    static void Start(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    /// <summary>El acceso directo del menú Inicio con ese nombre: primero el exacto, después el que empieza así y por último el que lo contiene.</summary>
    static string? FindShortcut(string name)
    {
        var shortcuts = new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms }
            .Select(Environment.GetFolderPath)
            .Where(Directory.Exists)
            .SelectMany(dir =>
            {
                try { return Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories); }
                catch (Exception) { return []; }
            })
            .Select(path => (Path: path, Name: Path.GetFileNameWithoutExtension(path)))
            .Where(s => !s.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) && !s.Name.Contains("desinstalar", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        return shortcuts.FirstOrDefault(s => Same(s.Name, name)).Path
            ?? shortcuts.Where(s => compare.IsPrefix(s.Name, name.Trim(), Loose)).OrderBy(s => s.Name.Length).FirstOrDefault().Path
            ?? shortcuts.Where(s => compare.IndexOf(s.Name, name.Trim(), Loose) >= 0).OrderBy(s => s.Name.Length).FirstOrDefault().Path;
    }

    // ── Windows ─────────────────────────────────────────────────────────────────────────────

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SystemParametersInfo(int action, int param, string value, int winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);

    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, int flags, IntPtr extra);

    // Core Audio: la salida predeterminada y su volumen. Solo se declaran los métodos hasta el último que se usa, en su orden.

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float level, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float level);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float level);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    }
}
