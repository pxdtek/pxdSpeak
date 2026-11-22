using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;

using System.Reflection;
using System.Security.Cryptography;

namespace pxdSpeak.Speech;

public enum ServerState { NotInstalled, Installing, Stopped, Starting, Ready, Failed }

/// <summary>
/// Chatterbox Turbo (Resemble AI, MIT): the most human-sounding voice here, run on the graphics card by a small
/// Python server (Chatterbox\server.py, embedded) that pxd Speak sets up and starts itself:
/// <list type="bullet">
/// <item>uv (Astral) makes a private Python 3.11 + PyTorch (CUDA 12.4) in %LOCALAPPDATA%\pxdSpeak\Chatterbox; nothing is
/// installed system-wide.</item>
/// <item>The model (from Hugging Face, ResembleAI/chatterbox-turbo) is downloaded on the first start.</item>
/// <item>The server listens on 127.0.0.1 only and quits with pxd Speak; it is also stopped after a while unused so the
/// graphics card's memory is free for games.</item>
/// </list>
/// </summary>
public static class Chatterbox
{
    public const string UvVersion = "0.13.9";
    public const string Package = "chatterbox-tts==0.1.7";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly object Gate = new();
    private static Process? _server;
    private static int _port;
    private static TaskCompletionSource<bool>? _ready;
    private static DateTime _lastUse = DateTime.UtcNow;
    private static Timer? _idleTimer;

    public static ServerState State { get; private set; }
    public static string Status { get; private set; } = "";
    public static int SampleRate { get; private set; } = 24000;
    public static string Device { get; private set; } = "";

    /// <summary>Raised (on a worker thread) when <see cref="State"/> or <see cref="Status"/> changes.</summary>
    public static event Action? Changed;

    /// <summary>Minutes unused before the server is stopped to free the graphics card; 0 = never.</summary>
    public static int IdleMinutes { get; set; } = 30;

    public static string Folder => Path.Combine(Path.GetDirectoryName(VoiceStore.Folder)!, "Chatterbox");
    public static string ClipsFolder => Path.Combine(Folder, "voices");
    private static string EnvPython => Path.Combine(Folder, "env", "Scripts", "python.exe");
    private static string Marker => Path.Combine(Folder, ".installed");
    private static string ServerScript => Path.Combine(Folder, "server.py");
    public static string LogFile => Path.Combine(Folder, "server.log");

    public static bool IsInstalled => File.Exists(Marker) && File.Exists(EnvPython);

    static Chatterbox()
    {
        State = IsInstalled ? ServerState.Stopped : ServerState.NotInstalled;
    }

    private static void Set(ServerState s, string status)
    {
        State = s;
        Status = status;
        Changed?.Invoke();
    }

    // ---- install -----------------------------------------------------------------------------------------------------

    private static CancellationTokenSource? _install;

    /// <summary>Starts <see cref="Install"/> in the background (survives the window closing).</summary>
    public static void StartInstall()
    {
        if (State == ServerState.Installing)
            return;
        _install = new CancellationTokenSource();
        var ct = _install.Token;
        _ = Task.Run(() => Install(ct));
    }

    public static void CancelInstall() => _install?.Cancel();

    /// <summary>Sets everything up (about 5 GB): uv, Python, PyTorch, Chatterbox, then the model on the first start.</summary>
    public static async Task Install(CancellationToken ct)
    {
        if (State == ServerState.Installing)
            return;
        Set(ServerState.Installing, "Getting ready…");
        try
        {
            Directory.CreateDirectory(Folder);
            var uv = await FindOrGetUv(ct).ConfigureAwait(false);
            WriteResource("requirements.txt", Path.Combine(Folder, "requirements.txt"));
            WriteResource("server.py", ServerScript);

            Set(ServerState.Installing, "Getting Python 3.11…");
            await Run(uv, ["venv", "--allow-existing", "--python", "3.11", Path.Combine(Folder, "env")], ct).ConfigureAwait(false);
            Set(ServerState.Installing, "Downloading PyTorch and Chatterbox (about 3 GB)…");
            await Run(uv, ["pip", "install", "--python", EnvPython, "--index-strategy", "unsafe-best-match", "-r", Path.Combine(Folder, "requirements.txt")], ct).ConfigureAwait(false);
            await Run(uv, ["pip", "install", "--python", EnvPython, "--no-deps", Package], ct).ConfigureAwait(false);
            File.WriteAllText(Marker, DateTime.UtcNow.ToString("O"));

            Set(ServerState.Stopped, "Installed. Downloading the voice model on first start…");
            await EnsureRunning(ct, firstStart: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Set(IsInstalled ? ServerState.Stopped : ServerState.NotInstalled, "Cancelled");
        }
        catch (Exception ex)
        {
            Set(IsInstalled ? ServerState.Failed : ServerState.NotInstalled, "Install failed: " + ex.Message);
        }
    }

    /// <summary>uv from winget/PATH if there is one; otherwise the official release, checked against its SHA-256.</summary>
    private static async Task<string> FindOrGetUv(CancellationToken ct)
    {
        var own = Path.Combine(Folder, "uv", "uv.exe");
        if (File.Exists(own))
            return own;
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            var found = Directory.EnumerateDirectories(winget, "astral-sh.uv_*").Select(d => Path.Combine(d, "uv.exe")).FirstOrDefault(File.Exists);
            if (found is not null)
                return found;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir.Trim(), "uv.exe");
            if (File.Exists(p))
                return p;
        }

        Set(ServerState.Installing, "Downloading uv…");
        var url = $"https://github.com/astral-sh/uv/releases/download/{UvVersion}/uv-x86_64-pc-windows-msvc.zip";
        var zip = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        var sumText = await Http.GetStringAsync(url + ".sha256", ct).ConfigureAwait(false);
        var expected = sumText.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)[0];
        var actual = Convert.ToHexString(SHA256.HashData(zip));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("uv download didn't match its checksum");
        var dest = Path.Combine(Folder, "uv");
        Directory.CreateDirectory(dest);
        using (var archive = new ZipArchive(new MemoryStream(zip)))
            foreach (var e in archive.Entries.Where(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                e.ExtractToFile(Path.Combine(dest, e.Name), overwrite: true);
        return own;
    }

    private static void WriteResource(string name, string path)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("pxdSpeak.Chatterbox." + name)
                      ?? throw new InvalidOperationException("missing resource " + name);
        using var f = File.Create(path);
        s.CopyTo(f);
    }

    private static ProcessStartInfo Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Folder,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        // Everything stays inside the Chatterbox folder.
        psi.Environment["UV_CACHE_DIR"] = Path.Combine(Folder, "cache");
        psi.Environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(Folder, "python");
        psi.Environment["UV_NO_PROGRESS"] = "1";
        psi.Environment["HF_HOME"] = Path.Combine(Folder, "hf");
        psi.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["TQDM_DISABLE"] = "1";
        return psi;
    }

    private static async Task Run(string exe, string[] args, CancellationToken ct)
    {
        using var p = Process.Start(Start(exe, args))!;
        var err = new System.Text.StringBuilder();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (err)
                err.AppendLine(e.Data);
            AppendLog(e.Data);
        };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                AppendLog(e.Data);
        };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        using (ct.Register(() =>
               {
                   try { p.Kill(true); } catch { /* gone */ }
               }))
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            string tail;
            lock (err)
                tail = string.Join(" ", err.ToString().Split('\n').Where(l => l.Trim().Length > 0).TakeLast(2)).Trim();
            throw new InvalidOperationException($"{Path.GetFileNameWithoutExtension(exe)} failed: {tail}");
        }
    }

    private static void AppendLog(string line)
    {
        try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch { /* best effort */ }
    }

    // ---- server ------------------------------------------------------------------------------------------------------

    /// <summary>Starts the server if needed and waits until it has loaded the model (up to a few minutes on first start).</summary>
    public static async Task EnsureRunning(CancellationToken ct, bool firstStart = false)
    {
        if (!IsInstalled)
            throw new InvalidOperationException("Chatterbox isn't installed yet.");
        _lastUse = DateTime.UtcNow;
        Task<bool> wait;
        lock (Gate)
        {
            if (_server is { HasExited: false } && _ready is not null)
                wait = _ready.Task;
            else
            {
                _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _ready.Task;
                Launch(firstStart);
            }
        }

        await wait.WaitAsync(TimeSpan.FromMinutes(firstStart ? 30 : 5), ct).ConfigureAwait(false);
        if (!wait.Result)
            throw new InvalidOperationException(Status);
    }

    private static void Launch(bool firstStart)
    {
        // Script refreshed every start, so an app update brings a new server along.
        try { WriteResource("server.py", ServerScript); } catch { /* keep the old one */ }
        Set(ServerState.Starting, firstStart ? "Downloading the voice model (about 4 GB)…" : "Loading Chatterbox on the graphics card…");
        var psi = Start(EnvPython, [ServerScript, "--port", "0", "--parent-pid", Environment.ProcessId.ToString()]);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = _ready!;
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            AppendLog(e.Data);
            var parts = e.Data.Split(' ');
            if (parts.Length >= 4 && parts[0] == "READY")
            {
                _port = int.Parse(parts[1]);
                Device = parts[2];
                SampleRate = int.Parse(parts[3]);
                Set(ServerState.Ready, Device == "cuda" ? "Ready on the graphics card" : "Ready (no graphics card found: running on the CPU, slow)");
                tcs.TrySetResult(true);
            }
        };
        var lastErr = "";
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            AppendLog(e.Data);
            if (e.Data.Trim().Length > 0)
                lastErr = e.Data.Trim();
        };
        p.Exited += (_, _) =>
        {
            lock (Gate)
                if (_server == p)
                    _server = null;
            if (tcs.TrySetResult(false))
                Set(ServerState.Failed, "Chatterbox stopped: " + (lastErr.Length > 0 ? lastErr : "see " + LogFile));
            else if (State == ServerState.Ready)
                Set(ServerState.Stopped, "Stopped");
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _server = p;
        _idleTimer ??= new Timer(_ => StopIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private static void StopIfIdle()
    {
        if (IdleMinutes > 0 && State == ServerState.Ready && DateTime.UtcNow - _lastUse > TimeSpan.FromMinutes(IdleMinutes))
            Stop("Stopped after being unused, to free the graphics card");
    }

    public static void Stop(string status = "Stopped")
    {
        Process? p;
        lock (Gate)
        {
            p = _server;
            _server = null;
            _ready = null;
        }

        if (p is null)
            return;
        try { p.Kill(true); } catch { /* already gone */ }
        Set(IsInstalled ? ServerState.Stopped : ServerState.NotInstalled, status);
    }

    /// <summary>Loads the model in the background so the first reading doesn't wait.</summary>
    public static void Preload()
    {
        if (IsInstalled && State is ServerState.Stopped or ServerState.Failed)
            _ = Task.Run(async () =>
            {
                try { await EnsureRunning(CancellationToken.None); } catch { /* shown in Status */ }
            });
    }

    /// <summary>Streams speech for <paramref name="text"/>, one sentence at a time.</summary>
    public static void Speak(string text, string clip, Func<float[], int, bool> chunk, CancellationToken ct)
    {
        EnsureRunning(ct).GetAwaiter().GetResult();
        _lastUse = DateTime.UtcNow;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/speak")
        {
            // A plain body with Content-Length: the server reads exactly that many bytes (no chunked encoding).
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { text, voice = clip, temperature = 0.8 }), System.Text.Encoding.UTF8, "application/json"),
        };
        using var response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var stream = response.Content.ReadAsStream(ct);
        using var cancel = ct.Register(() => stream.Dispose());
        var header = new byte[4];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!ReadExactly(stream, header))
                    break;
                var n = BitConverter.ToInt32(header);
                if (n < 0)
                {
                    // The server explains what went wrong (e.g. a voice clip shorter than 5 seconds).
                    var msg = new byte[-n];
                    ReadExactly(stream, msg);
                    throw new InvalidOperationException(System.Text.Encoding.UTF8.GetString(msg));
                }

                var bytes = new byte[n * 4];
                if (!ReadExactly(stream, bytes))
                    break;
                var samples = new float[n];
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                _lastUse = DateTime.UtcNow;
                if (!chunk(samples, SampleRate))
                    break;
            }
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
        }
        catch (IOException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static bool ReadExactly(Stream s, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = s.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                return false;
            read += n;
        }

        return true;
    }

    /// <summary>Removes everything Chatterbox installed (Python, PyTorch, model, voice clips).</summary>
    public static void Uninstall()
    {
        Stop();
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, true);
        Set(ServerState.NotInstalled, "");
    }
}

/// <summary>Chatterbox as one of the app's engines.</summary>
public sealed class ChatterboxSynth(string clip) : ISynth
{
    public void Speak(string text, float speed, Func<float[], int, bool> chunk, CancellationToken ct) =>
        Chatterbox.Speak(text, clip, chunk, ct);

    public void Dispose()
    {
    }
}
