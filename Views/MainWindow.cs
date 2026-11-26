using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using pxdSpeak.Extensions;
using pxdSpeak.Platform;
using pxdSpeak.Services;
using pxdSpeak.Speech;
using pxdStandard;

namespace pxdSpeak.Views;

/// <summary>The one window: the Read tab (paste text and hear it with each word lit up), the voices, and the settings.</summary>
public sealed class MainWindow : Window
{
    public const string PlacementKey = "main";

    private readonly Speaker _sp;
    private readonly TrayLink? _tray;
    private readonly Action? _quit;
    private readonly List<(VoiceInfo Voice, Action Refresh)> _rows = new();
    private readonly Dictionary<string, List<Action>> _packRefresh = new();
    private string _filter = "All";
    private StackPanel _lists = null!;
    private TextBlock _nowName = null!, _nowTags = null!, _volumeLabel = null!, _storage = null!, _hint = null!;
    private StackPanel _settingsRoot = null!;
    private Border _conflicts = null!;
    private TextBlock _conflictText = null!;

    public TabControl Tabs { get; private set; } = null!;

    /// <summary>The Read tab.</summary>
    public ReaderView Reader { get; }

    private AppSettings S => _sp.Settings;

    public MainWindow(Speaker sp, TrayLink? tray = null, Action? quit = null, ProHost? pro = null)
    {
        _sp = sp;
        _tray = tray;
        _quit = quit;
        Title = "pxd Speak";
        Width = 640;
        Height = 800;
        MinWidth = 520;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = BrandIcon.Window(128);

        Tabs = new TabControl { Padding = new Thickness(0, 10, 0, 0) };
        Reader = new ReaderView(sp, () => Tabs!.SelectedIndex = 1, () => Tabs!.SelectedIndex = 2);
        Reader.VoiceChanged += RefreshAll;
        Tabs.Items.Add(new TabItem { Header = "Read", Content = new Border { Padding = new Thickness(0, 0, 12, 6), Child = Reader } });
        Tabs.Items.Add(new TabItem { Header = "Voices", Content = Scroll(BuildVoicesTab()) });
        Tabs.Items.Add(new TabItem { Header = "Settings", Content = Scroll(BuildSettingsTab()) });
        var body = new Grid { Margin = new Thickness(22, 10, 22, 16), Children = { Tabs } };
        Content = pxdChrome.Wrap(this, body, "pxd Speak", BrandIcon.Render(36), resizable: true);
        pxdChrome.RememberPlacement(this, PlacementKey);

        Downloads.Changed += OnDownloadChanged;
        Downloads.Finished += OnDownloadFinished;
        Chatterbox.Changed += OnChatterboxChanged;
        Closed += (_, _) =>
        {
            Chatterbox.Changed -= OnChatterboxChanged;
            Downloads.Changed -= OnDownloadChanged;
            Downloads.Finished -= OnDownloadFinished;
        };
        RefreshAll();
        ShowConflicts();
        pro?.Opened(this);
    }

    private static ScrollViewer Scroll(Control c) => new() { Content = new Border { Padding = new Thickness(0, 0, 12, 6), Child = c } };

    // ---- Voices tab --------------------------------------------------------------------------------------------------

    private Control BuildVoicesTab()
    {
        var root = new StackPanel { Spacing = 12 };

        _conflictText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#F5C97A"), FontSize = 12 };
        _conflicts = new Border { Classes = { "card" }, Background = Brush("#2A2210"), BorderBrush = Brush("#7A5A1A"), Padding = new Thickness(14), IsVisible = false, Child = _conflictText };
        root.Children.Add(_conflicts);

        // Now reading with (reading itself is on the Read tab)
        _nowName = new TextBlock { Classes = { "neon" }, FontSize = 26, FontWeight = FontWeight.Bold };
        _nowTags = new TextBlock { Classes = { "muted" }, FontSize = 12 };
        _hint = new TextBlock { Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        root.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "NOW READING WITH", Classes = { "label" } },
                new StackPanel { Spacing = 2, Children = { _nowName, _nowTags } },
                _hint,
            },
        }));

        // Filters
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var f in new[] { "All", "Female", "Male", "US", "UK", "Downloaded" })
        {
            var chip = new ToggleButton { Classes = { "chip" }, Content = f, IsChecked = f == _filter, Margin = new Thickness(0, 0, 8, 8) };
            chip.Click += (_, _) =>
            {
                _filter = f;
                foreach (var c in chips.Children.OfType<ToggleButton>())
                    c.IsChecked = (string?)c.Content == f;
                RefreshAll();
            };
            chips.Children.Add(chip);
        }

        root.Children.Add(chips);

        _lists = new StackPanel { Spacing = 12 };
        root.Children.Add(_lists);
        BuildLists();
        return root;
    }

    private void BuildLists()
    {
        _rows.Clear();
        _packRefresh.Clear();
        _lists.Children.Clear();

        // The Store build leaves Chatterbox out: its set-up downloads Python and PyTorch, which Store rules don't allow.
        if (!Packaged.IsPackaged)
            _lists.Children.Add(BuildChatterboxCard());

        // Kokoro: one pack for all its voices.
        var kokoroHead = SectionHead("KOKORO", "Natural, expressive voices · one 334 MB download for all 28 · Apache-2.0", VoiceCatalog.Kokoro);
        var kokoroList = new StackPanel { Spacing = 4 };
        foreach (var v in VoiceCatalog.KokoroVoices)
            kokoroList.Children.Add(VoiceRow(v, ownDownload: false));
        _lists.Children.Add(Card(new StackPanel { Spacing = 10, Children = { kokoroHead, kokoroList } }));

        var piperList = new StackPanel { Spacing = 4 };
        foreach (var v in VoiceCatalog.Piper)
            piperList.Children.Add(VoiceRow(v, ownDownload: true));
        _lists.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children = { SectionHead("PIPER", "Light and fast · each voice is its own 64–110 MB download · licences per voice", null), piperList },
        }));

        var winList = new StackPanel { Spacing = 4 };
        foreach (var v in WindowsVoices.All())
            winList.Children.Add(VoiceRow(v, ownDownload: false));
        if (WindowsVoices.All().Count == 0)
            winList.Children.Add(new TextBlock { Text = "No Windows voices found.", Classes = { "muted" } });
        var addMore = new Button { Classes = { "ghost" }, Content = "Add more Windows voices…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4) };
        addMore.Click += (_, _) => Open("ms-settings:speech");
        _lists.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children = { SectionHead("WINDOWS", "Built into Windows · no download · more robotic", null), winList, addMore },
        }));
    }

    /// <summary>Chatterbox: set-up button and status, its voices, and "add a voice from a clip".</summary>
    private Control BuildChatterboxCard()
    {
        var installed = Chatterbox.IsInstalled;
        var state = Chatterbox.State;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new StackPanel
        {
            Spacing = 1, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "CHATTERBOX  ·  MOST HUMAN", Classes = { "label" } },
                new TextBlock
                {
                    Text = "Runs on your graphics card · one-time set-up, about 9 GB · MIT licence · the speed setting doesn't apply",
                    Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                },
            },
        });
        var action = new Button { MinWidth = 140, Padding = new Thickness(12, 5), MinHeight = 30, FontSize = 12 };
        if (state == ServerState.Installing)
        {
            action.Content = "Cancel set-up";
            action.Click += (_, _) => Chatterbox.CancelInstall();
        }
        else if (!installed)
        {
            action.Classes.Add("primary");
            action.Content = "Set up · about 9 GB";
            action.Click += (_, _) => Chatterbox.StartInstall();
        }
        else
        {
            action.Classes.Add("ghost");
            action.Content = "Remove";
            ToolTip.SetTip(action, "Deletes Chatterbox's Python, model and your voice clips");
            action.Click += (_, _) =>
            {
                if (_sp.CurrentVoice.Family == VoiceFamily.Chatterbox)
                    _sp.Reader.Stop();
                try { Chatterbox.Uninstall(); } catch { /* files in use; try again */ }
                RebuildLists();
            };
        }

        Place(head, action, 1);
        var body = new StackPanel { Spacing = 10, Children = { head } };

        var status = Chatterbox.Status;
        if (status.Length > 0 || state is ServerState.Installing or ServerState.Starting)
        {
            var busy = state is ServerState.Installing or ServerState.Starting;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (busy)
                line.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 80, Height = 6, MinHeight = 6, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#19F58C") });
            line.Children.Add(new TextBlock
            {
                Text = status, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 460,
                Foreground = Brush(state == ServerState.Failed || status.Contains("failed", StringComparison.OrdinalIgnoreCase) ? "#F2666A" : "#8A92A6"),
            });
            body.Children.Add(line);
        }

        if (!installed)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Sounds like a real person: natural pauses, emphasis and breathing. pxd Speak sets up its own private Python, " +
                       "PyTorch and the Chatterbox Turbo model in its folder (nothing system-wide), then runs it only on this PC.",
                Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
            return Card(body);
        }

        var list = new StackPanel { Spacing = 4 };
        foreach (var v in VoiceCatalog.ChatterboxVoices())
            list.Children.Add(VoiceRow(v, ownDownload: false, removable: v.Id.StartsWith(VoiceCatalog.ChatterboxClipPrefix, StringComparison.Ordinal)));
        body.Children.Add(list);

        var add = new Button { Content = "+  Add a voice from an audio clip…", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += async (_, _) => await AddClip();
        body.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                add,
                new TextBlock
                {
                    Text = "10–20 seconds of one person talking clearly (WAV, MP3, FLAC or OGG), with no music behind. Only copy voices you have the right to use. " +
                           "Tip: Chatterbox acts on tags like [laugh], [sigh] or [chuckle] in the text.",
                    Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                },
            },
        });
        return Card(body);
    }

    private async Task AddClip()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Pick a clip of the voice (10–20 seconds)",
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Audio") { Patterns = VoiceCatalog.ClipExtensions.Select(e => "*" + e).ToList() }],
        });
        var path = files.FirstOrDefault()?.Path is { IsFile: true } uri ? uri.LocalPath : null;
        if (path is null)
            return;
        Directory.CreateDirectory(Chatterbox.ClipsFolder);
        var name = string.Concat(Path.GetFileNameWithoutExtension(path).Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (name.Length == 0)
            name = "My voice";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var dest = Path.Combine(Chatterbox.ClipsFolder, name + ext);
        for (var i = 2; File.Exists(dest); i++)
            dest = Path.Combine(Chatterbox.ClipsFolder, $"{name} {i}{ext}");
        File.Copy(path, dest);
        if (VoiceCatalog.Find(VoiceCatalog.ChatterboxClipPrefix + Path.GetFileName(dest)) is { } voice)
            _sp.SetVoice(voice);
        RebuildLists();
    }

    private void RebuildLists()
    {
        BuildLists();
        RefreshAll();
    }

    private Control SectionHead(string title, string sub, VoicePack? pack)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(new StackPanel
        {
            Spacing = 1, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = title, Classes = { "label" } }, new TextBlock { Text = sub, Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap } },
        });
        if (pack is not null)
            Place(g, PackControl(pack, compact: false), 1);
        return g;
    }

    /// <summary>Download / progress + cancel / delete for a pack; refreshed as the download moves along.</summary>
    private Control PackControl(VoicePack pack, bool compact)
    {
        var button = new Button { MinWidth = compact ? 96 : 140, Padding = new Thickness(12, 5), MinHeight = 30, FontSize = 12 };
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = compact ? 96 : 140, Height = 6, MinHeight = 6, IsVisible = false, Foreground = Brush("#19F58C") };
        var panel = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { button, bar } };

        void Refresh()
        {
            var running = Downloads.IsRunning(pack.Name);
            var installed = VoiceStore.IsInstalled(pack.Name);
            bar.IsVisible = running;
            bar.Value = Downloads.Progress(pack.Name);
            button.Classes.Set("primary", !running && !installed);
            button.Classes.Set("ghost", installed && !running);
            button.Content = running
                ? (Downloads.Progress(pack.Name) >= 1 ? "Unpacking…" : $"Cancel  {Downloads.Progress(pack.Name):P0}")
                : installed ? "Delete" : $"Download · {pack.SizeMb:0} MB";
        }

        button.Click += (_, _) =>
        {
            if (Downloads.IsRunning(pack.Name))
                Downloads.Cancel(pack.Name);
            else if (VoiceStore.IsInstalled(pack.Name))
            {
                _sp.Reader.Unload();
                try { VoiceStore.Delete(pack.Name); } catch { /* in use; try again later */ }
                RefreshAll();
            }
            else
                Downloads.Start(pack);
            Refresh();
        };
        if (!_packRefresh.TryGetValue(pack.Name, out var list))
            _packRefresh[pack.Name] = list = new();
        list.Add(Refresh);
        Refresh();
        return panel;
    }

    private Control VoiceRow(VoiceInfo v, bool ownDownload, bool removable = false)
    {
        var dot = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var name = new TextBlock { Text = v.Name + (v.Star ? "  ★" : ""), FontWeight = FontWeight.SemiBold, FontSize = 14 };
        var tags = new TextBlock { Text = v.Tags, Classes = { "muted" }, FontSize = 11 };
        var play = new Button { Classes = { "ghost" }, Content = "▶", Width = 40, MinHeight = 30, Padding = new Thickness(0), FontSize = 13 };
        ToolTip.SetTip(play, "Hear this voice");
        play.Click += (_, _) => _sp.Read(v.Family == VoiceFamily.Chatterbox
            ? "Hey there! So this is what I sound like. Pretty natural, right? Select some text, press the hotkey, and I'll read it to you."
            : $"Hi, I'm {v.Name}. This is how I sound when I read to you.", v);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(dot);
        Place(grid, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Children = { name, tags } }, 1);
        Place(grid, play, 2);
        if (ownDownload && VoiceCatalog.PackOf(v) is { } pack)
        {
            var pc = PackControl(pack, compact: true);
            pc.Margin = new Thickness(8, 0, 0, 0);
            Place(grid, pc, 3);
        }

        if (removable)
        {
            var remove = new Button { Classes = { "ghost" }, Content = "✕", Width = 36, MinHeight = 30, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) };
            ToolTip.SetTip(remove, "Remove this voice");
            remove.Click += (_, _) =>
            {
                try { File.Delete(Path.Combine(Chatterbox.ClipsFolder, v.Id[VoiceCatalog.ChatterboxClipPrefix.Length..])); } catch { /* in use */ }
                RebuildLists();
            };
            Place(grid, remove, 3);
        }

        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6), BorderThickness = new Thickness(1), Child = grid, Cursor = new Cursor(StandardCursorType.Hand) };
        row.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual s && (s.FindAncestorOfType<Button>(includeSelf: true) is not null))
                return;
            if (!VoiceStore.IsInstalled(v))
                return;
            _sp.SetVoice(v);
            RefreshAll();
        };

        void Refresh()
        {
            var installed = VoiceStore.IsInstalled(v);
            var selected = _sp.CurrentVoice.Id == v.Id;
            row.IsVisible = _filter switch
            {
                "Female" => v.Gender == "Female",
                "Male" => v.Gender == "Male",
                "US" => v.Accent == "US",
                "UK" => v.Accent == "UK",
                "Downloaded" => installed,
                _ => true,
            };
            dot.BorderBrush = Brush(selected ? "#19F58C" : installed ? "#5A6378" : "#2E3442");
            dot.Background = Brush(selected ? "#19F58C" : "#00000000");
            row.Background = Brush(selected ? "#0F2A22" : "#00000000");
            row.BorderBrush = Brush(selected ? "#1D5A47" : "#00000000");
            name.Opacity = installed ? 1 : 0.55;
            play.IsEnabled = installed;
            play.Opacity = installed ? 1 : 0;
            ToolTip.SetTip(row, installed ? (selected ? "Reading with this voice" : "Click to read with this voice")
                : v.Family == VoiceFamily.Chatterbox ? "Download Kokoro below first: this voice is copied from it" : "Download it first");
        }

        _rows.Add((v, Refresh));
        if (v.Family != VoiceFamily.Windows)
        {
            if (!_packRefresh.TryGetValue(v.Pack, out var list))
                _packRefresh[v.Pack] = list = new();
            list.Add(Refresh);
        }

        Refresh();
        return row;
    }

    private void RefreshAll()
    {
        foreach (var (_, refresh) in _rows)
            refresh();
        foreach (var list in _packRefresh.Values)
            foreach (var r in list)
                r();
        var now = _sp.CurrentVoice;
        _nowName.Text = now.Name;
        _nowTags.Text = $"{now.Family} · {now.Tags}" + (VoiceStore.IsInstalled(now) ? "" : " · not downloaded yet");
        _hint.Text = (S.ReadHotkey is { } k
            ? $"Select text in any app and press {k} to hear it.{(S.ReadAgainStops ? " Press it again to stop." : "")}"
            : "Set a hotkey on the Settings tab to read selected text from any app.") + " Click a downloaded voice to use it; ▶ plays a sample.";
        if (_storage is not null)
            _storage.Text = $"Downloaded voices use {VoiceStore.SizeOnDisk() / 1048576.0:0} MB";
        Reader?.Refresh();
    }

    private void OnChatterboxChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (IsVisible)
            RebuildLists();
    });

    private void OnDownloadChanged(string pack)
    {
        if (_packRefresh.TryGetValue(pack, out var list))
            foreach (var r in list)
                r();
    }

    private void OnDownloadFinished(string pack)
    {
        // The first voice downloaded becomes the one used, unless one is already picked and ready.
        var chosen = VoiceCatalog.Find(S.Voice);
        if (chosen is null || !VoiceStore.IsInstalled(chosen) || chosen.Family == VoiceFamily.Windows)
        {
            var first = VoiceCatalog.KokoroVoices.Concat(VoiceCatalog.Piper).FirstOrDefault(v => v.Pack == pack && (v.Star || v.Family == VoiceFamily.Piper));
            if (first is not null && (chosen is null || !VoiceStore.IsInstalled(chosen)))
                _sp.SetVoice(first);
        }

        RefreshAll();
    }

    // ---- Settings tab ------------------------------------------------------------------------------------------------

    private Control BuildSettingsTab()
    {
        var root = _settingsRoot = new StackPanel { Spacing = 12 };

        var hotkeys = new StackPanel { Spacing = 10 };
        hotkeys.Children.Add(new TextBlock { Text = "HOTKEYS", Classes = { "label" } });
        hotkeys.Children.Add(HotkeyRow("Read selected text", "Copies the selection in the app in front and reads it", S.ReadHotkey, v => S.ReadHotkey = v));
        hotkeys.Children.Add(HotkeyRow("Pause / resume", "", S.PauseHotkey, v => S.PauseHotkey = v));
        hotkeys.Children.Add(HotkeyRow("Stop", "", S.StopHotkey, v => S.StopHotkey = v));
        hotkeys.Children.Add(Toggle("Pressing Read again stops reading", "Off: it starts over with the new selection", S.ReadAgainStops, v => S.ReadAgainStops = v));
        root.Children.Add(Card(hotkeys));

        var reading = new StackPanel { Spacing = 10 };
        reading.Children.Add(new TextBlock { Text = "READING", Classes = { "label" } });
        reading.Children.Add(Toggle("Nothing selected? Read the clipboard", "Handy for text you copied earlier", S.ClipboardFallback, v => S.ClipboardFallback = v));
        reading.Children.Add(Toggle("Show a banner while reading", "Bottom-right corner; click it to hide", S.ShowBanner, v => S.ShowBanner = v));
        var volume = new Slider { Minimum = 0, Maximum = 1, Value = S.Volume, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        _volumeLabel = new TextBlock { Classes = { "muted" }, Width = 52, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        void ShowVolume() => _volumeLabel.Text = $"{S.Volume * 100:0}%";
        volume.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty)
                return;
            S.Volume = Math.Round(volume.Value, 2);
            _sp.Reader.Volume = (float)S.Volume;
            S.Save();
            ShowVolume();
        };
        ShowVolume();
        var vrow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        vrow.Children.Add(new TextBlock { Text = "Volume", VerticalAlignment = VerticalAlignment.Center });
        Place(vrow, volume, 1);
        Place(vrow, _volumeLabel, 2);
        reading.Children.Add(vrow);

        // Chatterbox holds a few GB of graphics memory while loaded; let it go when unused (games want it).
        (int Minutes, string Label)[] idle = [(10, "After 10 minutes unused"), (30, "After 30 minutes unused"), (60, "After an hour unused"), (0, "Never (always ready)")];
        var idleBox = new ComboBox { ItemsSource = idle.Select(i => i.Label).ToList(), MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        idleBox.SelectedIndex = Math.Max(0, Array.FindIndex(idle, i => i.Minutes == S.ChatterboxIdleMinutes));
        idleBox.SelectionChanged += (_, _) =>
        {
            if (idleBox.SelectedIndex < 0)
                return;
            S.ChatterboxIdleMinutes = idle[idleBox.SelectedIndex].Minutes;
            Chatterbox.IdleMinutes = S.ChatterboxIdleMinutes;
            S.Save();
        };
        var irow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        irow.Children.Add(new StackPanel
        {
            Spacing = 1, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Free the graphics card from Chatterbox" },
                new TextBlock { Text = "It reloads in about 15 s next time", Classes = { "muted" }, FontSize = 11 },
            },
        });
        Place(irow, idleBox, 1);
        if (!Packaged.IsPackaged)
            reading.Children.Add(irow);
        root.Children.Add(Card(reading));

        root.Children.Add(Card(BuildBrowserCard()));

        var app = new StackPanel { Spacing = 10 };
        app.Children.Add(new TextBlock { Text = "APP", Classes = { "label" } });
        var startup = Toggle("Start with Windows", "Waits quietly in the tray", Autostart.IsEnabled(), v =>
        {
            try { Autostart.Set(v); } catch { /* shown as off next time */ }
        }, save: false);
        app.Children.Add(startup);
        if (Packaged.IsPackaged)
            _ = Autostart.IsEnabledAsync().ContinueWith(t => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (((Panel)startup).Children.OfType<ToggleSwitch>().FirstOrDefault() is { } sw && t.IsCompletedSuccessfully && sw.IsChecked != t.Result)
                {
                    _settingStartup = true;
                    sw.IsChecked = t.Result;
                    _settingStartup = false;
                }
            }), TaskScheduler.Default);
        app.Children.Add(Toggle("Start in the tray", "Even when opened by hand", S.StartInTray, v => S.StartInTray = v));
        app.Children.Add(Toggle("Show tray icon", "Off: start the app again to open this window", _tray?.Visible ?? pxdLink.TrayIconVisible(TrayLink.AppId), v =>
        {
            if (_tray is not null)
                _tray.Set(v);
            else
                pxdLink.SetTrayIconVisible(TrayLink.AppId, v);
        }, save: false));
        _storage = new TextBlock { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
        var openFolder = new Button { Classes = { "ghost" }, Content = "Open folder", Padding = new Thickness(10, 4) };
        openFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(VoiceStore.Folder);
            Open(VoiceStore.Folder);
        };
        var srow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        srow.Children.Add(_storage);
        Place(srow, openFolder, 1);
        app.Children.Add(srow);
        var intro = new Button { Classes = { "ghost" }, Content = "Show the intro", Padding = new Thickness(10, 4) };
        ToolTip.SetTip(intro, "The three \"start here\" cards on the Read tab");
        intro.Click += (_, _) =>
        {
            Reader.ShowIntro();
            Tabs.SelectedIndex = 0;
        };
        var quit = new Button { Content = "Quit pxd Speak", HorizontalAlignment = HorizontalAlignment.Left };
        quit.Click += (_, _) => _quit?.Invoke();
        app.Children.Add(new WrapPanel { Children = { quit, intro } });
        root.Children.Add(Card(app));

        root.Children.Add(Card(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "VOICES COME FROM", Classes = { "label" } },
                Credit("Kokoro-82M by hexgrad", "Apache-2.0", "https://huggingface.co/hexgrad/Kokoro-82M"),
                Credit("Piper voices (rhasspy / Open Home Foundation)", "each voice has its own licence: see MODEL_CARD in its folder", "https://huggingface.co/rhasspy/piper-voices"),
                Credit("sherpa-onnx by k2-fsa (runs the voices)", "Apache-2.0", "https://github.com/k2-fsa/sherpa-onnx"),
                Credit("eSpeak NG (pronunciation)", "GPL-3.0", "https://github.com/espeak-ng/espeak-ng"),
                new TextBlock { Text = $"pxd Speak {pxdUpdate.ThisVersion()} · everything runs on this PC; nothing you read is sent anywhere.", Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) },
            },
        }));
        return root;
    }

    private TextBlock _browserState = null!;

    /// <summary>The browser extension: is it linked, the on/off switch, and how to add it.</summary>
    private Control BuildBrowserCard()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(new TextBlock { Text = "WEB PAGES · WORD HIGHLIGHTING", Classes = { "label" } });
        _browserState = new TextBlock { FontWeight = FontWeight.SemiBold };
        p.Children.Add(_browserState);
        p.Children.Add(Toggle("Highlight each word on the page while reading", "Needs the pxd Speak extension in Edge or Chrome", S.BrowserHighlight, v => S.BrowserHighlight = v));

        if (BrowserStores.Listed)
        {
            var stores = new WrapPanel();
            if (BrowserStores.Chrome.Length > 0)
                stores.Children.Add(StoreButton("Add to Chrome", BrowserStores.Chrome));
            if (BrowserStores.Edge.Length > 0)
                stores.Children.Add(StoreButton("Add to Edge", BrowserStores.Edge));
            p.Children.Add(stores);
        }

        var open = new Button { Content = "Open the extension folder" };
        open.Click += (_, _) => Open(Packaged.BrowserExtensionFolder());
        var edge = new Button { Classes = { "ghost" }, Content = "Copy edge://extensions", Padding = new Thickness(10, 4) };
        edge.Click += async (_, _) => await (Clipboard?.SetTextAsync("edge://extensions") ?? Task.CompletedTask);
        var chrome = new Button { Classes = { "ghost" }, Content = "Copy chrome://extensions", Padding = new Thickness(10, 4) };
        chrome.Click += async (_, _) => await (Clipboard?.SetTextAsync("chrome://extensions") ?? Task.CompletedTask);
        var unpacked = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new WrapPanel { Children = { open, edge, chrome } },
                new TextBlock
                {
                    Text = "Open edge://extensions (or chrome://extensions), turn on Developer mode, click \"Load unpacked\" and pick the " +
                           "extension folder.",
                    Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        if (BrowserStores.Listed)
            p.Children.Add(new Expander { Header = "Advanced: load it unpacked", Content = unpacked, HorizontalAlignment = HorizontalAlignment.Stretch });
        else
            p.Children.Add(unpacked);
        p.Children.Add(new TextBlock
        {
            Text = "Then select text on any page and press the hotkey, or right-click › Read aloud with pxd Speak. " +
                   "Click a word to jump there; Esc stops. It talks only to this app, on this PC.",
            Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });
        ShowBrowserState();
        if (_sp.Bridge is { } bridge)
        {
            void OnChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(ShowBrowserState);
            bridge.Changed += OnChanged;
            Closed += (_, _) => bridge.Changed -= OnChanged;
        }

        return p;
    }

    private void ShowBrowserState()
    {
        var b = _sp.Bridge;
        (_browserState.Text, _browserState.Foreground) = b switch
        {
            null => ("Extension link is off in this mode", Brush("#8A92A6")),
            { Error: { } e } => ("Extension link unavailable: " + e, Brush("#F2666A")),
            { Count: > 0 } => ("● Linked to " + string.Join(" and ", b.Browsers), Brush("#19F58C")),
            _ => ("○ No browser linked yet: add the extension (below), or open your browser", Brush("#F5B14C")),
        };
    }

    private static Button StoreButton(string text, string url)
    {
        var b = new Button { Classes = { "primary" }, Content = text, Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => Open(url);
        return b;
    }

    /// <summary>A card at the top of the Settings tab (add-ons: "What's new / Pro").</summary>
    public void AddSettingsCard(Control c) => _settingsRoot.Children.Insert(0, c is Border { Classes: var k } && k.Contains("card") ? c : Card(c));

    private bool _settingStartup;

    private Control Credit(string what, string licence, string url)
    {
        var link = new Button { Classes = { "ghost" }, Content = what, Padding = new Thickness(0, 2), MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Left, Foreground = Brush("#00C8FF") };
        link.Click += (_, _) => Open(url);
        return new StackPanel { Spacing = 0, Children = { link, new TextBlock { Text = licence, Classes = { "muted" }, FontSize = 11 } } };
    }

    private Control HotkeyRow(string title, string sub, Hotkey? value, Action<Hotkey?> set)
    {
        var button = new HotkeyButton(value);
        button.CaptureStarted += _sp.PauseHotkeys;
        button.CaptureEnded += () =>
        {
            _sp.RegisterHotkeys();
            ShowConflicts();
        };
        button.ValueChanged += v =>
        {
            set(v);
            S.Save();
            _sp.RegisterHotkeys();
            ShowConflicts();
            RefreshAll();
        };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = title } } };
        if (sub.Length > 0)
            text.Children.Add(new TextBlock { Text = sub, Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        g.Children.Add(text);
        Place(g, button, 1);
        return g;
    }

    private Control Toggle(string title, string sub, bool value, Action<bool> set, bool save = true)
    {
        var sw = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        sw.IsCheckedChanged += (_, _) =>
        {
            if (_settingStartup)
                return;
            set(sw.IsChecked == true);
            if (save)
                S.Save();
            RefreshAll();
        };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(new StackPanel
        {
            Spacing = 1, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = title }, new TextBlock { Text = sub, Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap } },
        });
        Place(g, sw, 1);
        return g;
    }

    private void ShowConflicts()
    {
        _conflicts.IsVisible = _sp.HotkeyConflicts.Count > 0;
        _conflictText.Text = "Another program already uses " + string.Join(", ", _sp.HotkeyConflicts) + ". Pick another combination on the Settings tab.";
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static Border Card(Control child) => new() { Classes = { "card" }, Padding = new Thickness(16), Child = child };

    private static void Place(Grid g, Control c, int column)
    {
        Grid.SetColumn(c, column);
        g.Children.Add(c);
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { /* nothing to open it with */ }
    }

    public void Activate2()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }
}
