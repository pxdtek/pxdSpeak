using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using pxdSpeak.Services;
using pxdSpeak.Views;

namespace pxdSpeak;

/// <summary>
/// pxdSpeak.exe --store-assets DIR: everything the stores need as pictures, drawn headless (nothing on screen).
///   DIR\msix\        MSIX logos (Square44x44, Square150x150, Wide310x150, StoreLogo; scale-100/200, target sizes)
///   DIR\screens\     Microsoft Store screenshots, 1366×768 (the window on a backdrop, with a headline); window\ the bare window
///   DIR\extension\   Chrome Web Store / Edge Add-ons: 128 px icon, 300 px logo, 440×280 small tile, 1400×560 marquee
/// The screenshots use the Store look (no Chatterbox) and a pretend downloaded Kokoro, in a temporary data folder.
/// </summary>
public static class StoreAssets
{
    public static string? Dir { get; set; }

    /// <summary>Called before anything reads settings or voices, so the fake data folder is used.</summary>
    public static void Prepare()
    {
        var data = Path.Combine(Path.GetTempPath(), "pxdSpeak-store-assets");
        Directory.CreateDirectory(Path.Combine(data, "Voices", "espeak-ng-data"));
        Directory.CreateDirectory(Path.Combine(data, "Voices", Speech.VoiceCatalog.Kokoro.Name));
        File.WriteAllText(Path.Combine(data, "Voices", Speech.VoiceCatalog.Kokoro.Name, ".installed"), "store assets");
        Environment.SetEnvironmentVariable("PXDSPEAK_DATA_DIR", data);
        Environment.SetEnvironmentVariable("PXDSPEAK_FAKE_PACKAGED", "1");
        Environment.SetEnvironmentVariable("PXD_SHARED_DIR", Path.Combine(data, "shared"));
    }

    public static void Run(string dir)
    {
        pxdStandard.pxdChrome.Configure(TrayLink.AppId, Path.Combine(Path.GetTempPath(), "pxdSpeak-store-assets", "placement"));
        Logos(Path.Combine(dir, "msix"));
        Extension(Path.Combine(dir, "extension"));
        Screens(Path.Combine(dir, "screens"));
        File.WriteAllText(Path.Combine(dir, "done.txt"), "ok " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
    }

    // ── MSIX ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void Logos(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var scale in new[] { 100, 200 })
        {
            var k = scale / 100.0;
            Save(Icon((int)(44 * k), 0.92), dir, $"Square44x44Logo.scale-{scale}.png");
            Save(Icon((int)(150 * k), 0.62), dir, $"Square150x150Logo.scale-{scale}.png");
            Save(Tile((int)(310 * k), (int)(150 * k), wide: true), dir, $"Wide310x150Logo.scale-{scale}.png");
            Save(Icon((int)(50 * k), 1.0), dir, $"StoreLogo.scale-{scale}.png");
        }

        // Taskbar, Start list and Explorer use these exact sizes (unplated: no tile colour behind).
        foreach (var t in new[] { 16, 24, 32, 48, 256 })
        {
            Save(Icon(t, 1.0), dir, $"Square44x44Logo.targetsize-{t}.png");
            Save(Icon(t, 1.0), dir, $"Square44x44Logo.targetsize-{t}_altform-unplated.png");
        }
    }

    /// <summary>The app icon, <paramref name="fill"/> of a transparent square.</summary>
    private static Control Icon(int size, double fill)
    {
        var inner = Math.Max(8, (int)Math.Round(size * fill));
        return new Grid
        {
            Width = size, Height = size, Background = Brushes.Transparent,
            Children = { new Border { Width = inner, Height = inner, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = BrandIcon.Build(inner) } },
        };
    }

    private static Control Tile(int w, int h, bool wide)
    {
        var icon = (int)(h * 0.5);
        return new Grid
        {
            Width = w, Height = h, Background = Brushes.Transparent,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = h * 0.08, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        BrandIcon.Build(icon),
                        new TextBlock { Text = "pxd Speak", FontSize = h * 0.17, FontWeight = FontWeight.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, IsVisible = wide },
                    },
                },
            },
        };
    }

    // ── browser stores ────────────────────────────────────────────────────────────────────────────────────────────────

    private static void Extension(string dir)
    {
        Directory.CreateDirectory(dir);
        // Chrome: 128×128 with the art in the middle 96×96 (it adds its own padding look).
        Save(Icon(128, 0.75), dir, "icon-128.png");
        Save(Icon(300, 0.9), dir, "logo-300.png");
        Save(Promo(440, 280), dir, "promo-small-440x280.png");
        Save(Promo(1400, 560), dir, "promo-marquee-1400x560.png");
    }

    private static Control Promo(int w, int h)
    {
        var k = h / 280.0;
        var words = new WrapPanel { Margin = new Thickness(0, 10 * k, 0, 0) };
        var sample = "Every word lights up".Split(' ');
        for (var i = 0; i < sample.Length; i++)
            words.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse(i == 2 ? "#19F58C" : "#1C4250")),
                Padding = new Thickness(4 * k, 1 * k), Margin = new Thickness(0, 0, 0, 0),
                Child = new TextBlock { Text = sample[i] + " ", FontSize = 15 * k, Foreground = new SolidColorBrush(Color.Parse(i == 2 ? "#04140C" : "#E8EAF0")) },
            });
        return new Grid
        {
            Width = w, Height = h, Background = Backdrop(),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 22 * k, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        BrandIcon.Build(92 * k),
                        new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center, MaxWidth = w * 0.62,
                            Children =
                            {
                                new TextBlock { Text = "pxd Speak", FontSize = 34 * k, FontWeight = FontWeight.Bold, Foreground = Neon() },
                                new TextBlock { Text = "Hear any page read aloud", FontSize = 16 * k, Foreground = new SolidColorBrush(Color.Parse("#C9CFDB")), TextWrapping = TextWrapping.Wrap },
                                words,
                            },
                        },
                    },
                },
            },
        };
    }

    // ── Microsoft Store screenshots ───────────────────────────────────────────────────────────────────────────────────

    private const string Article =
        "How lighthouses kept ships safe\n\n" +
        "The old lighthouse stood at the edge of the cliff, its paint worn thin by a hundred winters. Every evening the keeper " +
        "climbed the spiral stairs, lit the lamp, and watched the ships slip past in the dark.\n\n" +
        "Some nights were calm. Others roared with wind and rain, but the light never failed. Sailors far out at sea counted its " +
        "flashes to know exactly which coast they were passing, and steered clear of the rocks below.\n\n" +
        "Today most lights run on their own, yet many keepers' cottages still stand, now small museums full of logbooks and lamps.";

    private static void Screens(string dir)
    {
        Directory.CreateDirectory(dir);
        var settings = new AppSettings { ReadOnly = true, Voice = "kokoro:af_heart" };
        using var sp = new Speaker(interactive: false, settings);
        var w = new MainWindow(sp)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-30000, -30000),
            ShowActivated = false,
            ShowInTaskbar = false,
            Width = 640, Height = 800,
        };
        w.Show();

        w.Reader.HideIntro();
        w.Reader.ShowForTest(Article, 33);
        Shot(w, dir, "1-reading.png", "Hear it. See every word.", "Paste anything and press play. Each word lights up as it's read, in natural voices that run on your PC.");
        w.Reader.StopReading();

        settings.IntroSeen = false;
        w.Reader.Text = "";
        w.Reader.ShowIntro();
        Shot(w, dir, "2-start.png", "Ready in a minute", "Pick a voice, paste some text, press play. No account, no subscription.");

        w.Tabs.SelectedIndex = 1;
        Shot(w, dir, "3-voices.png", "28 natural AI voices, free", "US and UK, female and male. They run offline: nothing you read leaves your PC.");

        w.Tabs.SelectedIndex = 2;
        Shot(w, dir, "4-anywhere.png", "Read anywhere with a hotkey", "Select text in any app and press Ctrl + Alt + R. With the browser extension, words light up on web pages too.");
        w.Close();
    }

    private static void Shot(Window w, string dir, string name, string headline, string sub)
    {
        w.UpdateLayout();
        var win = new RenderTargetBitmap(new PixelSize(640, 800));
        win.Render(w);
        // The bare window too (README screenshots).
        Directory.CreateDirectory(Path.Combine(dir, "window"));
        win.Save(Path.Combine(dir, "window", name));
        var scene = new Grid
        {
            Width = 1366, Height = 768, Background = Backdrop(),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        scene.Children.Add(new StackPanel
        {
            Margin = new Thickness(90, 0, 40, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 18,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { BrandIcon.Build(44), new TextBlock { Text = "pxd Speak", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center } } },
                new TextBlock { Text = headline, FontSize = 46, FontWeight = FontWeight.Bold, Foreground = Neon(), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = sub, FontSize = 21, Foreground = new SolidColorBrush(Color.Parse("#C9CFDB")), TextWrapping = TextWrapping.Wrap, LineHeight = 31 },
            },
        });
        var frame = new Border
        {
            Margin = new Thickness(0, 0, 90, 0), VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(10), ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 20 60 0 #AA000000"), BorderBrush = new SolidColorBrush(Color.Parse("#2B3242")), BorderThickness = new Thickness(1),
            Child = new Image { Source = win, Width = 560, Height = 700 },
        };
        Grid.SetColumn(frame, 1);
        scene.Children.Add(frame);
        Save(scene, dir, name);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static IBrush Backdrop() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#0D1117"), 0), new GradientStop(Color.Parse("#0B1A22"), 0.6), new GradientStop(Color.Parse("#0A2A22"), 1) },
    };

    private static IBrush Neon() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#35D8FF"), 0), new GradientStop(Color.Parse("#19F58C"), 1) },
    };

    private static void Save(Control c, string dir, string name)
    {
        var size = new Size(c.Width, c.Height);
        c.Measure(size);
        c.Arrange(new Rect(size));
        using var bmp = new RenderTargetBitmap(new PixelSize((int)c.Width, (int)c.Height), new Vector(96, 96));
        bmp.Render(c);
        bmp.Save(Path.Combine(dir, name));
    }
}
