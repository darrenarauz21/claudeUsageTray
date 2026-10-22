using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeUsageTray;

/// <summary>
/// Sonidos de los avisos, en %APPDATA%\ClaudeUsageTray\sonidos: un estilo por carpeta («clasico»,
/// «risitas», «chibi»), generados por la app, y «propios», donde un archivo con el mismo nombre (.wav o .mp3)
/// manda sobre el estilo elegido. Un sonido borrado de un estilo vuelve a generarse.
/// </summary>
sealed class Sounds : IDisposable
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray", "sonidos");
    public static string CustomFolder => Path.Combine(Folder, "propios");

    public static readonly (string Id, string Label)[] Packs = [("clasico", "Clásico"), ("risitas", "Risitas"), ("chibi", "Chibi")];
    static readonly string[] Names = ["terminado", "pregunta", "permiso", "fallo", "uso", "aviso"];

    enum Voice { Happy, Question, Knock, Sad, Neutral, Short }

    long _lastPlayed;

    public static string FileFor(NoticeKind kind) => kind switch
    {
        NoticeKind.Done or NoticeKind.UsageReset => "terminado",
        NoticeKind.Question => "pregunta",
        NoticeKind.Permission => "permiso",
        NoticeKind.Failed or NoticeKind.Closed or NoticeKind.Expired => "fallo",
        NoticeKind.UsageHigh or NoticeKind.UsageCritical => "uso",
        _ => "aviso",
    };

    static string PackFolder(string pack) => Path.Combine(Folder, pack);

    static byte[] Generate(string pack, string name)
    {
        var index = Array.IndexOf(Names, name);
        return pack switch
        {
            "risitas" => SynthesizeGiggle(index),
            "chibi" => name == "fallo" ? SynthesizeUwa() : SynthesizeOi(index),
            _ => Synthesize((Voice)index),
        };
    }

    /// <summary>Crea las carpetas y repone los sonidos de cada estilo que falten.</summary>
    public static void EnsureDefaults()
    {
        try
        {
            Directory.CreateDirectory(CustomFolder);
            MigrateLooseFiles();
            foreach (var (pack, _) in Packs)
            {
                Directory.CreateDirectory(PackFolder(pack));
                // Si el estilo cambió en una versión nueva de la app, se vuelven a escribir sus sonidos.
                var versionFile = Path.Combine(PackFolder(pack), ".version");
                var outdated = !File.Exists(versionFile) || File.ReadAllText(versionFile).Trim() != PackVersions[pack];
                foreach (var name in Names)
                {
                    var file = Path.Combine(PackFolder(pack), name + ".wav");
                    if (outdated || !File.Exists(file)) File.WriteAllBytes(file, Generate(pack, name));
                }
                if (outdated) File.WriteAllText(versionFile, PackVersions[pack]);
            }
            var readme = Path.Combine(Folder, "LEEME.txt");
            const string text =
                "Sonidos de los avisos de Uso de Claude.\r\n\r\n"
                + "terminado: terminó o límite reiniciado\r\npregunta: le está preguntando\r\n"
                + "permiso: necesita su permiso\r\nfallo: falló, se cerró o sesión vencida\r\n"
                + "uso: uso al 80 o 95 %\r\naviso: reintentando\r\n\r\n"
                + "clasico, risitas y chibi: los estilos de la app; se elige en Ajustes (Estilo de sonido).\r\n"
                + "propios: ponga aquí un archivo con el mismo nombre (.wav o .mp3) y sonará ese en lugar del estilo.\r\n"
                + "Si borra un sonido de un estilo, la app lo vuelve a crear.\r\n";
            if (!File.Exists(readme) || File.ReadAllText(readme) != text) File.WriteAllText(readme, text, Encoding.UTF8);
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Versiones anteriores dejaban los sonidos sueltos en la carpeta. Los que son el original se
    /// borran (ahora viven en «clasico»); los que la persona reemplazó pasan a «propios».
    /// </summary>
    static void MigrateLooseFiles()
    {
        foreach (var name in Names)
            foreach (var ext in new[] { ".wav", ".mp3" })
            {
                var loose = Path.Combine(Folder, name + ext);
                if (!File.Exists(loose)) continue;
                var isOriginal = ext == ".wav" && File.ReadAllBytes(loose).AsSpan().SequenceEqual(Generate("clasico", name));
                var target = Path.Combine(CustomFolder, name + ext);
                if (isOriginal || File.Exists(target)) File.Delete(loose);
                else File.Move(loose, target);
            }
    }

    public void Play(NoticeKind kind, string pack, bool force = false)
    {
        // Si llegan varios avisos juntos, suena uno.
        var now = Environment.TickCount64;
        if (!force && now - _lastPlayed < 600) return;
        _lastPlayed = now;

        EnsureDefaults();
        var name = FileFor(kind);
        var file = Newest(CustomFolder, name) ?? Newest(PackFolder(Packs.Any(p => p.Id == pack) ? pack : "clasico"), name);
        if (file is null) return;

        try
        {
            PlayFile(file);
        }
        catch (Exception) { }
    }

    /// <summary>El .wav o .mp3 con ese nombre; si están los dos, el más reciente.</summary>
    static string? Newest(string folder, string name) =>
        new[] { name + ".wav", name + ".mp3" }
            .Select(f => new FileInfo(Path.Combine(folder, f))).Where(f => f.Exists)
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;

    /// <summary>Reproduce sin esperar a que termine, con lo que tenga cada sistema.</summary>
    static void PlayFile(string file)
    {
        var mp3 = file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            if (mp3) PlayMp3(file);
            else PlaySound(file, IntPtr.Zero, SndAsync | SndFilename | SndNoDefault);
            return;
        }
        if (OperatingSystem.IsMacOS()) { Run("afplay", file); return; }

        // Linux: el primero que exista.
        string[][] players = mp3
            ? [["ffplay", "-nodisp", "-autoexit", "-loglevel", "quiet"], ["mpg123", "-q"], ["paplay"]]
            : [["paplay"], ["pw-play"], ["aplay", "-q"], ["ffplay", "-nodisp", "-autoexit", "-loglevel", "quiet"]];
        foreach (var player in players)
            if (Run(player[0], [.. player[1..], file])) return;
    }

    static bool Run(string program, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception) { return false; }
    }

    const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndFilename = 0x00020000;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern bool PlaySound(string sound, IntPtr module, uint flags);

    // ── MP3 con MCI (winmm), sin dependencias ───────────────────────────────────────────────

    const string Alias = "ClaudeUsageTraySound";

    static void PlayMp3(string file)
    {
        mciSendString($"close {Alias}", null, 0, IntPtr.Zero);
        if (mciSendString($"open \"{file}\" type mpegvideo alias {Alias}", null, 0, IntPtr.Zero) == 0)
            mciSendString($"play {Alias}", null, 0, IntPtr.Zero);
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr callback);

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) mciSendString($"close {Alias}", null, 0, IntPtr.Zero);
    }

    // ── Síntesis del «moe» ──────────────────────────────────────────────────────────────────

    const int Rate = 44100;

    /// <summary>
    /// Una voz chiquita: fuente armónica con la altura que sube o baja, dos formantes que pasan de
    /// la «o» a la «e», una «m» nasal al principio, vibrato suave y envolvente.
    /// </summary>
    static byte[] Synthesize(Voice voice)
    {
        var syllables = voice switch
        {
            Voice.Happy => new[] { new Syllable(0.30, 520, 800) },
            Voice.Question => new[] { new Syllable(0.36, 470, 520, 880) },
            Voice.Knock => new[] { new Syllable(0.15, 560, 640), new Syllable(0.15, 560, 680, Gap: 0.07) },
            Voice.Sad => new[] { new Syllable(0.46, 560, 330) },
            Voice.Neutral => new[] { new Syllable(0.28, 520, 600) },
            _ => new[] { new Syllable(0.13, 600, 660) },
        };

        var samples = new List<float>();
        foreach (var s in syllables)
        {
            samples.AddRange(new float[(int)(s.Gap * Rate)]);
            samples.AddRange(Syllable.Render(s));
        }
        samples.AddRange(new float[(int)(0.03 * Rate)]);

        var peak = samples.Max(Math.Abs);
        var gain = peak > 0 ? 0.6f / peak : 1f;
        return Wav(samples.Select(v => v * gain).ToArray());
    }

    /// <summary>Una sílaba: la altura va de Start a Middle; si hay End, sigue hasta End.</summary>
    sealed record Syllable(double Duration, double Start, double Middle, double End = double.NaN, double Gap = 0)
    {
        public static float[] Render(Syllable s)
        {
            var n = (int)(s.Duration * Rate);
            var output = new float[n];
            var phase = 0.0;
            var (f1, f2) = (new Resonator(), new Resonator());
            for (var i = 0; i < n; i++)
            {
                var t = i / (double)n;
                // Altura: de Start a Middle (y a End si lo hay), con un vibrato de 6 Hz.
                var pitch = double.IsNaN(s.End)
                    ? s.Start + (s.Middle - s.Start) * Smooth(t)
                    : t < 0.6 ? s.Start + (s.Middle - s.Start) * Smooth(t / 0.6) : s.Middle + (s.End - s.Middle) * Smooth((t - 0.6) / 0.4);
                pitch *= 1 + 0.012 * Math.Sin(2 * Math.PI * 6 * i / Rate);
                phase += 2 * Math.PI * pitch / Rate;

                // Fuente: armónicos con caída 1/k, sin pasar de 9 kHz.
                var source = 0.0;
                for (var k = 1; k * pitch < 9000 && k <= 24; k++) source += Math.Sin(k * phase) / k;

                // «m» nasal en el primer 12 %; luego «o» que se vuelve «e».
                var nasal = t < 0.12;
                var vowel = Smooth(Math.Clamp((t - 0.2) / 0.6, 0, 1));
                var formant1 = nasal ? 280 : 560 - 140 * vowel;
                var formant2 = nasal ? 1100 : 950 + 1250 * vowel;
                var voiced = 0.65 * f1.Next(source, formant1, 110) + 0.45 * f2.Next(source, formant2, 160);
                var sample = nasal ? 0.35 * voiced : voiced;

                // Envolvente: ataque de 12 ms y caída en el último 30 %.
                var attack = Math.Min(1, i / (0.012 * Rate));
                var release = t > 0.7 ? Math.Cos((t - 0.7) / 0.3 * Math.PI / 2) : 1;
                output[i] = (float)(sample * attack * release);
            }
            return output;
        }

        static double Smooth(double x) => x * x * (3 - 2 * x);
    }

    /// <summary>Resonador de dos polos con frecuencia variable (un formante).</summary>
    sealed class Resonator
    {
        double _y1, _y2;

        public double Next(double x, double frequency, double bandwidth)
        {
            var r = Math.Exp(-Math.PI * bandwidth / Rate);
            var b1 = 2 * r * Math.Cos(2 * Math.PI * frequency / Rate);
            var b2 = -r * r;
            var y = (1 - r) * x + b1 * _y1 + b2 * _y2;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }

    // ── Síntesis de las risitas: «jejeje» / «jijiji» ────────────────────────────────────────

    /// <summary>Versión de cada estilo: si cambia, la app vuelve a escribir sus archivos.</summary>
    static readonly Dictionary<string, string> PackVersions = new() { ["clasico"] = "1", ["risitas"] = "3", ["chibi"] = "7" };

    /// <summary>
    /// Una risa: sílabas «je» o «ji» (Vowel 0 = e, 1 = i), una cada Period segundos, con el tono
    /// bajando de StartPitch a EndPitch y el volumen apagándose según Fade.
    /// </summary>
    sealed record Laugh(int Syllables, double StartPitch, double EndPitch, double Vowel = 0, double Period = 0.19,
        double Fade = 0.35, double FinalRise = 0, double PauseBefore = 0);

    /// <summary>terminado, pregunta, permiso, fallo, uso, aviso (mismo orden que Names).</summary>
    static byte[] SynthesizeGiggle(int index)
    {
        var laughs = index switch
        {
            0 => new[] { new Laugh(5, 470, 360) },                                               // jejejeje, contento
            1 => new[] { new Laugh(2, 500, 470, Vowel: 1), new Laugh(1, 430, 430, FinalRise: 0.35, PauseBefore: 0.08) }, // jiji… ¿je?
            2 => new[] { new Laugh(2, 470, 440, Period: 0.16), new Laugh(2, 470, 440, Period: 0.16, PauseBefore: 0.14) }, // je-je, je-je
            3 => new[] { new Laugh(3, 400, 280, Period: 0.32, Fade: 0.6) },                      // je… je… je, triste
            4 => new[] { new Laugh(4, 580, 540, Vowel: 1, Period: 0.17, Fade: 0.2) },            // jijiji, nervioso
            _ => new[] { new Laugh(1, 460, 460) },                                               // je
        };

        var random = new Random(11 + index); // siempre el mismo sonido para el mismo caso
        var samples = new List<float>();
        foreach (var laugh in laughs)
        {
            samples.AddRange(new float[(int)(laugh.PauseBefore * Rate)]);
            for (var i = 0; i < laugh.Syllables; i++)
            {
                var k = laugh.Syllables == 1 ? 0 : i / (double)(laugh.Syllables - 1);
                var last = i == laugh.Syllables - 1;
                var syllable = Je(
                    pitch: laugh.StartPitch + (laugh.EndPitch - laugh.StartPitch) * k,
                    vowel: laugh.Vowel,
                    amplitude: 1 - laugh.Fade * k,
                    rise: last ? laugh.FinalRise : 0,
                    random);
                // Cada sílaba ocupa su periodo; si es más corta, el resto es silencio.
                var period = (int)(laugh.Period * (0.94 + 0.12 * random.NextDouble()) * Rate);
                samples.AddRange(syllable);
                if (!last) samples.AddRange(new float[Math.Max(0, period - syllable.Length)]);
            }
        }
        samples.AddRange(new float[(int)(0.04 * Rate)]);

        var peak = samples.Max(Math.Abs);
        var gain = peak > 0 ? 0.6f / peak : 1f;
        return Wav(samples.Select(v => v * gain).ToArray());
    }

    /// <summary>
    /// Una sílaba «je»/«ji»: 45 ms de «j» (aire filtrado por la boca, con algo de raspado arriba),
    /// luego la vocal con tres formantes, que empieza fuerte y se apaga. El tono cae un poco dentro
    /// de la sílaba, salvo la última de una pregunta, que sube.
    /// </summary>
    static float[] Je(double pitch, double vowel, double amplitude, double rise, Random random)
    {
        const double Consonant = 0.045;
        var vowelLength = rise > 0 ? 0.20 : 0.11;
        var n = (int)((Consonant + vowelLength) * Rate);
        var output = new float[n];
        var phase = 0.0;
        var (v1, v2, v3) = (new Resonator(), new Resonator(), new Resonator());
        var (n1, n2, rasp) = (new Resonator(), new Resonator(), new Resonator());

        // Formantes de «e» y de «i», un poco subidos para una voz aguda.
        var f1 = 560 - 230 * vowel;
        var f2 = 2050 + 350 * vowel;
        var f3 = 2850 + 350 * vowel;

        for (var i = 0; i < n; i++)
        {
            var time = i / (double)Rate;
            var white = random.NextDouble() * 2 - 1;

            // «j»: aire con la forma de la vocal que viene, más un raspado alrededor de 2,6 kHz.
            var consonant = 0.0;
            if (time < Consonant + 0.015)
            {
                var c = time / Consonant;
                var envelope = Math.Min(1, c * 4) * (time < Consonant ? 1 : 1 - (time - Consonant) / 0.015);
                consonant = envelope * (0.5 * n1.Next(white, f2, 500) + 0.35 * n2.Next(white, f3, 600)
                    + 0.6 * rasp.Next(white, 2600, 900));
            }

            // Vocal: fuente armónica con un poco de aire, tres formantes y envolvente que se apaga.
            var voiced = 0.0;
            if (time >= Consonant - 0.008)
            {
                var t = (time - (Consonant - 0.008)) / (vowelLength + 0.008);
                var p = pitch * (rise > 0 ? 1 + rise * t * t : 1.04 - 0.12 * t);
                phase += 2 * Math.PI * p / Rate;
                var source = 0.0;
                for (var h = 1; h * p < 9000 && h <= 30; h++) source += Math.Sin(h * phase) / Math.Pow(h, 1.1);
                source += 0.25 * white;
                var formants = v1.Next(source, f1, 90) + 0.55 * v2.Next(source, f2, 130) + 0.3 * v3.Next(source, f3, 180);
                var attack = Math.Min(1, t / 0.08);
                var decay = Math.Exp(-2.2 * t);
                var release = t > 0.85 ? Math.Cos((t - 0.85) / 0.15 * Math.PI / 2) : 1;
                voiced = formants * attack * decay * release;
            }

            output[i] = (float)(amplitude * (voiced + 0.9 * consonant));
        }
        return output;
    }

    // ── Síntesis del chibi: «¡oi!» (el «¡ey!» japonés), voz femenina de anime ─────────────────

    /// <summary>
    /// Un «oi» con la melodía del «uwaaa»: la altura sube de Start a Peak en el primer cuarto y baja
    /// despacio hasta End; OShare es la parte que dura la «o», larga y tan abierta que suena casi a
    /// «a» (como la del «uwaaa»), antes de cerrar en «i».
    /// Gap es el silencio antes.
    /// </summary>
    sealed record Oi(double Duration, double Start, double Peak, double End, double OShare = 0.6, double Gap = 0);

    /// <summary>terminado, pregunta, permiso, uso y aviso (mismo orden que Names); fallo es SynthesizeUwa.</summary>
    static byte[] SynthesizeOi(int index)
    {
        var calls = index switch
        {
            0 => new[] { new Oi(0.55, 300, 370, 260) },                                                     // ¡oooi!, contenta
            1 => new[] { new Oi(0.55, 290, 300, 420, OShare: 0.55) },                                       // ¿oooi?, sube al final
            2 => new[] { new Oi(0.30, 300, 360, 270, 0.55), new Oi(0.40, 310, 370, 250, Gap: 0.06) },       // oi, oooi
            4 => new[] { new Oi(0.75, 300, 360, 230, OShare: 0.7) },                                        // ¡ooooi!, llamando
            _ => new[] { new Oi(0.30, 320, 360, 280, 0.55) },                                               // oi, cortito
        };

        var random = new Random(23 + index); // siempre el mismo sonido para el mismo caso
        var samples = new List<float>();
        foreach (var call in calls)
        {
            samples.AddRange(new float[(int)(call.Gap * Rate)]);
            samples.AddRange(RenderOi(call, random));
        }
        samples.AddRange(new float[(int)(0.04 * Rate)]);

        var peak = samples.Max(Math.Abs);
        var gain = peak > 0 ? 0.6f / peak : 1f;
        return Wav(samples.Select(v => v * gain).ToArray());
    }

    /// <summary>
    /// La misma voz que el «uwaaa» (SynthesizeUwa): femenina adulta, limpia y sin vibrato, como una voz
    /// de lectura en japonés. Tres formantes pasan de la «o» casi «a» a la «i» (cerrada y brillante).
    /// </summary>
    static float[] RenderOi(Oi call, Random random)
    {
        var n = (int)(call.Duration * Rate);
        var output = new float[n];
        var phase = 0.0;
        var (r1, r2, r3) = (new Resonator(), new Resonator(), new Resonator());
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)n;
            var pitch = t < 0.25
                ? call.Start + (call.Peak - call.Start) * Smooth(t / 0.25)
                : call.Peak + (call.End - call.Peak) * Smooth((t - 0.25) / 0.75);
            phase += 2 * Math.PI * pitch / Rate;

            var source = 0.0;
            for (var k = 1; k * pitch < 9000 && k <= 30; k++) source += Math.Sin(k * phase) / k;
            source += 0.03 * (random.NextDouble() * 2 - 1);

            // «o» casi «a» → «i», con formantes de voz femenina adulta.
            var vowel = Smooth(Math.Clamp((t - call.OShare) / 0.2, 0, 1));
            var formant1 = 850 - 520 * vowel;
            var formant2 = 1300 + 1300 * vowel;
            var formant3 = 2800 + 400 * vowel;
            var voiced = r1.Next(source, formant1, 80) + 0.5 * r2.Next(source, formant2, 100) + 0.25 * r3.Next(source, formant3, 150);

            // Ataque de 15 ms, como el «uwaaa», y caída en el último 20 %.
            var attack = Math.Min(1, i / (0.015 * Rate));
            var release = t > 0.8 ? Math.Cos((t - 0.8) / 0.2 * Math.PI / 2) : 1;
            output[i] = (float)(voiced * attack * release);
        }
        return output;

        static double Smooth(double x) => x * x * (3 - 2 * x);
    }

    /// <summary>
    /// «¡Uwaaa!» (うわあああ), para lo que falló, con la voz del «oi»: femenina adulta, limpia y sin
    /// vibrato, como una voz de lectura en japonés. La «u» pasa por la «w» (más cerrada y un poco
    /// más baja de volumen) a una «a» larga; la altura sube en el «wa» y cae despacio en el «aaa».
    /// </summary>
    static byte[] SynthesizeUwa()
    {
        const double Duration = 0.9, Peak = 0.22;
        var n = (int)(Duration * Rate);
        var samples = new float[n + (int)(0.04 * Rate)];
        var phase = 0.0;
        var random = new Random(29);
        var (r1, r2, r3) = (new Resonator(), new Resonator(), new Resonator());
        for (var i = 0; i < n; i++)
        {
            var time = i / (double)Rate;
            var pitch = time < Peak
                ? 300 + 60 * Smooth(time / Peak)
                : 360 - 130 * Smooth((time - Peak) / (Duration - Peak));
            phase += 2 * Math.PI * pitch / Rate;

            var source = 0.0;
            for (var k = 1; k * pitch < 9000 && k <= 30; k++) source += Math.Sin(k * phase) / k;
            source += 0.03 * (random.NextDouble() * 2 - 1);

            // De «u» a «a»; a mitad de camino, la «w» cierra los labios: bajan los formantes y el volumen.
            var toA = Smooth(Math.Clamp((time - 0.10) / 0.12, 0, 1));
            var w = Math.Sin(Math.PI * toA);
            var formant1 = 370 + 580 * toA - 150 * w;
            var formant2 = 1500 + 50 * toA - 700 * w;
            var formant3 = 2800 + 200 * toA;
            var voiced = r1.Next(source, formant1, 80) + 0.5 * r2.Next(source, formant2, 100) + 0.25 * r3.Next(source, formant3, 150);

            var attack = Math.Min(1, time / 0.015);
            var t = time / Duration;
            var release = t > 0.75 ? Math.Cos((t - 0.75) / 0.25 * Math.PI / 2) : 1;
            samples[i] = (float)(voiced * (1 - 0.45 * w) * attack * release);
        }

        var peak = samples.Max(Math.Abs);
        var gain = peak > 0 ? 0.6f / peak : 1f;
        return Wav(samples.Select(v => v * gain).ToArray());

        static double Smooth(double x) => x * x * (3 - 2 * x);
    }

    static byte[] Wav(float[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
        foreach (var v in samples) w.Write((short)Math.Clamp(v * short.MaxValue, short.MinValue, short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
