using System.Reflection;
using Avalonia.Controls;
using pxdSpeak.Services;
using pxdSpeak.Speech;
using pxdSpeak.Views;

namespace pxdSpeak.Extensions;

/// <summary>
/// An add-on that brings extra features. The public app has none; the Microsoft Store build ships pxdSpeak.Pro.dll next
/// to the exe, which is found and attached at start-up. Add-ons only add things: everything in the public app works the
/// same with or without one.
/// </summary>
public interface IProModule
{
    /// <summary>Called once at start-up, on the UI thread.</summary>
    void Attach(ProHost host);
}

/// <summary>What an add-on can use: the reader, settings, voices, audio rendering, and each window as it opens.</summary>
public sealed class ProHost
{
    private readonly Speaker _sp;

    internal ProHost(Speaker sp) => _sp = sp;

    public Speaker Speaker => _sp;
    public AppSettings Settings => _sp.Settings;
    public Reader Reader => _sp.Reader;
    public VoiceInfo CurrentVoice => _sp.CurrentVoice;
    public float Speed => (float)_sp.Settings.Speed;

    /// <summary>True in the Microsoft Store (MSIX) build.</summary>
    public bool IsPackaged => Platform.Packaged.IsPackaged;

    /// <summary>The app's own version.</summary>
    public Version AppVersion => pxdStandard.pxdUpdate.ThisVersion();

    /// <summary>Audio for <paramref name="text"/> with the current (or given) voice and speed; progress 0…1; cancellable.</summary>
    public (float[] Samples, int Rate) Render(string text, Action<double>? progress, CancellationToken ct, VoiceInfo? voice = null, float? speed = null) =>
        _sp.Reader.Render(text, voice ?? CurrentVoice, speed ?? Speed, progress, ct);

    /// <summary>Raised on the UI thread each time the main window is built (it is rebuilt every time it is opened).</summary>
    public event Action<ProWindow>? WindowOpened;

    /// <summary>The window open now, if any.</summary>
    public ProWindow? Window { get; private set; }

    internal void Opened(MainWindow w)
    {
        var pw = new ProWindow(w);
        Window = pw;
        w.Closed += (_, _) =>
        {
            if (Window == pw)
                Window = null;
        };
        WindowOpened?.Invoke(pw);
    }
}

/// <summary>The parts of the main window an add-on may change.</summary>
public sealed class ProWindow
{
    private readonly MainWindow _w;

    internal ProWindow(MainWindow w) => _w = w;

    public Window Window => _w;

    /// <summary>Read, Voices, Settings (an add-on may add its own tab).</summary>
    public TabControl Tabs => _w.Tabs;

    /// <summary>The Read tab.</summary>
    public ReaderView Reader => _w.Reader;

    /// <summary>Puts text on the Read tab and reads it.</summary>
    public void ReadText(string text)
    {
        _w.Tabs.SelectedIndex = 0;
        _w.Reader.LoadAndRead(text);
    }

    /// <summary>Adds a button (or any control) to the Read tab's tool row.</summary>
    public void AddReaderTool(Control c) => _w.Reader.ProTools.Children.Add(c);

    /// <summary>Handles files dropped on the Read tab's text; return true if they were taken.</summary>
    public Func<IReadOnlyList<string>, bool>? FileDrop
    {
        get => _w.Reader.FileDrop;
        set => _w.Reader.FileDrop = value;
    }

    /// <summary>A card at the top of the Settings tab ("What's new / Pro").</summary>
    public void AddSettingsCard(Control c) => _w.AddSettingsCard(c);

    /// <summary>The window handle (for the Store purchase dialog).</summary>
    public IntPtr Hwnd => _w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
}

/// <summary>Finds pxdSpeak.Pro.dll next to the exe and attaches it. Never fails the app.</summary>
public static class ProLoader
{
    public const string FileName = "pxdSpeak.Pro.dll";

    public static List<IProModule> Loaded { get; } = new();

    public static string? Error { get; private set; }

    public static void Load(ProHost host, string? dir = null)
    {
        var path = Path.Combine(dir ?? AppContext.BaseDirectory, FileName);
        if (!File.Exists(path))
            return;
        try
        {
            var asm = Assembly.LoadFrom(path);
            foreach (var type in asm.GetExportedTypes().Where(t => typeof(IProModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }))
            {
                var module = (IProModule)Activator.CreateInstance(type)!;
                module.Attach(host);
                Loaded.Add(module);
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}
