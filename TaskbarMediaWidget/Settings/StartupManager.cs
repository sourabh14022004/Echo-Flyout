using System;
using Microsoft.Win32;

namespace TaskbarMediaWidget.Settings;

/// <summary>
/// Launches the app at Windows sign-in via the per-user Run key. The registered command line
/// carries <c>--startup</c> so boot brings up the tray + widget silently, per saved settings,
/// instead of popping the dashboard in the user's face.
/// </summary>
internal static class StartupManager
{
    public const string StartupArgument = "--startup";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarMediaWidget";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            string? exePath = Environment.ProcessPath;
            if (exePath == null) return;

            key.SetValue(ValueName, $"\"{exePath}\" {StartupArgument}");
        }
        catch (Exception ex)
        {
            Logger.LogException("StartupManager: could not update the Run key", ex);
        }
    }
}
