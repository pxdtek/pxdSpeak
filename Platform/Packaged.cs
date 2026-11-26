using System.Runtime.InteropServices;

namespace pxdSpeak.Platform;

/// <summary>
/// Whether this copy runs as an MSIX package (the Microsoft Store build). The zip build from GitHub never is. Store
/// rules differ: no self-update, start-up through a StartupTask, no downloading code (so no Chatterbox), and the
/// install folder is locked.
/// </summary>
public static class Packaged
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    private static readonly Lazy<bool> Is = new(Check);

    public static bool IsPackaged => Is.Value;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    private static bool Check()
    {
        if (Environment.GetEnvironmentVariable("PXDSPEAK_FAKE_PACKAGED") == "1")
            return true;   // self-test renders of the Store look
        if (!OperatingSystem.IsWindowsVersionAtLeast(8))
            return false;
        try
        {
            var length = 0;
            var rc = GetCurrentPackageFullName(ref length, null);
            return rc != AppModelErrorNoPackage && (rc == ErrorInsufficientBuffer || rc == 0);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A copy of the browser extension where browsers can load it from ("Load unpacked"): the install folder for the
    /// zip build; Documents\pxd Speak\BrowserExtension for the Store build, whose install folder is hidden and locked
    /// (AppData won't do either: a packaged app's AppData writes go to private package storage).
    /// </summary>
    public static string BrowserExtensionFolder()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "BrowserExtension");
        if (!IsPackaged)
            return shipped;
        var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "pxd Speak", "BrowserExtension");
        try
        {
            foreach (var file in Directory.EnumerateFiles(shipped, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(dest, Path.GetRelativePath(shipped, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to) || File.GetLastWriteTimeUtc(to) != File.GetLastWriteTimeUtc(file))
                {
                    File.Copy(file, to, overwrite: true);
                    File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(file));
                }
            }

            return dest;
        }
        catch
        {
            return shipped;
        }
    }
}
