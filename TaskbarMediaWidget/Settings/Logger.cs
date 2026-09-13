using System;
using System.IO;

namespace TaskbarMediaWidget.Settings;

/// <summary>
/// Minimal file logger. This is a background/tray app with no console, so a log file at
/// %LocalAppData%\TaskbarMediaWidget\log.txt is the only practical way to diagnose failures that
/// happen on a user's machine (including silently-swallowed async exceptions) after the fact.
/// </summary>
internal static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMediaWidget", "log.txt");

    private static readonly object Lock = new();

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                string? dir = Path.GetDirectoryName(LogPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never be why the app fails.
        }
    }

    public static void LogException(string context, Exception ex) => Log($"{context}: {ex}");
}
