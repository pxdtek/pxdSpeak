using Microsoft.Win32;

namespace pxdSpeak.Services;

/// <summary>
/// Start with Windows: a per-user Run registry entry for the zip build; the package's StartupTask (TaskId
/// pxdSpeakStartup in AppxManifest.xml) for the Store build, where Run entries don't work.
/// </summary>
public static class Autostart
{
    private const string AppName = "pxdSpeak";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupTaskId = "pxdSpeakStartup";

    /// <summary>The Store build's answer, once known (the StartupTask is only asked asynchronously).</summary>
    private static bool _packagedEnabled;

    public static async Task<bool> IsEnabledAsync()
    {
        if (!Platform.Packaged.IsPackaged)
            return IsEnabled();
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            _packagedEnabled = task.State is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }
        catch
        {
            _packagedEnabled = false;
        }

        return _packagedEnabled;
    }

    public static bool IsEnabled()
    {
        if (Platform.Packaged.IsPackaged)
            return _packagedEnabled;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return LaunchCommand() is { } command && key?.GetValue(AppName) is string existing
                   && string.Equals(existing, command + " --minimized", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        if (Platform.Packaged.IsPackaged)
        {
            _ = SetPackaged(enabled);
            return;
        }

        var command = LaunchCommand() ?? throw new InvalidOperationException("Can't determine how this app was launched.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(AppName, command + " --minimized");
        else
            key.DeleteValue(AppName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Turning it on asks Windows, which says no if the user switched it off in Task Manager or Settings › Startup
    /// (DisabledByUser); then only they can turn it back on there.
    /// </summary>
    private static async Task SetPackaged(bool enabled)
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            if (enabled)
                _packagedEnabled = await task.RequestEnableAsync() is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            else
            {
                task.Disable();
                _packagedEnabled = false;
            }
        }
        catch
        {
            /* shown as it really is next time */
        }
    }

    private static string? LaunchCommand()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return null;

        // Running through "dotnet pxdSpeak.dll" (e.g. dotnet run): register the dll under dotnet instead.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Environment.GetCommandLineArgs().FirstOrDefault();
            return string.IsNullOrEmpty(dll) ? null : $"\"{exe}\" \"{dll}\"";
        }

        return $"\"{exe}\"";
    }
}
