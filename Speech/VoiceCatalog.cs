namespace pxdSpeak.Speech;

public enum VoiceFamily { Kokoro, Piper, Windows, Chatterbox }

/// <summary>
/// One voice the user can pick. Kokoro and Piper voices live in downloadable packs (Kokoro: one pack, many speakers;
/// Piper: one pack per voice); Windows voices are already installed.
/// </summary>
public sealed record VoiceInfo(
    string Id,             // stable id saved in settings, e.g. "kokoro:af_heart", "piper:en_US-ryan-high", "windows:<voice id>"
    VoiceFamily Family,
    string Name,           // "Heart"
    string Accent,         // "US", "UK"
    string Gender,         // "Female", "Male", ""
    string Pack,           // download pack (folder) name; "" for Windows voices
    int Speaker = 0,       // speaker number inside a multi-speaker pack
    bool Star = false,     // one of the best-sounding voices
    string Note = "")
{
    public string Tags => string.Join(" · ", new[] { Accent, Gender, Note }.Where(s => s.Length > 0));
}

/// <summary>A downloadable voice pack from the sherpa-onnx "tts-models" release on GitHub.</summary>
public sealed record VoicePack(string Name, VoiceFamily Family, double SizeMb)
{
    public string Url => $"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{Name}.tar.bz2";
}

/// <summary>
/// The free voices on offer. Sources (all free, all run offline once downloaded):
/// <list type="bullet">
/// <item>Kokoro-82M v1.0 (hexgrad, Apache-2.0): 54 natural voices in one model; the English ones are listed.</item>
/// <item>Piper (rhasspy / OHF-Voice): single-voice models, small and fast. Each voice has its own model card and licence.</item>
/// </list>
/// Both come repackaged for sherpa-onnx (k2-fsa, Apache-2.0), which runs them on the CPU.
/// </summary>
public static class VoiceCatalog
{
    // The full-precision model: on desktop CPUs it runs about 5x faster than the int8 one (13 s of speech in 1.6 s vs 8 s).
    public const string KokoroPack = "kokoro-multi-lang-v1_0";

    public static readonly VoicePack Kokoro = new(KokoroPack, VoiceFamily.Kokoro, 334);

    /// <summary>Kokoro v1.0 speaker numbers are the official voice names in alphabetical order (em_santa added last, 53).</summary>
    private static readonly string[] KokoroSpeakers =
    [
        "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore", "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
        "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx", "am_puck", "am_santa",
        "bf_alice", "bf_emma", "bf_isabella", "bf_lily", "bm_daniel", "bm_fable", "bm_george", "bm_lewis",
    ];

    /// <summary>The Kokoro voices that score best in the model's own voice grades.</summary>
    private static readonly HashSet<string> KokoroStars = ["af_heart", "af_bella", "af_nicole", "am_michael", "am_fenrir", "am_puck", "bf_emma", "bm_george", "bm_fable"];

    private static readonly Dictionary<string, string> KokoroNotes = new()
    {
        ["af_nicole"] = "whispery",
        ["am_santa"] = "jolly",
        ["af_sky"] = "light",
        ["am_onyx"] = "deep",
    };

    /// <summary>Hand-picked English Piper voices (single speaker). Size in MB of each download.</summary>
    private static readonly (string Pack, string Name, string Accent, string Gender, bool Star, string Note, double Mb)[] PiperVoices =
    [
        ("en_US-lessac-high", "Lessac", "US", "Female", true, "", 110),
        ("en_US-ryan-high", "Ryan", "US", "Male", true, "", 110),
        ("en_US-amy-medium", "Amy", "US", "Female", false, "", 64),
        ("en_US-ljspeech-high", "LJ", "US", "Female", false, "audiobook", 110),
        ("en_US-kristin-medium", "Kristin", "US", "Female", false, "", 64),
        ("en_US-hfc_female-medium", "HFC Female", "US", "Female", false, "", 64),
        ("en_US-joe-medium", "Joe", "US", "Male", false, "", 64),
        ("en_US-john-medium", "John", "US", "Male", false, "", 64),
        ("en_US-norman-medium", "Norman", "US", "Male", false, "", 64),
        ("en_US-bryce-medium", "Bryce", "US", "Male", false, "", 64),
        ("en_US-hfc_male-medium", "HFC Male", "US", "Male", false, "", 64),
        ("en_US-kusal-medium", "Kusal", "US", "Male", false, "", 64),
        ("en_US-reza_ibrahim-medium", "Reza", "US", "Male", false, "", 64),
        ("en_US-glados-high", "GLaDOS", "US", "Female", false, "robot", 110),
        ("en_GB-cori-high", "Cori", "UK", "Female", true, "", 110),
        ("en_GB-jenny_dioco-medium", "Jenny", "UK", "Female", false, "", 64),
        ("en_GB-alba-medium", "Alba", "UK", "Female", false, "Scottish", 64),
        ("en_GB-southern_english_female-low", "Southern", "UK", "Female", false, "", 64),
        ("en_GB-alan-medium", "Alan", "UK", "Male", false, "", 64),
        ("en_GB-northern_english_male-medium", "Northern", "UK", "Male", false, "", 64),
    ];

    public static IReadOnlyList<VoiceInfo> KokoroVoices { get; } = KokoroSpeakers.Select((v, i) => new VoiceInfo(
        "kokoro:" + v, VoiceFamily.Kokoro,
        char.ToUpperInvariant(v[3]) + v[4..],
        v[0] == 'b' ? "UK" : "US",
        v[1] == 'f' ? "Female" : "Male",
        KokoroPack, i, KokoroStars.Contains(v), KokoroNotes.GetValueOrDefault(v, ""))).ToList();

    public static IReadOnlyList<VoiceInfo> Piper { get; } = PiperVoices.Select(p => new VoiceInfo(
        "piper:" + p.Pack, VoiceFamily.Piper, p.Name, p.Accent, p.Gender, "vits-piper-" + p.Pack, 0, p.Star, p.Note)).ToList();

    public static IReadOnlyList<VoicePack> PiperPacks { get; } = PiperVoices.Select(p => new VoicePack("vits-piper-" + p.Pack, VoiceFamily.Piper, p.Mb)).ToList();

    public static VoicePack? PackOf(VoiceInfo v) =>
        v.Family switch
        {
            VoiceFamily.Kokoro => Kokoro,
            VoiceFamily.Piper => PiperPacks.FirstOrDefault(p => p.Name == v.Pack),
            _ => null,
        };

    /// <summary>The voice used until the user picks one: the best Kokoro voice if it's downloaded, else a Windows voice.</summary>
    public const string DefaultVoice = "kokoro:af_heart";

    public static VoiceInfo? Find(string? id) =>
        id is null ? null
        : KokoroVoices.FirstOrDefault(v => v.Id == id) ?? Piper.FirstOrDefault(v => v.Id == id)
          ?? ChatterboxVoices().FirstOrDefault(v => v.Id == id) ?? WindowsVoices.All().FirstOrDefault(v => v.Id == id);

    // ---- Chatterbox ---------------------------------------------------------------------------------------------------

    public const string ChatterboxDefault = "chatterbox:default";
    public const string ChatterboxClipPrefix = "chatterbox:clip:";
    public const string ChatterboxKokoroPrefix = "chatterbox:kokoro:";

    /// <summary>Audio files a voice can be copied from (Chatterbox reads them with librosa).</summary>
    public static readonly string[] ClipExtensions = [".wav", ".mp3", ".flac", ".ogg"];

    /// <summary>
    /// Chatterbox voices: its own built-in voice; the best Kokoro voices, re-voiced by Chatterbox from a short Kokoro
    /// recording (needs Kokoro downloaded); and any voice the user added from an audio clip.
    /// </summary>
    public static IReadOnlyList<VoiceInfo> ChatterboxVoices()
    {
        var list = new List<VoiceInfo> { new(ChatterboxDefault, VoiceFamily.Chatterbox, "Default", "", "", "", 0, true, "built-in voice") };
        foreach (var k in KokoroVoices.Where(v => v.Star))
            list.Add(new VoiceInfo(ChatterboxKokoroPrefix + k.Id[7..], VoiceFamily.Chatterbox, k.Name, k.Accent, k.Gender, KokoroPack, 0, false, "from Kokoro"));
        try
        {
            if (Directory.Exists(Speech.Chatterbox.ClipsFolder))
                foreach (var f in Directory.EnumerateFiles(Speech.Chatterbox.ClipsFolder).Where(f => ClipExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f))
                    list.Add(new VoiceInfo(ChatterboxClipPrefix + Path.GetFileName(f), VoiceFamily.Chatterbox, Path.GetFileNameWithoutExtension(f), "", "", "", 0, false, "your clip"));
        }
        catch
        {
            /* folder being changed */
        }

        return list;
    }
}
