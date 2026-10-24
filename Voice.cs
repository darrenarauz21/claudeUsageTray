using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;

namespace ClaudeUsageTray;

/// <summary>
/// El micrófono, por WinMM (waveIn): 16 kHz, 16 bits, mono, en trozos de 100 ms. Un hilo propio espera
/// cada trozo, lo guarda y avisa el nivel (para las barritas). Nada sale del equipo.
/// </summary>
sealed class Microphone : IDisposable
{
    const int Rate = 16000, BufferMs = 100, Buffers = 4;
    const int BufferBytes = Rate * 2 * BufferMs / 1000;

    /// <summary>El nivel de la voz en el último trozo, de 0 a 1 (en el hilo de la interfaz).</summary>
    public event Action<float>? Level;

    readonly List<short> _samples = new();
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    readonly AutoResetEvent _ready = new(false);
    IntPtr _device;
    IntPtr[] _headers = [];
    IntPtr[] _data = [];
    Thread? _thread;
    int _next;
    volatile bool _recording;

    public bool Recording => _recording;

    /// <summary>Para las pruebas (--hablar): en vez del micrófono, este audio, a tiempo real (y después, silencio).</summary>
    public short[]? Fake { get; set; }
    System.Threading.Timer? _fakeTimer;
    int _fakeAt;

    /// <summary>Empieza a grabar. False si no hay micrófono (o Windows no lo deja usar).</summary>
    public bool Start()
    {
        if (_recording) return true;
        _samples.Clear();
        _next = 0;
        if (Fake is { } fake)
        {
            _recording = true;
            _fakeAt = 0;
            _fakeTimer = new System.Threading.Timer(_ =>
            {
                if (!_recording) return;
                var count = Math.Clamp(fake.Length - _fakeAt, 0, BufferBytes / 2);
                Store(count > 0 ? fake.AsSpan(_fakeAt, count).ToArray() : new short[BufferBytes / 2]);
                _fakeAt += count;
            }, null, BufferMs, BufferMs);
            return true;
        }
        var format = new WaveFormat { Tag = 1, Channels = 1, SamplesPerSec = Rate, AvgBytesPerSec = Rate * 2, BlockAlign = 2, BitsPerSample = 16 };
        if (waveInOpen(out _device, -1, ref format, _ready.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x50000) != 0) // CALLBACK_EVENT
            return false;
        _headers = new IntPtr[Buffers];
        _data = new IntPtr[Buffers];
        var size = Marshal.SizeOf<WaveHeader>();
        for (var i = 0; i < Buffers; i++)
        {
            _data[i] = Marshal.AllocHGlobal(BufferBytes);
            _headers[i] = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(new WaveHeader { Data = _data[i], BufferLength = BufferBytes }, _headers[i], false);
            waveInPrepareHeader(_device, _headers[i], size);
            waveInAddBuffer(_device, _headers[i], size);
        }
        _recording = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "Micrófono" };
        _thread.Start();
        waveInStart(_device);
        return true;
    }

    void Pump()
    {
        while (_recording)
        {
            _ready.WaitOne(200);
            while (_recording && Take(requeue: true)) { }
        }
    }

    /// <summary>
    /// Guarda el próximo trozo si ya está lleno. Se leen en el orden en que se llenan (_next), que es el
    /// de la cola, aunque el hilo se atrase y encuentre varios listos a la vez.
    /// </summary>
    bool Take(bool requeue)
    {
        var header = _headers[_next];
        var h = Marshal.PtrToStructure<WaveHeader>(header);
        if ((h.Flags & 1) == 0) return false; // WHDR_DONE
        var count = (int)h.BytesRecorded / 2;
        if (count > 0)
        {
            var chunk = new short[count];
            Marshal.Copy(h.Data, chunk, 0, count);
            Store(chunk);
        }
        h.BytesRecorded = 0;
        h.Flags &= ~1u;
        Marshal.StructureToPtr(h, header, false);
        if (requeue) waveInAddBuffer(_device, header, Marshal.SizeOf<WaveHeader>());
        _next = (_next + 1) % Buffers;
        return true;
    }

    /// <summary>Guarda un trozo y avisa su nivel.</summary>
    void Store(short[] chunk)
    {
        if (chunk.Length == 0) return;
        double sum = 0;
        foreach (var s in chunk) sum += (double)s * s;
        var rms = (float)Math.Min(1, Math.Sqrt(sum / chunk.Length) / 6000);
        lock (_samples) _samples.AddRange(chunk);
        if (_ui is not null) _ui.Post(_ => Level?.Invoke(rms), null);
    }

    /// <summary>Deja de grabar y devuelve lo grabado, con el último trozo aunque esté a medias.</summary>
    public short[] Stop()
    {
        if (!_recording) return [];
        _recording = false;
        if (_fakeTimer is { } timer)
        {
            timer.Dispose();
            _fakeTimer = null;
            lock (_samples) return _samples.ToArray();
        }
        _ready.Set();
        _thread?.Join(500);
        waveInStop(_device);  // el trozo que se estaba llenando queda listo con lo que tenga
        waveInReset(_device); // y los vacíos vuelven también
        for (var i = 0; i < Buffers && Take(requeue: false); i++) { }
        var size = Marshal.SizeOf<WaveHeader>();
        foreach (var header in _headers) waveInUnprepareHeader(_device, header, size);
        waveInClose(_device);
        foreach (var p in _headers.Concat(_data)) Marshal.FreeHGlobal(p);
        _headers = _data = [];
        lock (_samples) return _samples.ToArray();
    }

    /// <summary>Lo grabado hasta ahora, sin dejar de grabar (para ir pasando a texto mientras se habla).</summary>
    public short[] Snapshot()
    {
        lock (_samples) return _samples.ToArray();
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormat
    {
        public ushort Tag, Channels;
        public int SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr device, int id, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveInStart(IntPtr device);
    [DllImport("winmm.dll")] static extern int waveInStop(IntPtr device);
    [DllImport("winmm.dll")] static extern int waveInReset(IntPtr device);
    [DllImport("winmm.dll")] static extern int waveInClose(IntPtr device);
}

/// <summary>
/// El modelo de voz de Whisper (ggml «small», multilingüe, 488 MB): se descarga una vez, al encender el
/// micrófono, a la carpeta de la app. Después todo funciona sin internet.
/// </summary>
static class VoiceModel
{
    const string Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin";
    /// <summary>Lo que mide y su SHA-256, según Hugging Face: una descarga que no coincide no se usa.</summary>
    public const long Size = 487_601_967;
    const string Sha256 = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b";

    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray", "voz", "ggml-small.bin");

    /// <summary>Ya está descargado (y no a medias).</summary>
    public static bool Ready => Length(FilePath) == Size;

    static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>Lo baja (ver ModelDownload) y avisa el avance de 0 a 1.</summary>
    public static Task DownloadAsync(IProgress<double> progress, CancellationToken cancel) =>
        ModelDownload.FetchAsync(Url, FilePath, Size, Sha256, new Progress<long>(have => progress.Report((double)have / Size)), cancel);
}

/// <summary>
/// La descarga de un archivo de modelo: a un .parte y, entero y con su SHA-256, a su lugar. Si la
/// conexión se corta (o se queda un minuto sin datos), retoma donde iba; también lo que quedó de una
/// vez anterior. Avisa cuántos bytes lleva.
/// </summary>
static class ModelDownload
{
    static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    public static async Task FetchAsync(string url, string path, long size, string sha256, IProgress<long> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".parte";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var failures = 0;
        progress.Report(Length(part));
        while (Length(part) < size)
        {
            var before = Length(part);
            try { await ResumeAsync(http, url, part, progress, cancel); }
            catch (Exception ex) when (!cancel.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                failures = Length(part) > before ? 1 : failures + 1;
                if (failures >= 8) throw new IOException("Se cortó la conexión varias veces seguidas.", ex);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * failures)), cancel);
            }
        }
        progress.Report(size);
        if (Length(part) != size || !await Task.Run(() => Matches(part, sha256, cancel), cancel))
        {
            File.Delete(part);
            throw new IOException("Lo descargado no es el modelo esperado; se borró.");
        }
        File.Move(part, path, overwrite: true);
    }

    static async Task ResumeAsync(HttpClient http, string url, string part, IProgress<long> progress, CancellationToken cancel)
    {
        var have = Length(part);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(part); // lo que había no sirve para retomar: desde cero
            return;
        }
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.PartialContent) have = 0; // el servidor no retoma: desde cero
        await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
        await using var target = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
        var buffer = new byte[1 << 16];
        var reported = have;
        while (true)
        {
            stall.CancelAfter(TimeSpan.FromSeconds(60));
            var read = await source.ReadAsync(buffer, stall.Token);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancel);
            have += read;
            if (have - reported >= 1_000_000) { reported = have; progress.Report(have); }
        }
    }

    static bool Matches(string path, string sha256, CancellationToken cancel)
    {
        using var file = File.OpenRead(path);
        using var sha = SHA256.Create();
        var buffer = new byte[1 << 20];
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(buffer, 0, 0);
        return Convert.ToHexString(sha.Hash!).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Pasa la voz grabada a texto con Whisper, en español, en el equipo. De a una: el modelo cargado ocupa
/// unos 850 MB de memoria, y VoiceInput lo suelta cuando no se usa.
/// </summary>
sealed class Transcriber : IDisposable
{
    /// <summary>Por debajo de este nivel (RMS de 16 bits, en trozos de 100 ms) no hay voz.</summary>
    const double Quiet = 350;
    /// <summary>Con silencio o ruido, Whisper «oye» frases de los subtítulos con que se entrenó.</summary>
    static readonly string[] Phantoms = ["amara.org", "subtítulos realizados", "subtitulado por", "suscríbete", "gracias por ver"];
    /// <summary>El tono y las palabras que se esperan: ayuda a escribir bien «Claude» y los signos de pregunta.</summary>
    const string Prompt = "Hola, Claude. ¿Cómo van mis sesiones de Claude Code?";

    readonly string _model;
    readonly SemaphoreSlim _one = new(1, 1);
    readonly object _gate = new();
    WhisperFactory? _factory;
    bool _disposed;

    public Transcriber(string? model = null) => _model = model ?? VoiceModel.FilePath;

    static int Threads => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    /// <summary>Carga el modelo (tarda un poco): se llama al empezar a escuchar, mientras la persona habla.</summary>
    public Task WarmAsync() => Task.Run(Factory);

    WhisperFactory Factory()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _factory ??= WhisperFactory.FromPath(_model);
        }
    }

    /// <summary>El texto de lo grabado; vacío si no hubo voz (o solo se «oyó» una frase fantasma).</summary>
    public async Task<string> TranscribeAsync(short[] pcm, CancellationToken cancel = default)
    {
        if (!HasVoice(pcm)) return "";
        await _one.WaitAsync(cancel);
        try
        {
            return await Task.Run(async () =>
            {
                var padded = Padded(pcm);
                // await using: si se cancela a mitad (un trozo parcial, al soltar la tecla), Dispose fallaría
                // mientras Whisper todavía procesa; DisposeAsync espera a que termine.
                await using var processor = Factory().CreateBuilder()
                    .WithLanguage("es")
                    .WithPrompt(Prompt)
                    .WithThreads(Threads)
                    .WithNoSpeechThreshold(0.6f)
                    .WithAudioContextSize(AudioContext(padded.Length))
                    .Build();
                using var wav = Wav(padded);
                var text = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(wav, cancel))
                    if (segment.NoSpeechProbability < 0.6f) text.Append(segment.Text);
                return Clean(text.ToString());
            }, cancel);
        }
        finally
        {
            lock (_gate)
                if (_disposed) { _factory?.Dispose(); _factory = null; }
            _one.Release();
        }
    }

    static bool HasVoice(short[] pcm)
    {
        for (var start = 0; start < pcm.Length; start += 1600)
        {
            var end = Math.Min(pcm.Length, start + 1600);
            double sum = 0;
            for (var i = start; i < end; i++) sum += (double)pcm[i] * pcm[i];
            if (Math.Sqrt(sum / (end - start)) > Quiet) return true;
        }
        return false;
    }

    /// <summary>Con silencio al final (Whisper corta la última palabra si el audio termina de golpe) y al menos 1 s.</summary>
    static short[] Padded(short[] pcm)
    {
        var padded = new short[Math.Max(pcm.Length + 4000, 17600)];
        pcm.CopyTo(padded, 0);
        return padded;
    }

    /// <summary>
    /// La ventana del codificador, del largo de lo dicho (50 por segundo, más un margen) en vez de los 30 s de
    /// siempre: una frase de 3 s pasa de unos 5 s a menos de 1 s, y acierta igual (medido con 4 frases).
    /// </summary>
    static int AudioContext(int samples) => Math.Min(1500, samples * 50 / 16000 + 64);

    /// <summary>Sin [Música], (risas), frases fantasma ni espacios de más.</summary>
    static string Clean(string text)
    {
        text = Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ");
        var sentences = Regex.Split(text, @"(?<=[.!?…])\s+")
            .Where(s => !Phantoms.Any(p => s.Contains(p, StringComparison.OrdinalIgnoreCase)));
        return Regex.Replace(string.Join(" ", sentences), @"\s+", " ").Trim().TrimStart('-', ' ');
    }

    /// <summary>Lo grabado como un WAV en memoria (16 kHz, 16 bits, mono), que es lo que lee Whisper.</summary>
    static MemoryStream Wav(short[] pcm)
    {
        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(pcm.Length * 2);
            foreach (var s in pcm) w.Write(s);
        }
        stream.Position = 0;
        return stream;
    }

    /// <summary>Suelta el modelo; si está pasando algo a texto, al terminar.</summary>
    public void Dispose()
    {
        lock (_gate) _disposed = true;
        if (!_one.Wait(0)) return;
        lock (_gate) { _factory?.Dispose(); _factory = null; }
        _one.Release();
    }
}

/// <summary>
/// La tecla para hablar (Ctrl derecho, por defecto), con un gancho de teclado de bajo nivel que no
/// se queda con ninguna tecla. Mantenerla un cuarto de segundo empieza (Began) y soltarla termina
/// (Ended). Si mientras tanto se aprieta otra tecla, era un atajo: se cancela. Dos toques rápidos dejan
/// el micrófono abierto hasta el toque siguiente. Esc cancela. Los avisos salen después, en el hilo de
/// la interfaz: el gancho tiene que volver enseguida o Windows lo quita. Una combinación grabada o la
/// tecla Copilot (las vigila KeyHook, que avisa con ComboDown y ComboUp) no se usa para otra cosa: con
/// un solo toque ya escucha sin mantenerla, y el toque siguiente envía.
/// </summary>
sealed class PushToTalk : IDisposable
{
    public event Action? Began, Ended, Canceled;

    const int HoldMs = 250, DoubleTapMs = 400;
    readonly HookProc _proc;
    readonly System.Windows.Forms.Timer _hold = new() { Interval = HoldMs };
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    IntPtr _hook;
    bool _down, _other, _active, _handsFree, _comboDown, _comboSent;
    long _downAt, _lastTap;

    public Keys Key { get; set; } = Keys.RControlKey;

    public bool Active => _active;

    /// <summary>Escucha sin mantener la tecla (dos toques o el botón): termina con el toque siguiente.</summary>
    public bool HandsFree => _active && _handsFree;

    /// <summary>Vuelve a esperar la tecla, sin avisar (no se pudo abrir el micrófono, por ejemplo).</summary>
    public void Reset() => _active = _handsFree = false;

    /// <summary>Empieza como si se mantuviera la tecla (para las pruebas; termina con Finish).</summary>
    public void Hold()
    {
        if (!_active) Start(handsFree: false);
    }

    void Raise(Action? handler)
    {
        if (handler is null) return;
        if (_ui is null) handler();
        else _ui.Post(_ => handler(), null);
    }

    public PushToTalk()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(13, _proc, GetModuleHandle(null), 0); // WH_KEYBOARD_LL
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            if ((_down && !_other || _comboDown) && !_active) Start(handsFree: false);
        };
    }

    /// <summary>Bajó la combinación: si ya escuchaba sin mantener, este toque envía; si no, se ve si la mantiene.</summary>
    public void ComboDown()
    {
        if (_comboDown) return;
        _comboDown = true;
        _comboSent = _active && _handsFree;
        if (_comboSent) Finish();
        else if (!_active) _hold.Start();
    }

    /// <summary>Subió: si la mantuvo, envía; si fue un toque, escucha hasta el toque siguiente (o una pausa).</summary>
    public void ComboUp()
    {
        if (!_comboDown) return;
        _comboDown = false;
        _hold.Stop();
        if (_comboSent) return;
        if (_active && !_handsFree) { _active = false; Raise(Ended); }
        else if (!_active) Start(handsFree: true);
    }

    void Start(bool handsFree)
    {
        _active = true;
        _handsFree = handsFree;
        Raise(Began);
    }

    /// <summary>Termina desde fuera (el botón del micrófono, por ejemplo).</summary>
    public void Finish()
    {
        if (!_active) return;
        _active = _handsFree = false;
        Raise(Ended);
    }

    /// <summary>Empieza a escuchar sin tecla (el botón del micrófono): hasta Finish o el próximo toque.</summary>
    public void StartHandsFree()
    {
        if (!_active) Start(handsFree: true);
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vk = (Keys)Marshal.ReadInt32(lParam);
            var message = (int)wParam;
            var down = message is 0x100 or 0x104; // WM_KEYDOWN, WM_SYSKEYDOWN
            var up = message is 0x101 or 0x105;
            var now = Environment.TickCount64;
            if (vk == Key)
            {
                if (down && !_down)
                {
                    _down = true;
                    _other = false;
                    _downAt = now;
                    if (!_active) _hold.Start();
                }
                else if (up && _down)
                {
                    _down = false;
                    _hold.Stop();
                    var tap = now - _downAt < HoldMs && !_other;
                    if (_active && !_handsFree) { _active = false; Raise(Ended); }
                    else if (_active && _handsFree && tap) Finish();
                    else if (!_active && tap)
                    {
                        if (now - _lastTap < DoubleTapMs) { _lastTap = 0; Start(handsFree: true); }
                        else _lastTap = now;
                    }
                }
            }
            else if (down)
            {
                if (vk == Keys.Escape && _active)
                {
                    _active = _handsFree = false;
                    Raise(Canceled);
                }
                else if (_down)
                {
                    _other = true; // era un atajo (Ctrl derecho + otra tecla)
                    _hold.Stop();
                    if (_active && !_handsFree) { _active = false; Raise(Canceled); }
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        _hold.Dispose();
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}

/// <summary>
/// Hablarle a la mascota o a la isla: la tecla (o el botón del micrófono) abre el micrófono; mientras
/// se habla, lo dicho se va pasando a texto (de a un trozo, si el equipo da abasto) y al terminar se
/// pasa todo. Todo avisa en el hilo de la interfaz. El modelo se carga al empezar a escuchar y se suelta
/// a los 3 minutos sin uso.
/// </summary>
sealed class VoiceInput : IDisposable
{
    /// <summary>Empezó a escuchar.</summary>
    public event Action? Started;
    /// <summary>El nivel de la voz, de 0 a 1, cada 100 ms.</summary>
    public event Action<float>? Level;
    /// <summary>Lo entendido hasta ahora, mientras sigue escuchando.</summary>
    public event Action<string>? Partial;
    /// <summary>Terminó de escuchar: está pasando todo a texto.</summary>
    public event Action? Converting;
    /// <summary>El texto final; vacío si no se entendió nada.</summary>
    public event Action<string>? Heard;
    /// <summary>Se canceló (Esc, otra tecla con la de hablar, o un toque sin hablar).</summary>
    public event Action? Canceled;
    /// <summary>Se pidió escuchar pero falta el modelo de voz.</summary>
    public event Action? NeedsModel;
    /// <summary>Algo falló (el micrófono, Whisper): el mensaje para la persona.</summary>
    public event Action<string>? Failed;

    const int MinSamples = 16000 * 4 / 10;  // menos de 0,4 s: fue un toque, no se habló
    const int PartialStep = 16000 * 7 / 10; // otro trozo cuando hay 0,7 s nuevos
    // Sin mantener la tecla, también se envía al callar (PauseMs después de hablar) o si no se dice nada en WaitMs.
    const float SpeechLevel = 350f / 6000;  // el umbral de Transcriber, en la escala de Microphone.Level
    const int PauseMs = 1800, WaitMs = 8000;
    readonly PushToTalk _key = new();
    readonly Microphone _mic = new();
    readonly System.Windows.Forms.Timer _partialTimer = new() { Interval = 400 };
    readonly System.Windows.Forms.Timer _idle = new() { Interval = 3 * 60_000 };
    Transcriber? _transcriber;
    CancellationTokenSource? _partialCancel;
    Task? _partial;
    int _partialSamples, _take;
    bool _buttonPending, _byButton, _spoke;
    long _listenAt, _voiceAt;

    public VoiceInput()
    {
        _key.Began += Begin;
        _key.Ended += () => _ = EndAsync();
        _key.Canceled += Cancel;
        _mic.Level += level =>
        {
            Level?.Invoke(level);
            if (!_key.HandsFree || !_mic.Recording) return;
            var now = Environment.TickCount64;
            if (level > SpeechLevel) { _spoke = true; _voiceAt = now; }
            else if (_spoke ? now - _voiceAt > PauseMs : now - _listenAt > WaitMs) _key.Finish();
        };
        _partialTimer.Tick += (_, _) => MaybePartial();
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            if (_mic.Recording) return;
            _transcriber?.Dispose();
            _transcriber = null;
        };
    }

    public Keys Key { get => _key.Key; set => _key.Key = value; }

    /// <summary>Una combinación grabada o la tecla Copilot (KeyHook): un toque escucha sin mantenerla; mantenerla, hasta soltarla.</summary>
    public void ComboDown() => _key.ComboDown();

    public void ComboUp() => _key.ComboUp();

    public bool Listening => _mic.Recording;

    Transcriber Transcriber => _transcriber ??= new Transcriber();

    /// <summary>Cómo se termina, para mostrarlo mientras escucha.</summary>
    public string Hint(string keyLabel) =>
        _byButton ? "Envía al callar o con el micrófono" : _key.HandsFree ? $"Envía al callar o con {keyLabel}" : $"Suelte {keyLabel}";

    /// <summary>El botón del micrófono: empieza a escuchar (sin mantener la tecla) o termina y envía.</summary>
    public void Toggle()
    {
        if (_key.Active) { _key.Finish(); return; }
        _buttonPending = true;
        _key.StartHandsFree();
    }

    void Begin()
    {
        _byButton = _buttonPending;
        _buttonPending = false;
        if (!VoiceModel.Ready)
        {
            _key.Reset();
            NeedsModel?.Invoke();
            return;
        }
        if (!_mic.Start())
        {
            _key.Reset();
            Failed?.Invoke("No encuentro el micrófono, o Windows no deja usarlo (Configuración › Privacidad › Micrófono).");
            return;
        }
        _take++;
        _partialSamples = 0;
        _spoke = false;
        _listenAt = Environment.TickCount64;
        _idle.Stop();
        _partialTimer.Start();
        _ = Transcriber.WarmAsync().ContinueWith(t => ErrorLog.Write(t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
        Started?.Invoke();
    }

    /// <summary>Si hay voz nueva y nada pasándose a texto, pasa lo grabado hasta ahora (para verlo mientras se habla).</summary>
    void MaybePartial()
    {
        if (!_mic.Recording || _partial is { IsCompleted: false }) return;
        var pcm = _mic.Snapshot();
        if (pcm.Length - _partialSamples < PartialStep) return;
        _partialSamples = pcm.Length;
        var take = _take;
        var cancel = _partialCancel = new CancellationTokenSource();
        _partial = PartialAsync(pcm, take, cancel.Token);
    }

    async Task PartialAsync(short[] pcm, int take, CancellationToken cancel)
    {
        try
        {
            var text = await Transcriber.TranscribeAsync(pcm, cancel);
            if (take == _take && _mic.Recording && text.Length > 0) Partial?.Invoke(text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    async Task EndAsync()
    {
        if (!_mic.Recording) return;
        _partialTimer.Stop();
        var take = _take;
        var pcm = _mic.Stop();
        _partialCancel?.Cancel(); // lo de a trozos ya no hace falta: se pasa todo
        if (pcm.Length < MinSamples)
        {
            Canceled?.Invoke();
            _idle.Start();
            return;
        }
        Converting?.Invoke();
        try
        {
            if (_partial is { } partial) await partial;
            var text = await Transcriber.TranscribeAsync(pcm);
            if (take == _take) Heard?.Invoke(text);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            if (take == _take) Failed?.Invoke("No pude pasar la voz a texto.");
        }
        _idle.Start();
    }

    /// <summary>
    /// Para las pruebas (--hablar archivo.wav, de 16 kHz, mono y 16 bits): como si se mantuviera la tecla
    /// mientras suena ese audio, que reemplaza al micrófono esa vez.
    /// </summary>
    public async Task DemoAsync(string wav)
    {
        var bytes = await File.ReadAllBytesAsync(wav);
        var at = 12; // después de «RIFF», el largo y «WAVE»: los trozos, hasta «data»
        while (at + 8 <= bytes.Length && Encoding.ASCII.GetString(bytes, at, 4) != "data") at += 8 + BitConverter.ToInt32(bytes, at + 4);
        if (at + 8 > bytes.Length) return;
        var pcm = new short[Math.Min(BitConverter.ToInt32(bytes, at + 4), bytes.Length - at - 8) / 2];
        Buffer.BlockCopy(bytes, at + 8, pcm, 0, pcm.Length * 2);
        _mic.Fake = pcm;
        _key.Hold();
        await Task.Delay(pcm.Length / 16 + 400);
        _key.Finish();
        _mic.Fake = null;
    }

    /// <summary>Deja de escuchar sin enviar nada.</summary>
    public void Cancel()
    {
        _key.Reset();
        _partialTimer.Stop();
        _partialCancel?.Cancel();
        _take++;
        if (!_mic.Recording) return;
        _mic.Stop();
        _idle.Start();
        Canceled?.Invoke();
    }

    public void Dispose()
    {
        _partialTimer.Dispose();
        _idle.Dispose();
        _partialCancel?.Cancel();
        _key.Dispose();
        _mic.Dispose();
        _transcriber?.Dispose();
    }
}

/// <summary>
/// Lee las respuestas en voz alta (opcional). Con la voz de Claupet (Supertonic, femenina o masculina)
/// si su modelo ya está en el equipo: frase por frase, sonando la primera mientras se arma la
/// siguiente, y con las risas o suspiros que haya puesto Claude. Si no, o si se eligió, con una voz de
/// Windows en español. El modelo cargado ocupa unos 550 MB de memoria: se suelta a los 3 minutos sin hablar.
/// </summary>
sealed class ReadAloud : IDisposable
{
    static readonly TimeSpan KeepLoaded = TimeSpan.FromMinutes(3);
    const float Pause = 0.15f; // segundos entre frases

    readonly object _engineLock = new();
    readonly Dictionary<string, Supertonic.Voice> _voices = new();
    readonly System.Threading.Timer _idle;
    System.Speech.Synthesis.SpeechSynthesizer? _windows;
    Supertonic? _engine;
    CancellationTokenSource? _speaking;
    WaveOut? _output;

    public ReadAloud() => _idle = new System.Threading.Timer(_ => Unload());

    public void Say(string text, string voice)
    {
        Stop();
        // Sin las caritas de texto, que se leerían letra por letra.
        var clean = Regex.Replace(text, @"\([^\p{L}\d\s]{1,8}\)|[\\/]\(.{1,6}\)[\\/]|[♪♫]", " ").Trim();
        if (clean.Length == 0) return;
        if (ClaupetVoices.IsClaupet(voice) && ClaupetVoiceModel.Ready)
        {
            var cancel = _speaking = new CancellationTokenSource();
            _ = Task.Run(() => SpeakClaupet(clean, voice, cancel.Token));
        }
        else SayWithWindows(Expressions.Strip(clean));
    }

    void SpeakClaupet(string text, string voice, CancellationToken cancel)
    {
        try
        {
            _idle.Change(Timeout.Infinite, Timeout.Infinite);
            WaveOut? output = null;
            try
            {
                foreach (var chunk in Supertonic.Chunks(text))
                {
                    float[] samples;
                    int rate;
                    lock (_engineLock)
                    {
                        if (cancel.IsCancellationRequested) return;
                        var engine = _engine ??= new Supertonic(ClaupetVoiceModel.Folder);
                        if (!_voices.TryGetValue(voice, out var style))
                            _voices[voice] = style = Supertonic.LoadVoice(ClaupetVoiceModel.VoiceFile(voice));
                        samples = engine.Speak(chunk, style);
                        rate = engine.SampleRate;
                    }
                    if (cancel.IsCancellationRequested) return;
                    if (output is null) _output = output = new WaveOut(rate);
                    else output.Play(new float[(int)(Pause * rate)]);
                    output.Play(samples);
                }
                output?.Drain(cancel);
            }
            finally
            {
                if (output is not null)
                {
                    Interlocked.CompareExchange(ref _output, null, output);
                    output.Dispose();
                }
                _idle.Change(KeepLoaded, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ErrorLog.Write(ex); }
    }

    void SayWithWindows(string text)
    {
        try
        {
            if (_windows is null)
            {
                _windows = new System.Speech.Synthesis.SpeechSynthesizer();
                _windows.SetOutputToDefaultAudioDevice();
                var spanish = _windows.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "es");
                if (spanish is not null) _windows.SelectVoice(spanish.VoiceInfo.Name);
            }
            _windows.SpeakAsync(text);
        }
        catch (Exception) { }
    }

    public void Stop()
    {
        _speaking?.Cancel();
        _speaking = null;
        _output?.Stop();
        try { _windows?.SpeakAsyncCancelAll(); } catch (Exception) { }
    }

    /// <summary>Suelta el modelo (y su memoria) si no está hablando.</summary>
    void Unload()
    {
        lock (_engineLock)
        {
            _engine?.Dispose();
            _engine = null;
            _voices.Clear();
        }
    }

    public void Dispose()
    {
        Stop();
        _idle.Dispose();
        Unload();
        _windows?.Dispose();
    }
}
