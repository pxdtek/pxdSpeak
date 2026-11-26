using pxdStandard;

namespace pxdSpeak.Services;

/// <summary>
/// The pxd Toolkit link for the tray icon: %APPDATA%\pxd\toolkit.json says whether it shows. The file is watched, so
/// turning the icon off in pxd Toolkit (or in this app's settings, which writes the same file) hides it at once.
/// </summary>
public sealed class TrayLink : IDisposable
{
    public const string AppId = "pxdSpeak";

    private readonly Action<bool> _apply;
    private readonly Action<Action> _post;
    private readonly IDisposable _watch;
    private bool? _applied;

    /// <param name="apply">Shows or hides the icon; called through <paramref name="post"/> (the UI thread).</param>
    public TrayLink(Action<bool> apply, Action<Action> post)
    {
        _apply = apply;
        _post = post;
        Update();
        // The Store build's AppData is private package storage, so pxd Toolkit can't share the file; never fail over it.
        try { _watch = pxdLink.Watch(() => _post(Update)); }
        catch { _watch = new Nothing(); }
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }

    public bool Visible => _applied ?? true;

    /// <summary>Raised after the icon was shown or hidden, whichever side changed it.</summary>
    public event Action<bool>? Changed;

    /// <summary>The "Show tray icon" switch: writes toolkit.json (so pxd Toolkit agrees) and applies it now.</summary>
    public void Set(bool visible)
    {
        try { pxdLink.SetTrayIconVisible(AppId, visible); }
        catch { /* the switch just doesn't stick */ }
        if (_applied != visible)
        {
            _applied = visible;
            _apply(visible);
            Changed?.Invoke(visible);
        }
    }

    private void Update()
    {
        bool visible;
        try { visible = pxdLink.TrayIconVisible(AppId); }
        catch { visible = _applied ?? true; }
        if (visible == _applied)
            return;
        _applied = visible;
        _apply(visible);
        Changed?.Invoke(visible);
    }

    public void Dispose() => _watch.Dispose();
}
