using System.Text.Json;
using pxdSpeak.Platform;

namespace pxdSpeak.Services;

/// <summary>Everything the user can change, saved as JSON in %APPDATA%\pxdSpeak.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Reads the selected text aloud (pressed again while reading: stops).</summary>
    public Hotkey? ReadHotkey { get; set; } = new(Hotkey.Ctrl | Hotkey.Alt, 0x52);   // R

    public Hotkey? PauseHotkey { get; set; } = new(Hotkey.Ctrl | Hotkey.Alt, 0x50);  // P

    public Hotkey? StopHotkey { get; set; }

    /// <summary>Pressing the read hotkey while it is reading stops it, instead of starting over with the new selection.</summary>
    public bool ReadAgainStops { get; set; } = true;

    /// <summary>Nothing selected: read what's on the clipboard instead.</summary>
    public bool ClipboardFallback { get; set; } = true;

    public string Voice { get; set; } = Speech.VoiceCatalog.DefaultVoice;

    /// <summary>1 = normal; 0.5 … 2.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>0 … 1.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>A small corner banner while reading (which voice, how to stop) and for "nothing selected".</summary>
    public bool ShowBanner { get; set; } = true;

    /// <summary>Minutes unused before Chatterbox lets go of the graphics card; 0 = keep it loaded.</summary>
    public int ChatterboxIdleMinutes { get; set; } = 30;

    /// <summary>With the pxd Speak browser extension: read the page selection there and highlight each word as it's read.</summary>
    public bool BrowserHighlight { get; set; } = true;

    public bool StartInTray { get; set; }

    /// <summary>The text in the "Try it" box.</summary>
    public string TryText { get; set; } = "Hi! Select any text, press the hotkey, and I'll read it to you in this voice.";

    /// <summary>%APPDATA%\pxdSpeak, or PXDSPEAK_DATA_DIR (tests).</summary>
    public static string Folder =>
        Environment.GetEnvironmentVariable("PXDSPEAK_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pxdSpeak");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            /* a damaged file just means defaults */
        }

        return new AppSettings();
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool ReadOnly { get; set; }

    public void Save()
    {
        if (ReadOnly)
            return;
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            /* best effort */
        }
    }
}
