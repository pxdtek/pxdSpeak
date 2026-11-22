using pxdSpeak.Speech;

namespace pxdSpeak.Services;

/// <summary>Voice downloads in progress. Kept outside the window, so closing it doesn't stop a download.</summary>
public static class Downloads
{
    private static readonly Dictionary<string, (double Progress, CancellationTokenSource Cts)> Running = new();
    private static Action<Action> _post = a => a();

    /// <summary>Raised on the UI thread with the pack name whenever its progress or state changes.</summary>
    public static event Action<string>? Changed;

    /// <summary>Raised on the UI thread when a download fails (pack, message).</summary>
    public static event Action<string, string>? Failed;

    /// <summary>Raised on the UI thread when a pack has finished installing.</summary>
    public static event Action<string>? Finished;

    public static void Init(Action<Action> post) => _post = post;

    public static bool IsRunning(string pack) => Running.ContainsKey(pack);

    public static double Progress(string pack) => Running.TryGetValue(pack, out var r) ? r.Progress : 0;

    public static void Start(VoicePack pack)
    {
        if (Running.ContainsKey(pack.Name))
            return;
        var cts = new CancellationTokenSource();
        Running[pack.Name] = (0, cts);
        Changed?.Invoke(pack.Name);
        var progress = new Progress<double>(p =>
        {
            if (Running.ContainsKey(pack.Name))
            {
                Running[pack.Name] = (p, cts);
                Changed?.Invoke(pack.Name);
            }
        });
        _ = Task.Run(async () =>
        {
            string? error = null;
            try
            {
                await VoiceStore.Download(pack, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            _post(() =>
            {
                Running.Remove(pack.Name);
                if (error is not null)
                    Failed?.Invoke(pack.Name, error);
                else if (VoiceStore.IsInstalled(pack.Name))
                    Finished?.Invoke(pack.Name);
                Changed?.Invoke(pack.Name);
            });
        });
    }

    public static void Cancel(string pack)
    {
        if (Running.TryGetValue(pack, out var r))
            r.Cts.Cancel();
    }

    public static void CancelAll()
    {
        foreach (var r in Running.Values)
            r.Cts.Cancel();
    }
}
