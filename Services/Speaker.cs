using pxdSpeak.Platform;
using pxdSpeak.Speech;
using pxdSpeak.Views;

namespace pxdSpeak.Services;

/// <summary>The app's brain: owns the hotkeys and the reader, and turns "hotkey pressed" into "selection read aloud".</summary>
public sealed class Speaker : IDisposable
{
    private readonly HotkeyManager? _hotkeys;
    private Action<Action> _post = a => a();
    private int _grabbing;
    private BrowserBridge.Connection? _trackedOwner;
    private int _trackedId;
    private bool _inApp;

    /// <summary>Raised on the UI thread when a reading started by the in-app reader is over (finished, stopped or replaced).</summary>
    public event Action? InAppEnded;

    /// <summary>The link to the browser extension (interactive app only).</summary>
    public BrowserBridge? Bridge { get; }

    public AppSettings Settings { get; }
    public Reader Reader { get; } = new();

    /// <summary>Hotkeys another program already owns, as "Read selection: Ctrl + Alt + R".</summary>
    public List<string> HotkeyConflicts { get; } = new();

    /// <summary>Raised on the UI thread for the banner.</summary>
    public event Action<Notice>? Notice;

    /// <summary>Raised on the UI thread when reading starts, pauses or stops.</summary>
    public event Action<ReaderState>? StateChanged;

    public Speaker(bool interactive, AppSettings? settings = null)
    {
        Settings = settings ?? AppSettings.Load();
        Reader.Volume = (float)Settings.Volume;
        Chatterbox.IdleMinutes = Settings.ChatterboxIdleMinutes;
        if (interactive)
        {
            _hotkeys = new HotkeyManager();
            Bridge = new BrowserBridge();
        }
    }

    public void Start(Action<Action> post)
    {
        _post = post;
        Reader.StateChanged += s => _post(() =>
        {
            // Checked again here: a new reading may already have started since this one ended.
            if (s == ReaderState.Idle && Reader.State == ReaderState.Idle)
            {
                Banner.Hide();
                EndTracked();
                EndInApp();
            }
            StateChanged?.Invoke(s);
        });
        Reader.Failed += msg => _post(() => Notice?.Invoke(new Notice("COULDN'T READ", msg, Warning: true)));
        RegisterHotkeys();
        Reader.Preload(CurrentVoice);
        if (Bridge is not null)
        {
            Bridge.ReadRequested += r => _post(() => ReadTracked(r));
            Bridge.StopRequested += () => _post(Reader.Stop);
            Bridge.Start();
        }
    }

    /// <summary>
    /// Reads words the browser extension sent, telling it which word is being heard so it can highlight it on the page.
    /// </summary>
    public void ReadTracked(BrowserRead r)
    {
        var voice = CurrentVoice;
        if (!VoiceStore.IsInstalled(voice))
        {
            Notice?.Invoke(new Notice("VOICE NOT DOWNLOADED", $"Download {voice.Name} first, or pick another voice", Warning: true));
            return;
        }

        EndTracked();
        EndInApp();
        var id = ++_trackedId;
        var conn = r.From;
        _trackedOwner = conn;
        _ = conn.Send(new { type = "start", id });
        Reader.Volume = (float)Settings.Volume;
        Reader.SpeakTracked(new TrackedText
        {
            Words = r.Words,
            BlockEnds = Enumerable.Range(0, r.Words.Count).Select(r.BlockEnds.Contains).ToList(),
            Start = r.Start,
            OnWord = (i, first, last) => _ = conn.Send(new { type = "word", id, i, f = first, l = last }),
        }, voice, (float)Settings.Speed);
        ShowReadingBanner(voice);
    }

    /// <summary>
    /// Reads words from the app's own Read tab, reporting each word as it is heard. False (with a notice) when the voice
    /// isn't downloaded. No corner banner: the window shows what is being read.
    /// </summary>
    public bool ReadInApp(TrackedText tracked)
    {
        var voice = CurrentVoice;
        if (!VoiceStore.IsInstalled(voice))
        {
            Notice?.Invoke(new Notice("VOICE NOT DOWNLOADED", $"Download {voice.Name} first, or pick another voice", Warning: true));
            return false;
        }

        EndTracked();
        Banner.Hide();
        _inApp = true;
        Reader.Volume = (float)Settings.Volume;
        Reader.SpeakTracked(tracked, voice, (float)Settings.Speed);
        return true;
    }

    private void EndInApp()
    {
        if (!_inApp)
            return;
        _inApp = false;
        InAppEnded?.Invoke();
    }

    /// <summary>Tells the page that its reading is over (it takes the highlight away).</summary>
    private void EndTracked()
    {
        if (_trackedOwner is { } owner)
            _ = owner.Send(new { type = "end", id = _trackedId });
        _trackedOwner = null;
    }

    /// <summary>The chosen voice if it can be used; otherwise the first downloaded voice, then the first Windows voice.</summary>
    public VoiceInfo CurrentVoice
    {
        get
        {
            var chosen = VoiceCatalog.Find(Settings.Voice);
            if (chosen is not null && VoiceStore.IsInstalled(chosen))
                return chosen;
            return VoiceCatalog.KokoroVoices.Concat(VoiceCatalog.Piper).FirstOrDefault(VoiceStore.IsInstalled)
                   ?? WindowsVoices.All().FirstOrDefault()
                   ?? chosen
                   ?? VoiceCatalog.KokoroVoices[3];
        }
    }

    public void SetVoice(VoiceInfo v)
    {
        Settings.Voice = v.Id;
        Settings.Save();
        // Moving away from Chatterbox gives the graphics card's memory straight back (for games).
        if (v.Family != VoiceFamily.Chatterbox)
            Chatterbox.Stop();
        Reader.Preload(v);
    }

    public void RegisterHotkeys()
    {
        if (_hotkeys is null)
            return;
        _hotkeys.UnregisterAll();
        HotkeyConflicts.Clear();
        void Add(string name, Hotkey? key, Action action)
        {
            if (key is not null && !_hotkeys.Register(key, action))
                HotkeyConflicts.Add($"{name}: {key}");
        }

        Add("Read selection", Settings.ReadHotkey, OnReadHotkey);
        Add("Pause / resume", Settings.PauseHotkey, Reader.TogglePause);
        Add("Stop", Settings.StopHotkey, Reader.Stop);
    }

    public void PauseHotkeys() => _hotkeys?.UnregisterAll();

    private void OnReadHotkey()
    {
        if (Reader.IsBusy && Settings.ReadAgainStops)
        {
            Reader.Stop();
            return;
        }

        ReadSelection();
    }

    /// <summary>Copies the selection in the program in front and reads it (the clipboard is put back afterwards).</summary>
    public void ReadSelection()
    {
        if (Interlocked.Exchange(ref _grabbing, 1) == 1)
            return;
        var thread = new Thread(() =>
        {
            try
            {
                // A browser with the extension: take the selection from the page itself, so its words can be highlighted.
                if (Settings.BrowserHighlight && Bridge is { Count: > 0 } bridge && BrowserBridge.BrowserInFront()
                    && bridge.Grab(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult() is { } fromPage)
                {
                    _post(() => ReadTracked(fromPage));
                    return;
                }

                var (outcome, text) = Selection.Grab();
                if (outcome != Selection.Outcome.Text && Settings.ClipboardFallback)
                {
                    text = Selection.ReadText() ?? "";
                    if (!string.IsNullOrWhiteSpace(text))
                        outcome = Selection.Outcome.Text;
                }

                _post(() =>
                {
                    if (outcome == Selection.Outcome.Text)
                        Read(text);
                    else
                        Notice?.Invoke(new Notice("NOTHING TO READ", outcome == Selection.Outcome.ClipboardBusy
                            ? "Another program is holding the clipboard. Try again."
                            : "Select some text first, then press " + (Settings.ReadHotkey?.ToString() ?? "the hotkey"), Warning: true));
                });
            }
            finally
            {
                Interlocked.Exchange(ref _grabbing, 0);
            }
        }) { IsBackground = true, Name = "pxdSpeak selection" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Reads <paramref name="text"/> with the current voice (or <paramref name="voice"/>).</summary>
    public void Read(string text, VoiceInfo? voice = null)
    {
        voice ??= CurrentVoice;
        if (!VoiceStore.IsInstalled(voice))
        {
            Notice?.Invoke(new Notice("VOICE NOT DOWNLOADED", $"Download {voice.Name} first, or pick another voice", Warning: true));
            return;
        }

        EndTracked();
        EndInApp();
        Reader.Volume = (float)Settings.Volume;
        Reader.Speak(text, voice, (float)Settings.Speed);
        ShowReadingBanner(voice);
    }

    private void ShowReadingBanner(VoiceInfo voice)
    {
        if (!Settings.ShowBanner)
            return;
        var stop = Settings.ReadAgainStops && Settings.ReadHotkey is { } k ? $"{k} stops" : Settings.StopHotkey is { } s ? $"{s} stops" : "";
        Notice?.Invoke(new Notice("READING · " + voice.Name.ToUpperInvariant(), stop.Length > 0 ? stop : "Click to hide", Sticky: true));
    }

    public void Dispose()
    {
        _hotkeys?.Dispose();
        EndTracked();
        Bridge?.Dispose();
        Reader.Dispose();
    }
}
