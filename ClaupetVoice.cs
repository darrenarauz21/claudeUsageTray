using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// El modelo de la voz de Claupet (Supertonic 3, unos 400 MB): solo los archivos que se usan, de una
/// versión fija de Hugging Face y con su SHA-256. Se descarga una vez, al elegir una voz de Claupet, a
/// la carpeta de la app; después todo funciona sin internet.
/// </summary>
static class ClaupetVoiceModel
{
    const string Base = "https://huggingface.co/supertone-oss-archive/supertonic-3/resolve/aafc6e32416a594460b32413efc49d7fe4ce6d46/";

    static readonly (string Path, long Size, string Sha256)[] Files =
    [
        ("onnx/duration_predictor.onnx", 3700147, "c3eb91414d5ff8a7a239b7fe9e34e7e2bf8a8140d8375ffb14718b1c639325db"),
        ("onnx/text_encoder.onnx", 36416150, "c7befd5ea8c3119769e8a6c1486c4edc6a3bc8365c67621c881bbb774b9902ff"),
        ("onnx/vector_estimator.onnx", 256534781, "883ac868ea0275ef0e991524dc64f16b3c0376efd7c320af6b53f5b780d7c61c"),
        ("onnx/vocoder.onnx", 101424195, "085de76dd8e8d5836d6ca66826601f615939218f90e519f70ee8a36ed2a4c4ba"),
        ("onnx/tts.json", 8253, "42078d3aef1cd43ab43021f3c54f47d2d75ceb4e75f627f118890128b06a0d09"),
        ("onnx/unicode_indexer.json", 277676, "9bf7346e43883a81f8645c81224f786d43c5b57f3641f6e7671a7d6c493cb24f"),
        ("voice_styles/F4.json", 291808, "c2fa764c1225a76dfc3e2c73e8aa4f70d9ee48793860eb34c295fff01c2e032b"),
        ("voice_styles/M1.json", 291748, "e35604687f5d23694b8e91593a93eec0e4eca6c0b02bb8ed69139ab2ea6b0a5b"),
        ("LICENSE", 15007, "0d944a9110fed9a9602d60e0423a272903e7bd21ab060490774efc77c2275e9f"),
    ];

    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray", "voz", "supertonic-3");

    public static long Size => Files.Sum(f => f.Size);

    static string Local(string path) => Path.Combine(Folder, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Están todos los archivos (y no a medias).</summary>
    public static bool Ready => Files.All(f => File.Exists(Local(f.Path)) && new FileInfo(Local(f.Path)).Length == f.Size);

    public static string VoiceFile(string voice) => Local($"voice_styles/{voice}.json");

    /// <summary>Baja los que falten, uno tras otro, y avisa el avance de 0 a 1 sobre el total.</summary>
    public static async Task DownloadAsync(IProgress<double> progress, CancellationToken cancel)
    {
        long done = 0;
        foreach (var (path, size, sha256) in Files)
        {
            var local = Local(path);
            if (!File.Exists(local) || new FileInfo(local).Length != size)
            {
                var before = done;
                await ModelDownload.FetchAsync(Base + path, local, size, sha256,
                    new Progress<long>(have => progress.Report((double)(before + have) / Size)), cancel);
            }
            done += size;
            progress.Report((double)done / Size);
        }
    }
}

/// <summary>
/// La salida de audio por WinMM (waveOut), aparte de PlaySound (que usan los sonidos de los avisos y
/// se cortarían entre sí): mono, 16 bits. Los trozos se encolan a medida que se generan y suenan seguidos.
/// </summary>
sealed class WaveOut : IDisposable
{
    readonly AutoResetEvent _done = new(false);
    readonly List<(IntPtr Header, IntPtr Data)> _queued = new();
    IntPtr _device;

    public WaveOut(int rate)
    {
        var format = new WaveFormat { Tag = 1, Channels = 1, SamplesPerSec = rate, AvgBytesPerSec = rate * 2, BlockAlign = 2, BitsPerSample = 16 };
        if (waveOutOpen(out _device, -1, ref format, _done.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x50000) != 0) // CALLBACK_EVENT
            throw new IOException("No hay salida de audio.");
    }

    /// <summary>Encola un trozo (muestras de -1 a 1); suena cuando termine el anterior.</summary>
    public void Play(float[] samples)
    {
        var pcm = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++) pcm[i] = (short)(Math.Clamp(samples[i], -1f, 1f) * 32767);
        var bytes = pcm.Length * 2;
        var data = Marshal.AllocHGlobal(bytes);
        Marshal.Copy(pcm, 0, data, pcm.Length);
        var size = Marshal.SizeOf<WaveHeader>();
        var header = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = (uint)bytes }, header, false);
        waveOutPrepareHeader(_device, header, size);
        waveOutWrite(_device, header, size);
        lock (_queued) _queued.Add((header, data));
    }

    /// <summary>Espera a que suene todo lo encolado; si se cancela, corta en seco.</summary>
    public void Drain(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            lock (_queued)
                if (_queued.All(q => (Marshal.PtrToStructure<WaveHeader>(q.Header).Flags & 1) != 0)) return; // WHDR_DONE
            WaitHandle.WaitAny([_done, cancel.WaitHandle], 200);
        }
    }

    /// <summary>Corta lo que suena (se puede llamar desde otro hilo).</summary>
    public void Stop()
    {
        lock (_queued)
            if (_device != IntPtr.Zero) waveOutReset(_device);
    }

    public void Dispose()
    {
        var size = Marshal.SizeOf<WaveHeader>();
        lock (_queued)
        {
            if (_device == IntPtr.Zero) return;
            waveOutReset(_device); // lo que quedaba sin sonar vuelve como terminado
            foreach (var (header, data) in _queued)
            {
                waveOutUnprepareHeader(_device, header, size);
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(data);
            }
            _queued.Clear();
            waveOutClose(_device);
            _device = IntPtr.Zero;
        }
        _done.Dispose();
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

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr device, int id, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr device);
}
