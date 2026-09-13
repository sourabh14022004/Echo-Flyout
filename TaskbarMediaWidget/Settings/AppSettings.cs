using System;
using System.IO;
using System.Text.Json;

namespace TaskbarMediaWidget.Settings;

/// <summary>
/// The set of values that are genuinely user-tunable (not derivable from live Windows state).
/// Persisted as JSON so the separate Electron settings dashboard can read/write the exact same
/// file — this is the entire sync mechanism between the two apps, no IPC involved.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Master switch for the taskbar strip itself.</summary>
    public bool WidgetEnabled { get; set; } = true;

    /// <summary>Master switch for the next-track "now playing" popup.</summary>
    public bool FlyoutEnabled { get; set; } = true;

    /// <summary>0 or negative means "Permanent" — never auto-hide from inactivity.</summary>
    public int IdleHideTimeoutSeconds { get; set; } = 10;

    public int FlyoutDurationSeconds { get; set; } = 4;

    /// <summary>Background opacity of the docked widget, 0.3–1.0.</summary>
    public double Opacity { get; set; } = 0.85;

    /// <summary>Which side of the taskbar the widget docks near: "Tray" or "Start".</summary>
    public string DockSide { get; set; } = "Tray";

    /// <summary>Screen corner the next-track flyout appears in: "BottomRight", "BottomLeft", "TopRight", "TopLeft".</summary>
    public string FlyoutPosition { get; set; } = "BottomRight";

    /// <summary>Show the animated mascot when nothing is playing (false falls back to a plain text row).</summary>
    public bool IdleAnimationEnabled { get; set; } = true;

    /// <summary>
    /// Skip media sessions that look like incidental audio rather than something you chose to
    /// play — web ads, autoplaying clips, notification sounds. Judged on metadata: no title at
    /// all, or neither an artist nor cover art. Real music and video almost always carry at least
    /// one of those; a banner ad carries neither.
    /// </summary>
    public bool FilterIncidentalAudio { get; set; } = true;

    /// <summary>
    /// Also skip anything shorter than this many seconds. Off by default (0) because it catches
    /// legitimately short content too — Shorts, Reels, clips — so it's opt-in for people who
    /// mostly play full-length tracks and want ads gone regardless of their metadata.
    /// </summary>
    public int MinimumTrackSeconds { get; set; }

    /// <summary>null = follow the primary monitor; otherwise "{x},{y}" — the monitor's top-left corner in virtual-desktop coordinates (matches Electron's screen.getAllDisplays() bounds).</summary>
    public string? TargetMonitorKey { get; set; }

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMediaWidget", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file — fall back to defaults rather than crash.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (dir != null) Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort persistence — losing a settings write shouldn't crash the widget.
        }
    }

    /// <summary>Copies every field from another instance into this one (used when reloading after an external write).</summary>
    public void CopyFrom(AppSettings other)
    {
        WidgetEnabled = other.WidgetEnabled;
        FlyoutEnabled = other.FlyoutEnabled;
        IdleHideTimeoutSeconds = other.IdleHideTimeoutSeconds;
        FlyoutDurationSeconds = other.FlyoutDurationSeconds;
        Opacity = other.Opacity;
        DockSide = other.DockSide;
        FlyoutPosition = other.FlyoutPosition;
        IdleAnimationEnabled = other.IdleAnimationEnabled;
        FilterIncidentalAudio = other.FilterIncidentalAudio;
        MinimumTrackSeconds = other.MinimumTrackSeconds;
        TargetMonitorKey = other.TargetMonitorKey;
    }
}
