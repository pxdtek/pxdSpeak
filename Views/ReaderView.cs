using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using pxdSpeak.Platform;
using pxdSpeak.Services;
using pxdSpeak.Speech;

namespace pxdSpeak.Views;

/// <summary>
/// The Read tab: a big text area to paste into, a voice and speed row, and a transport bar. While reading, the text
/// turns into a read-only view where the sentence being read is tinted, the word being heard lights up, the view
/// scrolls along (unless the user is scrolling), and clicking a word reads from there. Stop (or Esc) brings the
/// editable text back.
/// </summary>
public sealed class ReaderView : UserControl
{
    private static readonly IBrush SentenceBg = Brush("#1C4250");
    private static readonly IBrush WordBg = Brush("#19F58C");
    private static readonly IBrush WordFg = Brush("#04140C");
    private static readonly IBrush DoneFg = Brush("#8A92A6");

    private readonly Speaker _sp;
    private readonly Action _showVoices;
    private readonly Action _showSettings;
    private readonly TextBox _editor;
    private readonly ScrollViewer _view;
    private readonly StackPanel _paragraphs;
    private readonly Button _voiceButton, _play, _stop, _back, _next, _pasteRead, _clear;
    private readonly Slider _speed;
    private readonly TextBlock _speedLabel, _progressText, _count;
    private readonly ProgressBar _progress;
    private readonly Border _intro;
    private readonly Grid _textArea;

    private ReadDoc? _doc;
    private string? _docText;
    private readonly List<TextBlock> _blockViews = new();
    private readonly List<List<Run>> _blockRuns = new();
    private readonly List<(int Start, int End, int Word)> _hitMap = new();
    private int[] _runOfWord = [];
    private bool _reading;
    private int _session;
    private int _word = -1, _sentence = -1;
    private DateTime _userScrolledAt = DateTime.MinValue;

    /// <summary>Extra buttons (Pro tools) go here, after Paste and read / Clear.</summary>
    public WrapPanel ProTools { get; } = new() { Orientation = Orientation.Horizontal };

    /// <summary>Files dropped on the text area (set by the Pro add-on; dropping is off while null).</summary>
    public Func<IReadOnlyList<string>, bool>? FileDrop
    {
        get => _fileDrop;
        set
        {
            _fileDrop = value;
            DragDrop.SetAllowDrop(_textArea, value is not null);
        }
    }

    private Func<IReadOnlyList<string>, bool>? _fileDrop;

    public bool IsReading => _reading;

    /// <summary>The word now being heard (or -1) and the sentence count, for the self-test.</summary>
    public (int Word, int Sentence, int Sentences) Position => (_word, _sentence, _doc?.Sentences.Count ?? 0);

    private AppSettings S => _sp.Settings;

    public ReaderView(Speaker sp, Action showVoices, Action showSettings)
    {
        _sp = sp;
        _showVoices = showVoices;
        _showSettings = showSettings;

        // ── voice + speed row ──
        _voiceButton = new Button { Classes = { "ghost" }, Padding = new Thickness(8, 4), MinHeight = 32, HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(_voiceButton, "Pick the voice");
        var voiceMenu = new MenuFlyout();
        voiceMenu.Opening += (_, _) => FillVoiceMenu(voiceMenu);
        _voiceButton.Flyout = voiceMenu;
        _speed = new Slider { Minimum = 0.5, Maximum = 2.0, Value = S.Speed, Width = 150, TickFrequency = 0.05, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        _speedLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 46, Classes = { "muted" } };
        _speed.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty)
                return;
            S.Speed = Math.Round(_speed.Value, 2);
            S.Save();
            ShowSpeed();
        };
        ShowSpeed();
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        top.Children.Add(_voiceButton);
        Place(top, new TextBlock { Text = "SPEED", Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, 1);
        Place(top, _speed, 2);
        Place(top, _speedLabel, 3);

        // ── the text: editable box, or the highlighted read-only view while reading ──
        _editor = new TextBox
        {
            Text = S.TryText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "Paste or type text here…",
            FontSize = S.ReaderFontSize,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(16, 14),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        _editor.TextChanged += (_, _) =>
        {
            S.TryText = _editor.Text ?? "";
            S.Save();
            ShowCount();
            TextChanged?.Invoke();
        };
        _paragraphs = new StackPanel { Spacing = 12, Margin = new Thickness(16, 14, 22, 40) };
        _view = new ScrollViewer { Content = _paragraphs, IsVisible = false, Focusable = true };
        _view.AddHandler(PointerWheelChangedEvent, (_, _) => _userScrolledAt = DateTime.UtcNow, RoutingStrategies.Tunnel);
        _view.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
                _userScrolledAt = DateTime.UtcNow;
        }, RoutingStrategies.Tunnel);
        var viewFrame = new Border
        {
            Background = Brush("#0E1015"), BorderBrush = Brush("#272C39"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Child = _view, IsVisible = false,
        };
        _view.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
                viewFrame.IsVisible = _view.IsVisible;
        };
        _textArea = new Grid { Children = { _editor, viewFrame } };
        _textArea.AddHandler(DragDrop.DropEvent, OnDrop);
        _textArea.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = _fileDrop is not null && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);

        // ── actions under the text ──
        _pasteRead = new Button { Content = "Paste and read", Padding = new Thickness(12, 6), MinHeight = 32 };
        ToolTip.SetTip(_pasteRead, "Puts what you copied here and reads it");
        _pasteRead.Click += async (_, _) => await PasteAndRead();
        _clear = new Button { Classes = { "ghost" }, Content = "Clear", Padding = new Thickness(10, 6), MinHeight = 32 };
        _clear.Click += (_, _) =>
        {
            StopReading();
            _editor.Text = "";
            _editor.Focus();
        };
        _count = new TextBlock { Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(_pasteRead);
        Place(actions, _clear, 1);
        Place(actions, _count, 3);
        // Add-on tools (Store build) get their own row, shown only when there are some.
        ProTools.Margin = new Thickness(-4, 4, 0, 0);
        ProTools.Children.CollectionChanged += (_, _) => ProTools.IsVisible = ProTools.Children.Count > 0;
        ProTools.IsVisible = false;
        var actionRows = new StackPanel { Children = { actions, ProTools } };

        // ── transport ──
        _back = Transport(PrevIcon, "Back one sentence");
        _back.Click += (_, _) => Jump(-1);
        _play = new Button { Classes = { "primary" }, Width = 64, Height = 48, MinHeight = 48, FontSize = 20, Padding = new Thickness(0), CornerRadius = new CornerRadius(24) };
        _play.Click += (_, _) => PlayPause();
        _next = Transport(NextIcon, "Forward one sentence");
        _next.Click += (_, _) => Jump(+1);
        _stop = Transport(StopIcon, "Stop (Esc)");
        _stop.Click += (_, _) => StopReading();
        _progressText = new TextBlock { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, Foreground = Brush("#19F58C"), VerticalAlignment = VerticalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _back, _play, _next, _stop } };
        var where = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0), Children = { _progressText, _progress } };
        var transport = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        transport.Children.Add(buttons);
        Place(transport, where, 1);
        var transportCard = new Border { Classes = { "card" }, Padding = new Thickness(14, 10), Margin = new Thickness(0, 10, 0, 0), Child = transport };

        // ── first run ──
        _intro = new Border { Margin = new Thickness(0, 0, 0, 10) };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        root.Children.Add(_intro);
        Grid.SetRow(top, 1);
        top.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(top);
        Grid.SetRow(_textArea, 2);
        root.Children.Add(_textArea);
        Grid.SetRow(actionRows, 3);
        root.Children.Add(actionRows);
        Grid.SetRow(transportCard, 4);
        root.Children.Add(transportCard);
        Content = root;

        _sp.StateChanged += OnState;
        _sp.InAppEnded += OnInAppEnded;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) =>
        {
            _sp.StateChanged -= OnState;
            _sp.InAppEnded -= OnInAppEnded;
        };

        BuildIntro();
        Refresh();
    }

    /// <summary>Raised when the text in the box changes.</summary>
    public event Action? TextChanged;

    /// <summary>The text in the box.</summary>
    public string Text
    {
        get => _editor.Text ?? "";
        set
        {
            StopReading();
            _editor.Text = value;
        }
    }

    /// <summary>The selected part of the text box, or all of it when nothing is selected (Pro: Save as audio).</summary>
    public string SelectedOrAllText => !_reading && _editor.SelectedText is { Length: > 0 } sel && !string.IsNullOrWhiteSpace(sel) ? sel : Text;

    /// <summary>A line in the transport bar while nothing is being read ("Opened book.epub · 12 chapters").</summary>
    public void ShowStatus(string text)
    {
        if (!_reading)
            _progressText.Text = text;
    }

    /// <summary>Puts <paramref name="text"/> in the box and reads it from the start (tray: "Open reader with clipboard").</summary>
    public void LoadAndRead(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        Text = text;
        Start(0);
    }

    // ── voice row ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private void FillVoiceMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        var current = _sp.CurrentVoice;
        var installed = VoiceCatalog.ChatterboxVoices().Concat(VoiceCatalog.KokoroVoices).Concat(VoiceCatalog.Piper).Concat(WindowsVoices.All())
            .Where(VoiceStore.IsInstalled).ToList();
        foreach (var v in installed)
        {
            var voice = v;
            var item = new MenuItem { Header = $"{(v.Id == current.Id ? "●  " : "     ")}{v.Name}   ({v.Family}, {v.Tags})" };
            item.Click += (_, _) =>
            {
                _sp.SetVoice(voice);
                VoiceChanged?.Invoke();
                Refresh();
            };
            menu.Items.Add(item);
        }

        if (installed.Count > 0)
            menu.Items.Add(new Separator());
        var more = new MenuItem { Header = "Download more voices…" };
        more.Click += (_, _) => _showVoices();
        menu.Items.Add(more);
    }

    /// <summary>Raised after a voice was picked here (the Voices tab updates its list).</summary>
    public event Action? VoiceChanged;

    private void ShowSpeed() => _speedLabel.Text = S.Speed.ToString("0.00") + "×";

    /// <summary>Voice label, speed, counts and buttons, after anything changed elsewhere.</summary>
    public void Refresh()
    {
        var v = _sp.CurrentVoice;
        var ready = VoiceStore.IsInstalled(v);
        _voiceButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                new TextBlock { Text = "VOICE", Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = $"{v.Name} ({v.Accent})", FontWeight = FontWeight.SemiBold, FontSize = 14, Foreground = Brush(ready ? "#E8EAF0" : "#F5B14C"), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = ready ? "▾" : "▾  not downloaded", Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        if (Math.Abs(_speed.Value - S.Speed) > 0.001)
            _speed.Value = S.Speed;
        ShowCount();
        OnState(_sp.Reader.State);
    }

    private void ShowCount()
    {
        var text = _editor.Text ?? "";
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        // About 160 words a minute at 1×.
        var minutes = words / (160.0 * Math.Max(0.5, S.Speed));
        _count.Text = words == 0 ? "" : $"{words:N0} words · about {(minutes < 1 ? "<1" : Math.Round(minutes).ToString("0"))} min";
    }

    // ── reading ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private void PlayPause()
    {
        switch (_sp.Reader.State)
        {
            case ReaderState.Speaking or ReaderState.Paused when _reading:
                _sp.Reader.TogglePause();
                return;
            case ReaderState.Loading when _reading:
                return;
        }

        // Carry on from the last word if the text hasn't changed since; otherwise from the top.
        var from = _docText == Text && _word > 0 && _word < (_doc?.WordCount ?? 0) - 1 ? _doc!.Sentences[_doc.SentenceOf(_word)].First : 0;
        Start(from);
    }

    private Task PasteAndRead()
    {
        var text = Selection.ReadText();
        if (string.IsNullOrWhiteSpace(text))
        {
            _progressText.Text = "Nothing on the clipboard to paste. Copy some text first.";
            return Task.CompletedTask;
        }

        LoadAndRead(text);
        return Task.CompletedTask;
    }

    /// <summary>Reads the box's text from word <paramref name="from"/>, switching to the highlighted view.</summary>
    public void Start(int from)
    {
        var text = Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _progressText.Text = "Paste or type something above first.";
            _editor.Focus();
            return;
        }

        if (_doc is null || _docText != text)
        {
            _doc = ReadDoc.From(text);
            _docText = text;
            BuildView(_doc);
        }

        if (_doc.WordCount == 0)
            return;
        var session = ++_session;
        var doc = _doc;
        var ok = _sp.ReadInApp(doc.Tracked(from, (i, _, _) => Dispatcher.UIThread.Post(() =>
        {
            if (session == _session && _reading)
                Highlight(i);
        })));
        if (!ok)
            return;
        _reading = true;
        _userScrolledAt = DateTime.MinValue;
        ShowView(true);
        Highlight(from, scroll: true);
        _view.Focus();
        OnState(_sp.Reader.State);
    }

    private void Jump(int by)
    {
        if (_doc is null || !_reading)
        {
            if (by > 0)
                Start(0);
            return;
        }

        var s = _doc.SentenceOf(Math.Max(0, _word));
        var target = by < 0 && _word > _doc.Sentences[s].First + 2 ? s : Math.Clamp(s + by, 0, _doc.Sentences.Count - 1);
        _userScrolledAt = DateTime.MinValue;
        Start(_doc.Sentences[target].First);
    }

    public void StopReading()
    {
        var was = _reading;
        _session++;
        _reading = false;
        if (was)
            _sp.Reader.Stop();
        ShowView(false);
        OnState(_sp.Reader.State);
    }

    private void OnInAppEnded()
    {
        if (!_reading)
            return;
        var finished = _doc is not null && _word >= _doc.WordCount - 1;
        _session++;
        _reading = false;
        ShowView(false);
        if (finished)
            _word = -1;
        OnState(_sp.Reader.State);
        if (finished && _doc is not null)
            _progressText.Text = $"Done · {Plural(_doc.Sentences.Count, "sentence")} read";
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _reading)
        {
            StopReading();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && _reading && e.Source is not TextBox)
        {
            _sp.Reader.TogglePause();
            e.Handled = true;
        }
    }

    private void OnState(ReaderState s)
    {
        var mine = _reading;
        _play.Content = mine && s == ReaderState.Speaking ? "❚❚" : mine && s == ReaderState.Loading ? "…" : "▶";
        ToolTip.SetTip(_play, mine && s == ReaderState.Speaking ? "Pause (Space)" : mine && s == ReaderState.Paused ? "Resume (Space)" : "Read it");
        _stop.IsEnabled = mine;
        _back.IsEnabled = mine;
        _next.IsEnabled = mine || !string.IsNullOrWhiteSpace(Text);
        if (!mine)
        {
            _progress.Value = 0;
            _progressText.Text = string.IsNullOrWhiteSpace(Text)
                ? "Paste or type something, then press ▶"
                : S.ReadHotkey is { } k ? $"Press ▶ to read it here · or select text in any app and press {k}" : "Press ▶ to read it here";
        }
        else if (s == ReaderState.Loading && _word <= 0)
            _progressText.Text = "Getting the voice ready…";
        else
            ShowProgress();
    }

    private void ShowProgress()
    {
        if (_doc is null || _doc.Sentences.Count == 0)
            return;
        var s = _doc.SentenceOf(Math.Max(0, _word));
        _progressText.Text = $"Sentence {s + 1} of {_doc.Sentences.Count}" + (_sp.Reader.State == ReaderState.Paused ? " · paused" : "");
        _progress.Value = _doc.WordCount <= 1 ? 0 : Math.Max(0, _word) / (double)(_doc.WordCount - 1);
    }

    private void ShowView(bool reading)
    {
        _view.IsVisible = reading;
        _editor.IsVisible = !reading;
        _pasteRead.IsEnabled = true;
        if (!reading)
            ClearHighlight();
    }

    // ── the highlighted view ──────────────────────────────────────────────────────────────────────────────────────────

    private void BuildView(ReadDoc doc)
    {
        _paragraphs.Children.Clear();
        _blockViews.Clear();
        _blockRuns.Clear();
        _runOfWord = new int[doc.WordCount];
        _word = _sentence = -1;
        for (var b = 0; b < doc.Blocks.Count; b++)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = S.ReaderFontSize, LineHeight = S.ReaderFontSize * 1.55, Cursor = new Cursor(StandardCursorType.Hand) };
            var runs = new List<Run>();
            var block = doc.Blocks[b];
            if (doc.Headings[b])
            {
                tb.FontWeight = FontWeight.Bold;
                tb.FontSize = S.ReaderFontSize * 1.25;
            }
            for (var p = 0; p < block.Count; p++)
            {
                var piece = block[p];
                var run = new Run(piece.Text);
                if (piece.Word < 0)
                    run.Foreground = DoneFg;
                else
                    _runOfWord[piece.Word] = runs.Count;
                runs.Add(run);
                tb.Inlines!.Add(run);
                // The space after a word is its own run, so the word highlight stops at the word.
                if (p < block.Count - 1)
                {
                    var space = new Run(" ");
                    runs.Add(space);
                    tb.Inlines.Add(space);
                }
            }

            var index = b;
            tb.PointerPressed += (_, e) => OnWordClick(index, tb, e);
            _blockViews.Add(tb);
            _blockRuns.Add(runs);
            _paragraphs.Children.Add(tb);
        }
    }

    private void OnWordClick(int block, TextBlock tb, PointerPressedEventArgs e)
    {
        if (_doc is null || !e.GetCurrentPoint(tb).Properties.IsLeftButtonPressed)
            return;
        var hit = tb.TextLayout.HitTestPoint(e.GetPosition(tb));
        var pos = hit.TextPosition;
        var at = 0;
        foreach (var piece in _doc.Blocks[block])
        {
            var len = piece.Text.Length + 1;
            if (pos < at + len)
            {
                var word = piece.Word >= 0 ? piece.Word : NextWord(block, piece);
                if (word >= 0)
                    Start(word);
                e.Handled = true;
                return;
            }

            at += len;
        }
    }

    private int NextWord(int block, ReadDoc.Piece after)
    {
        var pieces = _doc!.Blocks[block];
        var i = pieces.IndexOf(after);
        for (var k = i; k < pieces.Count; k++)
            if (pieces[k].Word >= 0)
                return pieces[k].Word;
        return -1;
    }

    private Run RunOf(int word) => _blockRuns[_doc!.BlockOf[word]][_runOfWord[word]];

    /// <summary>The space after a word, if there is one in its block.</summary>
    private Run? SpaceAfter(int word)
    {
        var runs = _blockRuns[_doc!.BlockOf[word]];
        var i = _runOfWord[word] + 1;
        return i < runs.Count && runs[i].Text == " " ? runs[i] : null;
    }

    private void Highlight(int word, bool scroll = true)
    {
        if (_doc is null || word < 0 || word >= _doc.WordCount)
            return;
        var sentence = _doc.SentenceOf(word);
        if (sentence != _sentence)
        {
            if (_sentence >= 0)
                Paint(_doc.Sentences[_sentence], null);
            Paint(_doc.Sentences[sentence], SentenceBg);
            _sentence = sentence;
        }
        else if (_word >= 0 && _word < _doc.WordCount)
        {
            var old = RunOf(_word);
            old.Background = SentenceBg;
            old.ClearValue(TextElement.ForegroundProperty);
        }

        var run = RunOf(word);
        run.Background = WordBg;
        run.Foreground = WordFg;
        _word = word;
        ShowProgress();
        if (scroll)
            ScrollTo(word);
    }

    private void Paint((int First, int Last) range, IBrush? bg)
    {
        for (var w = range.First; w <= range.Last && w < _doc!.WordCount; w++)
        {
            var r = RunOf(w);
            var space = w < range.Last ? SpaceAfter(w) : null;
            foreach (var x in space is null ? new[] { r } : new[] { r, space })
            {
                if (bg is null)
                    x.ClearValue(TextElement.BackgroundProperty);
                else
                    x.Background = bg;
                x.ClearValue(TextElement.ForegroundProperty);
            }
        }
    }

    private void ClearHighlight()
    {
        if (_doc is null)
            return;
        if (_sentence >= 0 && _sentence < _doc.Sentences.Count)
            Paint(_doc.Sentences[_sentence], null);
        _sentence = -1;
    }

    /// <summary>Keeps the word in view (about a third of the way down), unless the user scrolled in the last 4 s.</summary>
    private void ScrollTo(int word)
    {
        if ((DateTime.UtcNow - _userScrolledAt).TotalSeconds < 4)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_doc is null || word >= _doc.WordCount)
                return;
            var tb = _blockViews[_doc.BlockOf[word]];
            var charAt = 0;
            foreach (var p in _doc.Blocks[_doc.BlockOf[word]])
            {
                if (p.Word == word)
                    break;
                charAt += p.Text.Length + 1;
            }

            Rect rect;
            try { rect = tb.TextLayout.HitTestTextPosition(charAt); }
            catch { rect = new Rect(0, 0, 1, tb.Bounds.Height); }
            if (tb.TranslatePoint(rect.TopLeft, _paragraphs) is not { } p2)
                return;
            var viewport = _view.Viewport.Height;
            var y = p2.Y;
            var offset = _view.Offset.Y;
            if (y < offset + 20 || y + rect.Height > offset + viewport - 40)
                _view.Offset = new Vector(_view.Offset.X, Math.Max(0, y - viewport / 3));
        }, DispatcherPriority.Background);
    }

    /// <summary>Self-test: closes the intro cards without saving.</summary>
    internal void HideIntro()
    {
        S.IntroSeen = true;
        BuildIntro();
    }

    /// <summary>Self-test: shows <paramref name="text"/> as if it were being read, at word <paramref name="word"/>, without sound.</summary>
    internal void ShowForTest(string text, int word)
    {
        Text = text;
        _doc = ReadDoc.From(text);
        _docText = text;
        BuildView(_doc);
        _reading = true;
        ShowView(true);
        Highlight(word);
        _play.Content = "❚❚";
        _stop.IsEnabled = _back.IsEnabled = _next.IsEnabled = true;
    }

    // ── drag and drop (Pro) ───────────────────────────────────────────────────────────────────────────────────────────

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_fileDrop is null)
            return;
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (files is { Count: > 0 } && _fileDrop(files))
            e.Handled = true;
    }

    // ── first run ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Shows the three intro cards again (Settings › Show the intro).</summary>
    public void ShowIntro()
    {
        S.IntroSeen = false;
        S.Save();
        BuildIntro();
    }

    private void BuildIntro()
    {
        if (S.IntroSeen)
        {
            _intro.Child = null;
            _intro.IsVisible = false;
            return;
        }

        _intro.IsVisible = true;
        var voiceReady = VoiceCatalog.KokoroVoices.Any(VoiceStore.IsInstalled) || VoiceCatalog.Piper.Any(VoiceStore.IsInstalled);

        // ① a voice
        var one = new StackPanel { Spacing = 6 };
        one.Children.Add(Step("1", "Pick a voice"));
        if (voiceReady)
            one.Children.Add(new TextBlock { Text = "✓ " + _sp.CurrentVoice.Name + " is ready. More on the Voices tab.", Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        else
        {
            one.Children.Add(new TextBlock { Text = $"Kokoro: 28 natural voices in one {VoiceCatalog.Kokoro.SizeMb:0} MB download. Windows voices work meanwhile.", Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            var get = new Button { Classes = { "primary" }, Padding = new Thickness(10, 4), MinHeight = 30, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
            void ShowGet()
            {
                var running = Downloads.IsRunning(VoiceCatalog.Kokoro.Name);
                get.Content = VoiceStore.IsInstalled(VoiceCatalog.Kokoro.Name) ? "✓ Downloaded"
                    : running ? $"Downloading {Downloads.Progress(VoiceCatalog.Kokoro.Name):P0}" : "Download Kokoro";
                get.IsEnabled = !running && !VoiceStore.IsInstalled(VoiceCatalog.Kokoro.Name);
            }

            get.Click += (_, _) =>
            {
                Downloads.Start(VoiceCatalog.Kokoro);
                ShowGet();
            };
            void OnChanged(string pack)
            {
                if (pack == VoiceCatalog.Kokoro.Name)
                    ShowGet();
            }

            Downloads.Changed += OnChanged;
            Downloads.Finished += OnChanged;
            DetachedFromVisualTree += (_, _) =>
            {
                Downloads.Changed -= OnChanged;
                Downloads.Finished -= OnChanged;
            };
            ShowGet();
            one.Children.Add(get);
        }

        // ② paste and play
        var two = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Step("2", "Paste and press ▶"),
                new TextBlock { Text = "Each word lights up as it's read. Click a word to jump there; Esc stops.", Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            },
        };

        // ③ anywhere
        var hotkey = S.ReadHotkey?.ToString() ?? "a hotkey (Settings)";
        var add = new Button { Classes = { "ghost" }, Content = "Add to Chrome / Edge", Padding = new Thickness(0, 2), MinHeight = 0, FontSize = 12, Foreground = Brush("#00C8FF"), HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) =>
        {
            if (BrowserStores.Preferred() is { } url)
                Open(url);
            else
                _showSettings();
        };
        var three = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Step("3", "Read anywhere"),
                new TextBlock { Text = $"Select text in any app and press {hotkey}. With the browser extension, words light up on web pages too.", Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                add,
            },
        };

        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*,10,*") };
        cards.Children.Add(IntroCard(one));
        Place(cards, IntroCard(two), 2);
        Place(cards, IntroCard(three), 4);
        var close = new Button { Classes = { "ghost" }, Content = "Got it ✕", Padding = new Thickness(8, 2), MinHeight = 0, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) =>
        {
            S.IntroSeen = true;
            S.Save();
            BuildIntro();
        };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock { Text = "START HERE", Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center });
        Place(head, close, 1);
        _intro.Child = new StackPanel { Children = { head, cards } };
    }

    private static Control Step(string n, string title) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children =
        {
            new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brush("#19F58C"), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = n, Foreground = WordFg, FontWeight = FontWeight.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            },
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 150 },
        },
    };

    private static Border IntroCard(Control c) => new() { Classes = { "card" }, Padding = new Thickness(12), Child = c };

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    // Drawn, not font glyphs: ⏮ ⏭ are missing from many fonts.
    private const string PrevIcon = "M2,1 H4 V15 H2 Z M15,1 V15 L5,8 Z";
    private const string NextIcon = "M12,1 H14 V15 H12 Z M1,1 V15 L11,8 Z";
    private const string StopIcon = "M2,2 H14 V14 H2 Z";

    private static Button Transport(string icon, string tip)
    {
        var b = new Button { Width = 44, Height = 40, MinHeight = 40, Padding = new Thickness(0), CornerRadius = new CornerRadius(20) };
        b.Content = new PathIcon { Data = Geometry.Parse(icon), Width = 13, Height = 13 };
        ToolTip.SetTip(b, tip);
        return b;
    }

    private static string Plural(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    private static void Place(Grid g, Control c, int column)
    {
        Grid.SetColumn(c, column);
        g.Children.Add(c);
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ }
    }
}
