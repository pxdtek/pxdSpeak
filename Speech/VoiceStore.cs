using System.Formats.Tar;
using System.Net.Http;
using ICSharpCode.SharpZipLib.BZip2;

namespace pxdSpeak.Speech;

/// <summary>
/// Downloaded voice packs: %LOCALAPPDATA%\pxdSpeak\Voices\&lt;pack&gt;. Every pack ships the same eSpeak NG pronunciation
/// data (18 MB); it is kept once, in Voices\espeak-ng-data, and shared by all of them.
/// </summary>
public static class VoiceStore
{
    private const string Marker = ".installed";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    static VoiceStore() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("pxdSpeak/1.0");

    /// <summary>Voices folder; PXDSPEAK_DATA_DIR moves it (tests).</summary>
    public static string Folder =>
        Environment.GetEnvironmentVariable("PXDSPEAK_DATA_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, "Voices")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pxdSpeak", "Voices");

    public static string EspeakData => Path.Combine(Folder, "espeak-ng-data");

    public static string PackDir(string pack) => Path.Combine(Folder, pack);

    public static bool IsInstalled(string pack) =>
        File.Exists(Path.Combine(PackDir(pack), Marker)) && Directory.Exists(EspeakData);

    public static bool IsInstalled(VoiceInfo v) => v.Family switch
    {
        VoiceFamily.Windows => true,
        VoiceFamily.Chatterbox => Chatterbox.IsInstalled && (v.Pack.Length == 0 || IsInstalled(v.Pack)),
        _ => IsInstalled(v.Pack),
    };

    /// <summary>Downloads and unpacks a pack. <paramref name="progress"/> gets 0..1 (download), then 1 while unpacking.</summary>
    public static async Task Download(VoicePack pack, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var part = Path.Combine(Folder, pack.Name + ".tar.bz2.part");
        var staging = Path.Combine(Folder, pack.Name + ".unpacking");
        try
        {
            using (var response = await Http.GetAsync(pack.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? (long)(pack.SizeMb * 1024 * 1024);
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(part);
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                var lastReport = 0.0;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    var f = Math.Min(1.0, (double)done / total);
                    if (f - lastReport >= 0.005)
                    {
                        lastReport = f;
                        progress?.Report(f);
                    }
                }
            }

            progress?.Report(1);
            await Task.Run(() => Unpack(part, staging, pack.Name, ct), ct);

            var target = PackDir(pack.Name);
            if (Directory.Exists(target))
                Directory.Delete(target, true);
            Directory.Move(staging, target);
            File.WriteAllText(Path.Combine(target, Marker), DateTime.UtcNow.ToString("O"));
        }
        finally
        {
            TryDelete(part);
            if (Directory.Exists(staging))
                try { Directory.Delete(staging, true); } catch { /* next time */ }
        }
    }

    /// <summary>Unpacks &lt;pack&gt;/… into <paramref name="staging"/>, and &lt;pack&gt;/espeak-ng-data/… into the shared folder once.</summary>
    private static void Unpack(string archive, string staging, string pack, CancellationToken ct)
    {
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var needEspeak = !Directory.Exists(EspeakData);
        var espeakStaging = EspeakData + ".unpacking";
        if (needEspeak && Directory.Exists(espeakStaging))
            Directory.Delete(espeakStaging, true);

        using (var file = File.OpenRead(archive))
        using (var bz = new BZip2InputStream(file))
        using (var tar = new TarReader(bz))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    continue;
                var name = entry.Name.Replace('\\', '/');
                var slash = name.IndexOf('/');
                if (slash < 0)
                    continue;
                var rel = name[(slash + 1)..];
                string root;
                if (rel.StartsWith("espeak-ng-data/", StringComparison.Ordinal))
                {
                    if (!needEspeak)
                        continue;
                    root = espeakStaging;
                    rel = rel["espeak-ng-data/".Length..];
                }
                else
                    root = staging;

                var dest = Path.GetFullPath(Path.Combine(root, rel));
                if (!dest.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;   // never write outside the folder, whatever the archive says
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }

        if (needEspeak && Directory.Exists(espeakStaging))
            Directory.Move(espeakStaging, EspeakData);
    }

    public static void Delete(string pack)
    {
        var dir = PackDir(pack);
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    /// <summary>Space all downloaded voices take, in bytes.</summary>
    public static long SizeOnDisk()
    {
        try
        {
            return Directory.Exists(Folder)
                ? new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
