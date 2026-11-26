using System.IO.Pipes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using pxdSpeak.Platform;
using pxdSpeak.Services;
using pxdSpeak.Speech;
using pxdSpeak.Views;
using pxdStandard;

namespace pxdSpeak;

public partial class App : Application
{
    private Speaker _sp = null!;
    private MainWindow? _window;
    private TrayIcon _tray = null!;
    private IClassicDesktopStyleApplicationLifetime _lifetime = null!;
    private WindowIcon? _icon, _pausedIcon;
    private TrayLink _trayLink = null!;
    private readonly CancellationTokenSource _quit = new();
    private Extensions.ProHost _pro = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Styles.Add(pxdChrome.Styles());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            _lifetime = lifetime;
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (SelfTest.Dir is { } dir)
            {
                SelfTest.Run(dir);
                Dispatcher.UIThread.Post(() => lifetime.Shutdown());
                base.OnFrameworkInitializationCompleted();
                return;
            }

            Downloads.Init(a => Dispatcher.UIThread.Post(a));
            _sp = new Speaker(interactive: true);
            _sp.Notice += Banner.Show;
            _sp.StateChanged += _ => UpdateTray();
            _sp.Start(a => Dispatcher.UIThread.Post(a));
            Downloads.Finished += _ => UpdateTray();
            Chatterbox.Changed += () => Dispatcher.UIThread.Post(UpdateTray);
            Downloads.Failed += (pack, msg) => Banner.Show(new Notice("DOWNLOAD FAILED", $"{pack}: {msg}", Warning: true));

            _icon = BrandIcon.Window(64);
            _pausedIcon = BrandIcon.Window(64, paused: true);
            _tray = new TrayIcon { Icon = _icon, ToolTipText = "pxd Speak", IsVisible = pxdLink.TrayIconVisible(TrayLink.AppId) };
            _tray.Clicked += (_, _) => OpenWindow();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
            _trayLink = new TrayLink(v => _tray.IsVisible = v, a => Dispatcher.UIThread.Post(a));
            UpdateTray();

            _ = Task.Run(ListenForCommands);

            // Store build only: the Pro add-on (pxdSpeak.Pro.dll) if it ships next to the exe.
            _pro = new Extensions.ProHost(_sp);
            Extensions.ProLoader.Load(_pro);

            // First start (nothing downloaded and no hotkey trouble aside): open the window so voices can be picked.
            var inTray = Program.StartMinimized || _sp.Settings.StartInTray;
            if (!inTray || _sp.HotkeyConflicts.Count > 0 && !_sp.Settings.StartInTray)
                OpenWindow();
            else
            {
                if (_sp.HotkeyConflicts.Count > 0)
                    Banner.Show(new Notice("HOTKEY BLOCKED", string.Join("\n", _sp.HotkeyConflicts), Warning: true));
                DispatcherTimer.RunOnce(MemoryTrim.Trim, TimeSpan.FromSeconds(3));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void UpdateTray()
    {
        var menu = new NativeMenu();
        var state = _sp.Reader.State;
        var read = new NativeMenuItem("Read selection" + (_sp.Settings.ReadHotkey is { } k ? $"\t{k}" : ""));
        read.Click += (_, _) => _sp.ReadSelection();
        menu.Add(read);
        var clip = new NativeMenuItem("Read clipboard");
        clip.Click += (_, _) => _sp.Read(Selection.ReadText() ?? "");
        menu.Add(clip);
        var reader = new NativeMenuItem("Open reader with clipboard");
        reader.Click += (_, _) => OpenReader(Selection.ReadText() ?? "");
        menu.Add(reader);
        if (state != ReaderState.Idle)
        {
            var pause = new NativeMenuItem(state == ReaderState.Paused ? "Resume" : "Pause");
            pause.Click += (_, _) => _sp.Reader.TogglePause();
            menu.Add(pause);
            var stop = new NativeMenuItem("Stop");
            stop.Click += (_, _) => _sp.Reader.Stop();
            menu.Add(stop);
        }

        menu.Add(new NativeMenuItemSeparator());
        var current = _sp.CurrentVoice;
        var voices = new NativeMenu();
        foreach (var v in VoiceCatalog.ChatterboxVoices().Concat(VoiceCatalog.KokoroVoices).Concat(VoiceCatalog.Piper).Concat(WindowsVoices.All()).Where(VoiceStore.IsInstalled))
        {
            var voice = v;
            var item = new NativeMenuItem($"{v.Name}  ({v.Family}, {v.Tags})") { ToggleType = MenuItemToggleType.Radio, IsChecked = v.Id == current.Id };
            item.Click += (_, _) =>
            {
                _sp.SetVoice(voice);
                UpdateTray();
                _window?.Close();   // rebuilt fresh next time, showing the new pick
            };
            voices.Add(item);
        }

        menu.Add(new NativeMenuItem("Voice: " + current.Name) { Menu = voices });

        var speeds = new NativeMenu();
        foreach (var s in new[] { 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0 })
        {
            var speed = s;
            var item = new NativeMenuItem($"{s:0.##}×") { ToggleType = MenuItemToggleType.Radio, IsChecked = Math.Abs(_sp.Settings.Speed - s) < 0.001 };
            item.Click += (_, _) =>
            {
                _sp.Settings.Speed = speed;
                _sp.Settings.Save();
                UpdateTray();
            };
            speeds.Add(item);
        }

        menu.Add(new NativeMenuItem($"Speed: {_sp.Settings.Speed:0.##}×") { Menu = speeds });
        menu.Add(new NativeMenuItemSeparator());
        var open = new NativeMenuItem("Open pxd Speak…");
        open.Click += (_, _) => OpenWindow();
        menu.Add(open);
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        menu.Add(quit);
        _tray.Menu = menu;
        _tray.Icon = state == ReaderState.Paused ? _pausedIcon : _icon;
        _tray.ToolTipText = state switch
        {
            ReaderState.Speaking => $"pxd Speak · reading ({current.Name})",
            ReaderState.Paused => "pxd Speak · paused",
            ReaderState.Loading => "pxd Speak · getting ready…",
            _ => $"pxd Speak · {current.Name}" + (_sp.Settings.ReadHotkey is { } h ? $" · {h}" : ""),
        };
    }

    private async Task ListenForCommands()
    {
        while (!_quit.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_quit.Token);
                using var reader = new StreamReader(pipe);
                var args = (await reader.ReadToEndAsync(_quit.Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Dispatcher.UIThread.Post(() =>
                {
                    if (Commands.HasAction(args))
                        Commands.Run(_sp, args);
                    else
                        OpenWindow();
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
    }

    private void OpenWindow()
    {
        if (_window is not null)
        {
            _window.Activate2();
            return;
        }

        _window = new MainWindow(_sp, _trayLink, Quit, _pro);
        _window.Closed += (_, _) =>
        {
            _window = null;
            UpdateTray();
            DispatcherTimer.RunOnce(MemoryTrim.Trim, TimeSpan.FromSeconds(1));
        };
        _window.Show();
        _window.Activate();
    }

    /// <summary>Opens the window on the Read tab with <paramref name="text"/> in it, reading it with each word lit up.</summary>
    private void OpenReader(string text)
    {
        OpenWindow();
        if (_window is null)
            return;
        _window.Tabs.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(text))
            _window.Reader.LoadAndRead(text);
    }

    private void Quit()
    {
        _quit.Cancel();
        Downloads.CancelAll();
        _window?.Close();
        Banner.Hide();
        Chatterbox.Stop();
        _trayLink.Dispose();
        _sp.Dispose();
        _tray.IsVisible = false;
        _tray.Dispose();
        _lifetime.Shutdown();
    }
}
