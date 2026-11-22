using System.Runtime.InteropServices;
using NAudio.Wave;
using SherpaOnnx;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace pxdSpeak.Speech;

/// <summary>Turns text into audio, handing it over a piece at a time (a sentence or so) so reading can start at once.</summary>
public interface ISynth : IDisposable
{
    /// <summary>
    /// Synthesizes <paramref name="text"/>. <paramref name="chunk"/> gets mono float samples and their sample rate;
    /// generation stops early when it returns false or <paramref name="ct"/> is cancelled.
    /// </summary>
    void Speak(string text, float speed, Func<float[], int, bool> chunk, CancellationToken ct);
}

/// <summary>A voice that can say when each word starts (Windows voices).</summary>
public interface IWordTimed
{
    /// <summary>Audio for <paramref name="text"/> plus (character position in the text, start in seconds) for each word.</summary>
    (float[] Samples, int Rate, List<(int Char, double Seconds)> Words) SpeakTimed(string text, float speed, CancellationToken ct);
}

/// <summary>Kokoro and Piper voices through sherpa-onnx (CPU, offline).</summary>
public sealed class SherpaSynth : ISynth
{
    private readonly OfflineTts _tts;
    private readonly int _speaker;

    public string Key { get; }

    private SherpaSynth(OfflineTts tts, int speaker, string key)
    {
        _tts = tts;
        _speaker = speaker;
        Key = key;
    }

    /// <summary>The model a voice needs: one per pack (and accent for Kokoro, whose lexicon differs).</summary>
    public static string KeyOf(VoiceInfo v) => v.Family == VoiceFamily.Kokoro ? $"{v.Pack}|{v.Accent}" : v.Pack;

    private static int Threads => int.TryParse(Environment.GetEnvironmentVariable("PXDSPEAK_THREADS"), out var t) ? t : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    public static OfflineTts Load(VoiceInfo v)
    {
        var dir = VoiceStore.PackDir(v.Pack);
        var config = new OfflineTtsConfig { MaxNumSentences = 1 };
        config.Model.NumThreads = Threads;
        config.Model.Provider = "cpu";
        if (v.Family == VoiceFamily.Kokoro)
        {
            config.Model.Kokoro.Model = File.Exists(Path.Combine(dir, "model.onnx")) ? Path.Combine(dir, "model.onnx") : Path.Combine(dir, "model.int8.onnx");
            config.Model.Kokoro.Voices = Path.Combine(dir, "voices.bin");
            config.Model.Kokoro.Tokens = Path.Combine(dir, "tokens.txt");
            config.Model.Kokoro.DataDir = VoiceStore.EspeakData;
            config.Model.Kokoro.Lexicon = Path.Combine(dir, v.Accent == "UK" ? "lexicon-gb-en.txt" : "lexicon-us-en.txt");
            config.Model.Kokoro.Lang = v.Accent == "UK" ? "en" : "en-us";   // eSpeak NG: "en" is British English
            config.Model.Kokoro.LengthScale = 1.0f;
        }
        else
        {
            var onnx = Directory.EnumerateFiles(dir, "*.onnx").First();
            config.Model.Vits.Model = onnx;
            config.Model.Vits.Tokens = Path.Combine(dir, "tokens.txt");
            config.Model.Vits.DataDir = VoiceStore.EspeakData;
            config.Model.Vits.NoiseScale = 0.667f;
            config.Model.Vits.NoiseScaleW = 0.8f;
            config.Model.Vits.LengthScale = 1.0f;
        }

        return new OfflineTts(config);
    }

    /// <summary>A speaker of an already loaded model; the model belongs to the caller (shared by every speaker of a pack).</summary>
    public SherpaSynth(OfflineTts tts, VoiceInfo v) : this(tts, v.Speaker, KeyOf(v)) { }

    public void Speak(string text, float speed, Func<float[], int, bool> chunk, CancellationToken ct)
    {
        lock (_tts)   // one generation at a time per model; speakers of a pack share it
        {
            var rate = _tts.SampleRate;
            OfflineTtsCallback callback = (samples, n) =>
            {
                if (ct.IsCancellationRequested)
                    return 0;
                var copy = new float[n];
                Marshal.Copy(samples, copy, 0, n);
                return chunk(copy, rate) && !ct.IsCancellationRequested ? 1 : 0;
            };
            var audio = _tts.GenerateWithCallback(text, speed, _speaker, callback);
            audio.Dispose();
            GC.KeepAlive(callback);
        }
    }

    public void Dispose() { /* the model is owned by Reader */ }
}

/// <summary>The voices built into Windows (Settings › Time and language › Speech), through Windows.Media.SpeechSynthesis.</summary>
public sealed class WindowsSynth : ISynth, IWordTimed
{
    private readonly SpeechSynthesizer _synth = new();

    public WindowsSynth(VoiceInfo v)
    {
        var id = v.Id["windows:".Length..];
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(x => x.Id == id) ?? SpeechSynthesizer.DefaultVoice;
        _synth.Voice = voice;
    }

    public void Speak(string text, float speed, Func<float[], int, bool> chunk, CancellationToken ct)
    {
        _synth.Options.SpeakingRate = Math.Clamp(speed, 0.5, 6.0);
        foreach (var piece in TextPrep.Pieces(text, 400))
        {
            if (ct.IsCancellationRequested)
                return;
            using var stream = _synth.SynthesizeTextToStreamAsync(piece).AsTask(ct).GetAwaiter().GetResult();
            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                reader.LoadAsync((uint)stream.Size).AsTask(ct).GetAwaiter().GetResult();
                reader.ReadBytes(bytes);
            }

            var (samples, rate) = Wav.ToMonoFloat(bytes);
            if (!chunk(samples, rate))
                return;
        }
    }

    public (float[] Samples, int Rate, List<(int Char, double Seconds)> Words) SpeakTimed(string text, float speed, CancellationToken ct)
    {
        _synth.Options.SpeakingRate = Math.Clamp(speed, 0.5, 6.0);
        _synth.Options.IncludeWordBoundaryMetadata = true;
        using var stream = _synth.SynthesizeTextToStreamAsync(text).AsTask(ct).GetAwaiter().GetResult();
        var words = new List<(int, double)>();
        foreach (var track in stream.TimedMetadataTracks)
            foreach (var cue in track.Cues)
                if (cue is Windows.Media.Core.SpeechCue sc && track.Id == "SpeechWord")
                    words.Add((sc.StartPositionInInput ?? 0, sc.StartTime.TotalSeconds));
        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            reader.LoadAsync((uint)stream.Size).AsTask(ct).GetAwaiter().GetResult();
            reader.ReadBytes(bytes);
        }

        var (samples, rate) = Wav.ToMonoFloat(bytes);
        return (samples, rate, words);
    }

    public void Dispose() => _synth.Dispose();
}

public static class WindowsVoices
{
    private static List<VoiceInfo>? _all;

    /// <summary>Installed Windows voices (English first).</summary>
    public static IReadOnlyList<VoiceInfo> All()
    {
        if (_all is not null)
            return _all;
        try
        {
            _all = SpeechSynthesizer.AllVoices
                .OrderBy(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(v => v.DisplayName)
                .Select(v => new VoiceInfo(
                    "windows:" + v.Id, VoiceFamily.Windows,
                    v.DisplayName.Replace("Microsoft ", "").Replace(" Desktop", "").Replace(" Online (Natural)", "").Trim(),
                    Accent(v.Language), v.Gender == VoiceGender.Female ? "Female" : "Male", ""))
                .ToList();
        }
        catch
        {
            _all = [];
        }

        return _all;
    }

    private static string Accent(string lang) => lang.ToLowerInvariant() switch
    {
        "en-us" => "US",
        "en-gb" => "UK",
        "en-au" => "Australia",
        "en-ca" => "Canada",
        "en-in" => "India",
        "en-ie" => "Ireland",
        _ => lang,
    };
}

internal static class Wav
{
    public static (float[] Samples, int Rate) ToMonoFloat(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var provider = reader.ToSampleProvider();
        var channels = provider.WaveFormat.Channels;
        var list = new List<float>((int)(reader.SampleCount * channels));
        var buffer = new float[8192 * channels];
        int n;
        while ((n = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < n; i += channels)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                    sum += buffer[i + c];
                list.Add(sum / channels);
            }
        }

        return (list.ToArray(), provider.WaveFormat.SampleRate);
    }
}
