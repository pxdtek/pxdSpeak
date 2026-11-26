using System.IO.Pipes;
using Avalonia;
using Avalonia.Headless;
using pxdSpeak.Services;
using pxdSpeak.Speech;

namespace pxdSpeak;

internal static class Program
{
    public const string PipeName = "pxdSpeak.commands";
    private static Mutex? _instance;

    public const string UpdateRepo = "pxdtek/pxdSpeak";
    public const string UpdateAsset = @"^pxdSpeak-.*-win-x64\.zip$";

    public static bool StartMinimized { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Offline tools first: no window, no single-instance check.
        //   --wav "text" <voice id> <out.wav>     write speech to a file
        //   --download <pack>                      download a voice pack
        //   --voices                               list voices and whether they're downloaded
        if (Cli.Run(args) is { } code)
            return code;

        // The Store updates the Store build itself; apps from the Store may not update themselves.
        if (!Commands.HasAction(args) && !Platform.Packaged.IsPackaged && pxdStandard.pxdUpdate.BeforeStart(UpdateRepo, UpdateAsset, args))
            return 0;

        pxdStandard.pxdChrome.Configure(TrayLink.AppId, AppSettings.Folder);
        StartMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        var test = Array.FindIndex(args, a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase));
        if (test >= 0 && test + 1 < args.Length)
        {
            SelfTest.Dir = args[test + 1];
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args);
            return 0;
        }

        _instance = new Mutex(true, @"Local\pxdSpeak.instance", out var first);
        if (!first)
            return Forward(args.Length > 0 ? args : ["--show"]) ? 0 : 1;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
            .LogToTrace();

    private static bool Forward(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe);
            writer.Write(string.Join("\n", args));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Commands for a running copy (shortcuts, Stream Deck, scripts): --read-selection, --read "text", --stop, --pause, --show.
/// </summary>
public static class Commands
{
    private static readonly string[] Actions = ["--read-selection", "--read", "--stop", "--pause"];

    public static bool HasAction(string[] args) => args.Any(a => Actions.Contains(a, StringComparer.OrdinalIgnoreCase));

    public static void Run(Speaker sp, string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--read-selection": sp.ReadSelection(); break;
                case "--read" when i + 1 < args.Length: sp.Read(args[++i]); break;
                case "--stop": sp.Reader.Stop(); break;
                case "--pause": sp.Reader.TogglePause(); break;
            }
        }
    }
}

internal static class Cli
{
    public static int? Run(string[] args)
    {
        if (args.Length == 0)
            return null;
        switch (args[0].ToLowerInvariant())
        {
            case "--install-chatterbox":
            {
                Chatterbox.Changed += () => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {Chatterbox.State}: {Chatterbox.Status}");
                Chatterbox.Install(CancellationToken.None).GetAwaiter().GetResult();
                Chatterbox.Stop();
                return Chatterbox.IsInstalled ? 0 : 1;
            }
            case "--voices":
            {
                foreach (var v in VoiceCatalog.ChatterboxVoices().Concat(VoiceCatalog.KokoroVoices).Concat(VoiceCatalog.Piper).Concat(WindowsVoices.All()))
                    Console.WriteLine($"{(VoiceStore.IsInstalled(v) ? "*" : " ")} {v.Id,-48} {v.Name} ({v.Tags})");
                return 0;
            }
            case "--download" when args.Length >= 2:
            {
                var pack = VoiceCatalog.PiperPacks.Append(VoiceCatalog.Kokoro).FirstOrDefault(p => p.Name == args[1]);
                if (pack is null)
                {
                    Console.Error.WriteLine("Unknown pack " + args[1]);
                    return 2;
                }

                VoiceStore.Download(pack, null, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine("Installed " + pack.Name + " in " + VoiceStore.PackDir(pack.Name));
                return 0;
            }
            case "--wav" when args.Length >= 4:
            {
                var voice = VoiceCatalog.Find(args[2]);
                if (voice is null)
                {
                    Console.Error.WriteLine("Unknown voice " + args[2]);
                    return 2;
                }

                using var reader = new Reader();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (voice.Family == VoiceFamily.Chatterbox) { Chatterbox.EnsureRunning(CancellationToken.None).GetAwaiter().GetResult(); Console.WriteLine($"server start {sw.Elapsed.TotalSeconds:0.0} s"); sw.Restart(); }
                else if (voice.Family != VoiceFamily.Windows) { reader.Preload(voice); reader.SaveWav("Hi.", voice, 1f, args[3]); Console.WriteLine($"load + warm-up {sw.Elapsed.TotalSeconds:0.0} s"); sw.Restart(); }
                var length = reader.SaveWav(args[1], voice, args.Length >= 5 ? float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 1f, args[3]);
                Console.WriteLine($"{voice.Name}: {length.TotalSeconds:0.0} s of speech in {sw.Elapsed.TotalSeconds:0.0} s -> {args[3]}");
                Chatterbox.Stop();
                return 0;
            }
            default:
                return null;
        }
    }
}
