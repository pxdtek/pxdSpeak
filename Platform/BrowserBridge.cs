using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace pxdSpeak.Platform;

/// <summary>What the extension sends to be read: the selected words as the page shows them.</summary>
public sealed record BrowserRead(BrowserBridge.Connection From, List<string> Words, HashSet<int> BlockEnds, int Start);

/// <summary>
/// The link to the pxd Speak browser extension: a WebSocket on 127.0.0.1:47321 (this PC only). Only browser
/// extensions may connect (the Origin must be chrome-extension://…), so web pages can't use it. The extension sends the
/// selected words; pxd Speak sends back which word is being read so the page can highlight it.
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    public static int Port => int.TryParse(Environment.GetEnvironmentVariable("PXDSPEAK_BRIDGE_PORT"), out var p) ? p : 47321;
    private const string Path = "/pxdspeak";

    private readonly TcpListener _listener = new(IPAddress.Loopback, Port);   // 127.0.0.1 only
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<BrowserRead?>> _grabs = new();
    private int _nextGrab;

    public bool Listening { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Raised (worker thread) when an extension connects or goes away.</summary>
    public event Action? Changed;

    /// <summary>Raised (worker thread) when the extension asks for something to be read (its menu, or a click on a word).</summary>
    public event Action<BrowserRead>? ReadRequested;

    /// <summary>Raised (worker thread) when the extension asks to stop (Esc on the page).</summary>
    public event Action? StopRequested;

    public IReadOnlyList<string> Browsers => _connections.Keys.Select(c => c.Browser).Distinct().ToList();

    public int Count => _connections.Count;

    public void Start()
    {
        try
        {
            _listener.Start();
            Listening = true;
            _ = Task.Run(AcceptLoop);
        }
        catch (Exception ex)
        {
            Error = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                ? $"Port {Port} is in use by another program"
                : ex.Message;
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var owned = client;
        var stream = client.GetStream();
        var headers = await ReadHeaders(stream);
        if (headers is null)
            return;
        var (path, h) = headers.Value;
        var origin = h.GetValueOrDefault("origin", "");
        var key = h.GetValueOrDefault("sec-websocket-key", "");
        if (path != Path || key.Length == 0 || !h.GetValueOrDefault("upgrade", "").Equals("websocket", StringComparison.OrdinalIgnoreCase)
            || !(origin.StartsWith("chrome-extension://", StringComparison.Ordinal) || origin.StartsWith("moz-extension://", StringComparison.Ordinal)))
        {
            await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
        using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
        var conn = new Connection(ws);
        _connections[conn] = 0;
        try
        {
            var buffer = new byte[1 << 16];
            var message = new MemoryStream();
            while (ws.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, _stop.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
                message.Write(buffer, 0, r.Count);
                if (message.Length > 8 << 20)
                    break;   // nobody selects 8 MB of text
                if (!r.EndOfMessage)
                    continue;
                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                try
                {
                    Handle(conn, JsonNode.Parse(json));
                }
                catch
                {
                    /* one bad message doesn't end the link */
                }
            }
        }
        catch
        {
            /* closed */
        }
        finally
        {
            _connections.TryRemove(conn, out _);
            Changed?.Invoke();
        }
    }

    private void Handle(Connection conn, JsonNode? m)
    {
        switch ((string?)m?["type"])
        {
            case "hello":
                conn.Browser = (string?)m!["browser"] is { Length: > 0 } b ? b : "Browser";
                conn.Version = (string?)m["version"] ?? "";
                Changed?.Invoke();
                break;
            case "grabbed":
            {
                var id = (int?)m!["grabId"] ?? -1;
                if (_grabs.TryRemove(id, out var tcs))
                    tcs.TrySetResult(m["none"] is not null ? null : ParseRead(conn, m));
                break;
            }
            case "read":
                if (ParseRead(conn, m!) is { } read)
                    ReadRequested?.Invoke(read);
                break;
            case "stop":
                StopRequested?.Invoke();
                break;
        }
    }

    private static BrowserRead? ParseRead(Connection conn, JsonNode m)
    {
        var words = m["words"]?.AsArray().Select(w => (string?)w ?? "").ToList() ?? [];
        if (words.Count == 0)
            return null;
        var blocks = m["blocks"]?.AsArray().Select(b => (int?)b ?? -1).Where(b => b >= 0).ToHashSet() ?? [];
        var start = Math.Clamp((int?)m["start"] ?? 0, 0, words.Count - 1);
        return new BrowserRead(conn, words, blocks, start);
    }

    /// <summary>
    /// Asks every connected browser for its selection; the one whose page has focus and a selection answers. Null when
    /// none does within <paramref name="timeout"/> (then the app copies the selection the usual way).
    /// </summary>
    public async Task<BrowserRead?> Grab(TimeSpan timeout)
    {
        if (_connections.IsEmpty)
            return null;
        var conns = _connections.Keys.ToList();
        var pending = new List<Task<BrowserRead?>>();
        foreach (var c in conns)
        {
            var id = Interlocked.Increment(ref _nextGrab);
            var tcs = new TaskCompletionSource<BrowserRead?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _grabs[id] = tcs;
            pending.Add(tcs.Task);
            _ = c.Send(new { type = "grab", grabId = id });
            _ = Task.Delay(timeout).ContinueWith(_ =>
            {
                if (_grabs.TryRemove(id, out var t))
                    t.TrySetResult(null);
            });
        }

        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            if (await done.ConfigureAwait(false) is { } read)
                return read;
        }

        return null;
    }

    private static async Task<(string Path, Dictionary<string, string> Headers)?> ReadHeaders(NetworkStream stream)
    {
        var buf = new byte[8192];
        var len = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (len < buf.Length)
            {
                var n = await stream.ReadAsync(buf.AsMemory(len), cts.Token);
                if (n == 0)
                    return null;
                len += n;
                var text = Encoding.ASCII.GetString(buf, 0, len);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0)
                    continue;
                var lines = text[..end].Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length < 2 || first[0] != "GET")
                    return null;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }

                return (first[1], headers);
            }
        }
        catch
        {
            /* slow or broken client */
        }

        return null;
    }

    /// <summary>True when the window in front belongs to a Chromium-family browser or Firefox.</summary>
    public static bool BrowserInFront()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant() is "chrome" or "msedge" or "brave" or "opera" or "vivaldi" or "firefox" or "arc" or "chromium";
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { /* stopped */ }
        foreach (var c in _connections.Keys)
            c.Close();
    }

    public sealed class Connection(WebSocket ws)
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        public string Browser { get; set; } = "Browser";
        public string Version { get; set; } = "";

        public async Task Send(object message)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await _send.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                /* gone; the receive loop notices */
            }
            finally
            {
                _send.Release();
            }
        }

        public void Close()
        {
            try { ws.Abort(); } catch { /* gone */ }
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
