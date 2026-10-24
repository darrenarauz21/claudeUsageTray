using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ClaudeUsageTray;

/// <summary>
/// La voz de Claupet: Supertonic 3 (Supertone, modelo con licencia OpenRAIL-M), en el equipo y con
/// ONNX Runtime en la CPU. Adaptado del ejemplo en C# de github.com/supertone-inc/supertonic (MIT,
/// Copyright (c) 2025 Supertone Inc.): la misma inferencia, para una sola voz y sin lo que la app no
/// usa. El texto entra tal cual (sin pasar a fonemas) y puede llevar &lt;laugh&gt;, &lt;sigh&gt; o
/// &lt;breath&gt;. Sale audio mono de 44,1 kHz.
/// </summary>
sealed class Supertonic : IDisposable
{
    /// <summary>Pasos de limpieza del ruido: con 8 (el valor del ejemplo) una frase de 5 s tarda unos 2 s en un Ryzen 7 5700U.</summary>
    const int Steps = 8;
    const float Speed = 1.05f;

    readonly InferenceSession _duration, _encoder, _estimator, _vocoder;
    readonly long[] _indexer;
    readonly int _chunkSize, _latentDim;

    public int SampleRate { get; }

    public Supertonic(string folder)
    {
        var onnx = Path.Combine(folder, "onnx");
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(onnx, "tts.json"))))
        {
            var ae = doc.RootElement.GetProperty("ae");
            var ttl = doc.RootElement.GetProperty("ttl");
            SampleRate = ae.GetProperty("sample_rate").GetInt32();
            var compress = ttl.GetProperty("chunk_compress_factor").GetInt32();
            _chunkSize = ae.GetProperty("base_chunk_size").GetInt32() * compress;
            _latentDim = ttl.GetProperty("latent_dim").GetInt32() * compress;
        }
        _indexer = JsonSerializer.Deserialize<long[]>(File.ReadAllText(Path.Combine(onnx, "unicode_indexer.json")))
            ?? throw new InvalidDataException("unicode_indexer.json vacío");
        using var options = new SessionOptions();
        _duration = new InferenceSession(Path.Combine(onnx, "duration_predictor.onnx"), options);
        _encoder = new InferenceSession(Path.Combine(onnx, "text_encoder.onnx"), options);
        _estimator = new InferenceSession(Path.Combine(onnx, "vector_estimator.onnx"), options);
        _vocoder = new InferenceSession(Path.Combine(onnx, "vocoder.onnx"), options);
    }

    /// <summary>Una voz (voice_styles/&lt;nombre&gt;.json): sus dos vectores de estilo, ya como tensores.</summary>
    public sealed record Voice(DenseTensor<float> Ttl, DenseTensor<float> Dp);

    public static Voice LoadVoice(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        DenseTensor<float> Read(string name)
        {
            var part = doc.RootElement.GetProperty(name);
            var dims = part.GetProperty("dims").EnumerateArray().Select(d => (int)d.GetInt64()).ToArray();
            var data = new List<float>();
            foreach (var batch in part.GetProperty("data").EnumerateArray())
                foreach (var row in batch.EnumerateArray())
                    foreach (var value in row.EnumerateArray()) data.Add(value.GetSingle());
            return new DenseTensor<float>(data.ToArray(), dims);
        }
        return new Voice(Read("style_ttl"), Read("style_dp"));
    }

    /// <summary>Una frase (o un trozo de hasta 300 caracteres) en español, como muestras de -1 a 1.</summary>
    public float[] Speak(string text, Voice voice)
    {
        var processed = Preprocess(text);
        var ids = processed.Select(c => c < _indexer.Length ? _indexer[c] : 0).ToArray();
        var textIds = new DenseTensor<long>(ids, [1, ids.Length]);
        var textMask = new DenseTensor<float>(Enumerable.Repeat(1f, ids.Length).ToArray(), [1, 1, ids.Length]);

        float seconds;
        using (var outputs = _duration.Run([
            NamedOnnxValue.CreateFromTensor("text_ids", textIds),
            NamedOnnxValue.CreateFromTensor("style_dp", voice.Dp),
            NamedOnnxValue.CreateFromTensor("text_mask", textMask)]))
            seconds = outputs.First(o => o.Name == "duration").AsTensor<float>().First() / Speed;

        using var encoded = _encoder.Run([
            NamedOnnxValue.CreateFromTensor("text_ids", textIds),
            NamedOnnxValue.CreateFromTensor("style_ttl", voice.Ttl),
            NamedOnnxValue.CreateFromTensor("text_mask", textMask)]);
        var textEmb = encoded.First(o => o.Name == "text_emb").AsTensor<float>();

        // Ruido gaussiano con la forma del audio latente; lo que pasa del largo queda en cero.
        var wavLength = seconds * SampleRate;
        var latentLength = (int)((wavLength + _chunkSize - 1) / _chunkSize);
        var maskLength = (int)(((long)wavLength + _chunkSize - 1) / _chunkSize);
        var mask = new float[maskLength];
        Array.Fill(mask, 1f);
        var latent = new float[_latentDim * latentLength];
        var random = new Random();
        for (var d = 0; d < _latentDim; d++)
            for (var t = 0; t < latentLength; t++)
            {
                var noise = Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * (1.0 - random.NextDouble()));
                latent[d * latentLength + t] = t < maskLength ? (float)noise : 0f;
            }
        var latentMask = new DenseTensor<float>(mask, [1, 1, maskLength]);
        var total = new DenseTensor<float>(new[] { (float)Steps }, [1]);

        for (var step = 0; step < Steps; step++)
        {
            using var denoised = _estimator.Run([
                NamedOnnxValue.CreateFromTensor("noisy_latent", new DenseTensor<float>(latent, [1, _latentDim, latentLength])),
                NamedOnnxValue.CreateFromTensor("text_emb", textEmb),
                NamedOnnxValue.CreateFromTensor("style_ttl", voice.Ttl),
                NamedOnnxValue.CreateFromTensor("text_mask", textMask),
                NamedOnnxValue.CreateFromTensor("latent_mask", latentMask),
                NamedOnnxValue.CreateFromTensor("total_step", total),
                NamedOnnxValue.CreateFromTensor("current_step", new DenseTensor<float>(new[] { (float)step }, [1]))]);
            latent = denoised.First(o => o.Name == "denoised_latent").AsTensor<float>().ToArray();
        }

        using var audio = _vocoder.Run([NamedOnnxValue.CreateFromTensor("latent", new DenseTensor<float>(latent, [1, _latentDim, latentLength]))]);
        var wav = audio.First(o => o.Name == "wav_tts").AsTensor<float>().ToArray();
        return wav[..Math.Min(wav.Length, (int)wavLength)];
    }

    /// <summary>La limpieza del texto del ejemplo, en el mismo orden, y la marca de idioma.</summary>
    static string Preprocess(string text)
    {
        text = text.Normalize(NormalizationForm.FormKD);
        text = WithoutEmojis(text);
        foreach (var (from, to) in Replacements) text = text.Replace(from, to);
        text = Regex.Replace(text, @"[♥☆♡©\\]", "");
        text = text.Replace("@", " at ");
        foreach (var mark in new[] { ",", ".", "!", "?", ";", ":", "'" }) text = text.Replace(" " + mark, mark);
        while (text.Contains("\"\"")) text = text.Replace("\"\"", "\"");
        while (text.Contains("''")) text = text.Replace("''", "'");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (!Regex.IsMatch(text, @"[.!?;:,'""“”‘’)\]}…。」』】〉》›»]$")) text += ".";
        return "<es>" + text + "</es>";
    }

    static readonly (string From, string To)[] Replacements =
    [
        ("–", "-"), ("‑", "-"), ("—", "-"), ("_", " "), ("“", "\""), ("”", "\""), ("‘", "'"), ("’", "'"),
        ("´", "'"), ("`", "'"), ("[", " "), ("]", " "), ("|", " "), ("/", " "), ("#", " "), ("→", " "), ("←", " "),
    ];

    static string WithoutEmojis(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var code = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? char.ConvertToUtf32(text[i], text[++i]) : text[i];
            var emoji = code is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or >= 0x1F1E6 and <= 0x1F1FF;
            if (!emoji) result.Append(char.ConvertFromUtf32(code));
        }
        return result.ToString();
    }

    /// <summary>Trozos de hasta 300 caracteres por frases, para empezar a hablar con la primera mientras se arma la siguiente.</summary>
    public static List<string> Chunks(string text, int max = 300)
    {
        var chunks = new List<string>();
        foreach (var paragraph in Regex.Split(text.Trim(), @"\n\s*\n+").Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var current = "";
            foreach (var sentence in Regex.Split(paragraph, @"(?<!\b(?:Sr|Sra|Dr|Dra|etc|pág|núm)\.)(?<!\b[A-Z]\.)(?<=[.!?])\s+"))
            {
                if (sentence.Length == 0) continue;
                // La primera frase va sola, para que la voz arranque cuanto antes.
                if (current.Length > 0 && current.Length + sentence.Length + 1 <= max && chunks.Count > 0) current += " " + sentence;
                else
                {
                    if (current.Length > 0) chunks.Add(current.Trim());
                    current = sentence;
                }
            }
            if (current.Length > 0) chunks.Add(current.Trim());
        }
        return chunks;
    }

    public void Dispose()
    {
        _duration.Dispose();
        _encoder.Dispose();
        _estimator.Dispose();
        _vocoder.Dispose();
    }
}
