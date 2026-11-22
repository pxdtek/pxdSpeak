using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using pxdSpeak.Services;
using pxdSpeak.Speech;
using pxdSpeak.Views;
using pxdSpeak.Platform;

namespace pxdSpeak;

/// <summary>
/// pxdSpeak.exe --self-test DIR: draws the window headless (no OS window at all) to DIR\voices.png and settings.png,
/// writes the .ico, checks the text clean-up, and writes a short WAV with every downloaded voice family. Report in
/// DIR\report.txt. Nothing is played and nothing appears on screen.
/// </summary>
public static class SelfTest
{
    public static string? Dir { get; set; }

    private static int _fail;

    public static void Run(string dir)
    {
        var r = new StringBuilder();
        try
        {
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("PXD_SHARED_DIR", Path.Combine(dir, "shared"));
            pxdStandard.pxdChrome.Configure(TrayLink.AppId, Path.Combine(dir, "placement"));
            r.AppendLine($"pxd Speak {pxdStandard.pxdUpdate.ThisVersion()} self-test {DateTime.Now:yyyy-MM-dd HH:mm}");
            r.AppendLine("Voices folder: " + VoiceStore.Folder);

            Text(r);
            Catalog(r);
            Window(r);
            BrandIcon.Write(Path.Combine(dir, "pxdSpeak.ico"));
            BrandIcon.Write(Path.Combine(dir, "icon.png"));
            Directory.CreateDirectory(Path.Combine(dir, "ext-icons"));
            foreach (var size in new[] { 16, 32, 48, 128 })
                using (var f = File.Create(Path.Combine(dir, "ext-icons", size + ".png")))
                    BrandIcon.Render(size).Save(f);
            Speech(r, dir);
            Timing(r);
            Bridge(r);
        }
        catch (Exception ex)
        {
            r.AppendLine("CRASH: " + ex);
            _fail++;
        }

        r.AppendLine(_fail == 0 ? "ALL PASSED" : $"{_fail} FAILED");
        File.WriteAllText(Path.Combine(dir, "report.txt"), r.ToString());
    }

    private static void Check(StringBuilder r, string what, bool ok, string detail = "")
    {
        if (!ok)
            _fail++;
        r.AppendLine($"{(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    private static void Text(StringBuilder r)
    {
        var a = TextPrep.Clean("**Hello** there\nworld, see https://www.example.com/page?x=1 now");
        Check(r, "clean: markdown + wrapped line + link", a == "Hello there world, see link to example.com now.", a);
        var b = TextPrep.Clean("Shopping list\n- Eggs\n- Milk\n\nDone");
        Check(r, "clean: list items get pauses", b == "Shopping list. Eggs. Milk. Done.", b);
        Check(r, "clean: empty", TextPrep.Clean("  \n ") == "");
        var long1 = string.Join(" ", Enumerable.Repeat("This is a sentence.", 100));
        var pieces = TextPrep.Pieces(long1, 400).ToList();
        Check(r, "pieces: split under 400 chars at sentence ends", pieces.All(p => p.Length <= 400 && p.EndsWith('.')) && string.Join(" ", pieces) == long1, $"{pieces.Count} pieces");
    }

    private static void Catalog(StringBuilder r)
    {
        Check(r, "catalog: 28 English Kokoro voices", VoiceCatalog.KokoroVoices.Count == 28);
        Check(r, "catalog: Kokoro af_heart is speaker 3", VoiceCatalog.Find("kokoro:af_heart")?.Speaker == 3);
        Check(r, "catalog: Kokoro bm_lewis is speaker 27", VoiceCatalog.Find("kokoro:bm_lewis")?.Speaker == 27);
        Check(r, "catalog: Piper voices all have packs", VoiceCatalog.Piper.All(v => VoiceCatalog.PackOf(v) is not null), $"{VoiceCatalog.Piper.Count} voices");
        Check(r, "catalog: ids unique", VoiceCatalog.KokoroVoices.Concat(VoiceCatalog.Piper).Select(v => v.Id).Distinct().Count() == VoiceCatalog.KokoroVoices.Count + VoiceCatalog.Piper.Count);
        var win = WindowsVoices.All();
        r.AppendLine($"     Windows voices: {win.Count} ({string.Join(", ", win.Select(v => v.Name))})");
    }

    private static void Window(StringBuilder r)
    {
        var settings = new AppSettings { ReadOnly = true };
        using var sp = new Speaker(interactive: false, settings);
        var w = new MainWindow(sp)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-30000, -30000),
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        w.Show();
        foreach (var (tab, name) in new[] { (0, "voices"), (1, "settings") })
        {
            w.Tabs.SelectedIndex = tab;
            w.Width = 640;
            w.Height = 800;
            w.UpdateLayout();
            using var bmp = new RenderTargetBitmap(new PixelSize(640, 800));
            bmp.Render(w);
            bmp.Save(Path.Combine(Dir!, name + ".png"));
        }

        w.Tabs.SelectedIndex = 0;
        w.UpdateLayout();
        var scroller = w.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible).MaxBy(s => s.Extent.Height)!;
        foreach (var y in new[] { 1500, 2100, 2700 })
        {
            scroller.Offset = new Vector(0, y);
            w.UpdateLayout();
            using var shot = new RenderTargetBitmap(new PixelSize(640, 800));
            shot.Render(w);
            shot.Save(Path.Combine(Dir!, $"voices-{y}.png"));
        }

        var bar = w.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("pxd-caption"));
        Check(r, "window: built and drawn with the pxd title bar", bar >= 2, $"{bar} caption buttons");
        w.Close();
    }

    private static void Speech(StringBuilder r, string dir)
    {
        using var reader = new Reader();
        var tried = 0;
        foreach (var v in new[]
                 {
                     VoiceCatalog.Find("kokoro:af_heart"), VoiceCatalog.Find("kokoro:bm_george"),
                     VoiceCatalog.Find(VoiceCatalog.ChatterboxDefault), VoiceCatalog.Find("chatterbox:kokoro:bm_george"),
                     VoiceCatalog.Piper.FirstOrDefault(VoiceStore.IsInstalled), WindowsVoices.All().FirstOrDefault(),
                 })
        {
            if (v is null || !VoiceStore.IsInstalled(v))
                continue;
            tried++;
            var path = Path.Combine(dir, $"say-{v.Id.Split(':')[0]}-{v.Name.ToLowerInvariant()}.wav");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var length = reader.SaveWav("Hello! This is pxd Speak reading the text you selected.", v, 1f, path);
            Check(r, $"speech: {v.Family} {v.Name}", length.TotalSeconds > 1.5 && length.TotalSeconds < 15, $"{length.TotalSeconds:0.0} s audio in {sw.Elapsed.TotalSeconds:0.0} s");
        }

        if (tried == 0)
            r.AppendLine("     (no voices available to test speech)");
        Chatterbox.Stop();
    }

    // ---- word tracking + browser bridge ------------------------------------------------------------------------------

    private static readonly string[] SampleWords =
        "The quick brown fox jumps over the lazy dog. Then, after a long nap in the warm sun, it wandered home for dinner.".Split(' ');

    /// <summary>The estimator against a Windows voice's own word times on the same audio: how far off is the guess?</summary>
    private static void Timing(StringBuilder r)
    {
        var sentences = Tracking.Sentences(new TrackedText { Words = SampleWords, BlockEnds = SampleWords.Select(_ => false).ToList() });
        Check(r, "tracking: two sentences found", sentences.Count == 2 && sentences[0] == (0, 8), string.Join(" ", sentences));
        var mr = Tracking.Sentences(new TrackedText { Words = "Mr. Smith met Dr. Jones at 5 p.m. today. Fine.".Split(' '), BlockEnds = Enumerable.Repeat(false, 10).ToList() });
        Check(r, "tracking: Mr./Dr. don't end sentences", mr[0].First == 0 && mr[0].Last >= 4, string.Join(" ", mr));

        if (WindowsVoices.All().FirstOrDefault() is not { } wv)
            return;
        using var synth = new WindowsSynth(wv);
        var words = SampleWords.Take(9).ToList();
        var (samples, rate, cues) = synth.SpeakTimed(string.Join(" ", words), 1f, CancellationToken.None);
        Check(r, "windows: word cues reported", cues.Count >= words.Count - 1, $"{cues.Count} cues for {words.Count} words");
        if (cues.Count < words.Count - 1)
            return;
        var exact = cues.Take(words.Count).Select(c => c.Seconds).ToArray();
        var guess = Tracking.Estimate(samples, rate, words);
        var err = exact.Zip(guess, (a, b) => Math.Abs(a - b)).ToArray();
        Check(r, "estimate vs exact word starts within 0.25 s on average", err.Average() < 0.25,
            $"mean {err.Average() * 1000:0} ms, worst {err.Max() * 1000:0} ms");
    }

    /// <summary>A pretend extension talks to the bridge: refused from a web page, accepted from an extension, words come back in order.</summary>
    private static void Bridge(StringBuilder r)
    {
        Environment.SetEnvironmentVariable("PXDSPEAK_BRIDGE_PORT", "47399");
        using var bridge = new Platform.BrowserBridge();
        bridge.Start();
        Check(r, "bridge: listening on 127.0.0.1", bridge.Listening, bridge.Error ?? "");
        var url = new Uri($"ws://127.0.0.1:{Platform.BrowserBridge.Port}/pxdspeak");

        using (var evil = new System.Net.WebSockets.ClientWebSocket())
        {
            evil.Options.SetRequestHeader("Origin", "https://example.com");
            var refused = false;
            try { evil.ConnectAsync(url, CancellationToken.None).GetAwaiter().GetResult(); }
            catch { refused = true; }
            Check(r, "bridge: web pages are refused", refused);
        }

        using var ws = new System.Net.WebSockets.ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "chrome-extension://pxdspeaktest");
        ws.ConnectAsync(url, CancellationToken.None).GetAwaiter().GetResult();
        void Send(object o) => ws.SendAsync(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(o), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
        Send(new { type = "hello", browser = "TestBrowser", version = "1.0" });
        var events = new System.Collections.Concurrent.ConcurrentQueue<(double T, string Json)>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _ = Task.Run(async () =>
        {
            var buf = new byte[65536];
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                try
                {
                    var res = await ws.ReceiveAsync(buf, CancellationToken.None);
                    var json = Encoding.UTF8.GetString(buf, 0, res.Count);
                    events.Enqueue((clock.Elapsed.TotalSeconds, json));
                    if (json.Contains("\"grab\""))
                    {
                        var id = System.Text.Json.Nodes.JsonNode.Parse(json)!["grabId"]!.GetValue<int>();
                        Send(new { type = "grabbed", grabId = id, words = SampleWords, blocks = new[] { 8 }, start = 0 });
                    }
                }
                catch
                {
                    return;
                }
            }
        });
        for (var i = 0; i < 40 && bridge.Count == 0; i++)
            Thread.Sleep(50);
        Thread.Sleep(200);
        Check(r, "bridge: extension linked", bridge.Count == 1 && bridge.Browsers.FirstOrDefault() == "TestBrowser", string.Join(",", bridge.Browsers));

        // The right-click menu path: the page sends "read" on its own.
        BrowserRead? asked = null;
        bridge.ReadRequested += rr => asked = rr;
        Send(new { type = "read", words = SampleWords, blocks = new[] { 8 }, start = 4 });
        for (var i = 0; i < 40 && asked is null; i++)
            Thread.Sleep(25);
        Check(r, "bridge: a read from the page menu arrives", asked?.Words.Count == SampleWords.Length && asked.Start == 4);

        var grabbed = bridge.Grab(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Check(r, "bridge: grab returns the page's words", grabbed?.Words.Count == SampleWords.Length && grabbed.BlockEnds.Contains(8));
        if (grabbed is null || VoiceCatalog.Find("kokoro:af_heart") is not { } voice || !VoiceStore.IsInstalled(voice))
            return;

        // Read them silently (volume 0) with word reports going back over the link, as the app does.
        using var reader = new Reader { Volume = 0f };
        var done = new ManualResetEventSlim();
        reader.StateChanged += s =>
        {
            if (s == ReaderState.Idle && reader.State == ReaderState.Idle)
                done.Set();
        };
        clock.Restart();
        reader.SpeakTracked(new TrackedText
        {
            Words = grabbed.Words,
            BlockEnds = Enumerable.Range(0, grabbed.Words.Count).Select(grabbed.BlockEnds.Contains).ToList(),
            OnWord = (i, f, l) => _ = grabbed.From.Send(new { type = "word", id = 1, i, f, l }),
        }, voice, 1f);
        Thread.Sleep(500);
        done.Wait(TimeSpan.FromSeconds(60));
        Thread.Sleep(300);
        var words = events.Where(e => e.Json.Contains("\"word\""))
            .Select(e => (e.T, I: System.Text.Json.Nodes.JsonNode.Parse(e.Json)!["i"]!.GetValue<int>())).ToList();
        Check(r, "bridge: every word reported once, in order", words.Select(w => w.I).SequenceEqual(Enumerable.Range(0, SampleWords.Length)),
            $"{words.Count} of {SampleWords.Length}: {string.Join(",", words.Select(w => w.I))}");
        if (words.Count > 2)
            r.AppendLine($"     first word at {words[0].T:0.00} s, last at {words[^1].T:0.00} s after the read began");
        ws.Abort();
    }
}
