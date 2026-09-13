using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TaskbarMediaWidget.Media;
using TaskbarMediaWidget.Settings;
using TaskbarMediaWidget.Windows;

namespace TaskbarMediaWidget.Dashboard;

/// <summary>
/// Modern Glassmorphic dashboard: live media player, interactive taskbar/flyout previews,
/// and instant settings synchronization.
/// </summary>
public partial class DashboardWindow : Wpf.Ui.Controls.FluentWindow
{
    private sealed record DisplayOption(string Label, string? Key);

    private readonly AppSettings _settings;
    private readonly MediaSessionService? _mediaService;
    private readonly Action? _testFlyoutAction;

    private bool _loading = true;

    // Geometry data for Play / Pause toggle icon
    private static readonly Geometry PlayGeometry = Geometry.Parse("M 3,2 L 13,8 L 3,14 Z");
    private static readonly Geometry PauseGeometry = Geometry.Parse("M 3,2 L 6,2 L 6,14 L 3,14 Z M 10,2 L 13,2 L 13,14 L 10,14 Z");

    public DashboardWindow(AppSettings settings, MediaSessionService? mediaService = null, Action? testFlyoutAction = null)
    {
        _settings = settings;
        _mediaService = mediaService;
        _testFlyoutAction = testFlyoutAction;

        InitializeComponent();
        LoadFromSettings();

        // Dark mode must be set before the window is first composited, otherwise DWM frosts the
        // acrylic backdrop in LIGHT mode and the wallpaper washes out the light-on-glass text.
        // SourceInitialized is the first point the HWND exists, and it runs before Loaded.
        SourceInitialized += (_, _) =>
        {
            WindowStyler.SetDarkMode(this, true);
            WindowStyler.ApplyRoundedCorners(this);
        };

        Loaded += (_, _) =>
        {
            UpdateTaskbarPreview();
            UpdateCornerPreview(_settings.FlyoutPosition);

            if (_mediaService != null)
            {
                _mediaService.MediaChanged += OnMediaChanged;
                UpdateMediaUI(_mediaService.CurrentSnapshot);
            }
        };

        Closed += (_, _) =>
        {
            if (_mediaService != null)
            {
                _mediaService.MediaChanged -= OnMediaChanged;
            }
        };
    }

    private void LoadFromSettings()
    {
        _loading = true;

        WidgetEnabledToggle.IsChecked = _settings.WidgetEnabled;
        HomeWidgetToggle.IsChecked = _settings.WidgetEnabled;

        FlyoutEnabledToggle.IsChecked = _settings.FlyoutEnabled;
        HomeFlyoutToggle.IsChecked = _settings.FlyoutEnabled;

        IdleAnimationToggle.IsChecked = _settings.IdleAnimationEnabled;
        HomeIdleToggle.IsChecked = _settings.IdleAnimationEnabled;

        FilterAdsToggle.IsChecked = _settings.FilterIncidentalAudio;

        (_settings.MinimumTrackSeconds switch
        {
            <= 0 => MinLenOff,
            <= 30 => MinLen30,
            <= 60 => MinLen60,
            _ => MinLen90
        }).IsChecked = true;

        int percent = (int)Math.Round(Math.Clamp(_settings.Opacity, 0.3, 1.0) * 100);
        OpacitySlider.Value = percent;
        OpacityValue.Text = $"{percent}%";

        (_settings.IdleHideTimeoutSeconds switch
        {
            <= 0 => IdlePermanent,
            <= 60 => Idle1m,
            <= 300 => Idle5m,
            _ => Idle10m
        }).IsChecked = true;

        if (_settings.DockSide == "Start")
        {
            DockStart.IsChecked = true;
        }
        else
        {
            DockTray.IsChecked = true;
        }

        switch (_settings.FlyoutPosition)
        {
            case "TopLeft":
                CornerTopLeft.IsChecked = true;
                break;
            case "TopRight":
                CornerTopRight.IsChecked = true;
                break;
            case "BottomLeft":
                CornerBottomLeft.IsChecked = true;
                break;
            default:
                CornerBottomRight.IsChecked = true;
                break;
        }

        (_settings.FlyoutDurationSeconds switch
        {
            <= 2 => Dur2,
            <= 4 => Dur4,
            <= 6 => Dur6,
            _ => Dur8
        }).IsChecked = true;

        LoadDisplays();

        bool isStartup = StartupManager.IsEnabled();
        StartupToggle.IsChecked = isStartup;
        HomeStartupToggle.IsChecked = isStartup;

        VersionText.Text = $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";
        SettingsPathLabel.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarMediaWidget", "settings.json");
        LogPathLabel.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarMediaWidget", "log.txt");

        _loading = false;
    }

    private void LoadDisplays()
    {
        var options = new List<DisplayOption> { new("Automatic (primary display)", null) };

        IntPtr primary = DisplayManager.PrimaryMonitor;
        int index = 1;
        foreach (IntPtr monitor in DisplayManager.EnumerateMonitors())
        {
            Rect bounds = DisplayManager.GetMonitorBoundsPhysical(monitor);
            if (bounds.IsEmpty) continue;

            string key = $"{(int)bounds.X},{(int)bounds.Y}";
            string suffix = monitor == primary ? " (Primary)" : string.Empty;
            options.Add(new DisplayOption($"Display {index}{suffix} — {(int)bounds.Width}×{(int)bounds.Height}", key));
            index++;
        }

        DisplayCombo.ItemsSource = options;
        DisplayCombo.SelectedItem = options.Find(o => o.Key == _settings.TargetMonitorKey) ?? options[0];
    }

    // ----- Live Media Handling -----

    private void OnMediaChanged(object? sender, MediaSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() => UpdateMediaUI(snapshot));
    }

    private void UpdateMediaUI(MediaSnapshot snapshot)
    {
        if (LiveSongTitle == null) return;

        if (!snapshot.HasSession)
        {
            LiveSongTitle.Text = "No media playing";
            LiveSongArtist.Text = "Start playback on Spotify, browser, or media player";
            LiveSongAlbum.Text = string.Empty;
            LiveAppBadge.Text = "Idle";
            LiveStatusText.Text = "Waiting for playback";
            LiveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x99));
            LiveArtworkImage.Source = null;
            LiveArtworkFallback.Visibility = Visibility.Visible;
            LivePlayPauseIcon.Data = PlayGeometry;
            return;
        }

        LiveSongTitle.Text = string.IsNullOrWhiteSpace(snapshot.Title) ? "Unknown Track" : snapshot.Title;
        LiveSongArtist.Text = string.IsNullOrWhiteSpace(snapshot.Artist) ? "Unknown Artist" : snapshot.Artist;
        LiveSongAlbum.Text = snapshot.Album;

        string appName = snapshot.SourceAppId ?? string.Empty;
        if (appName.Contains("spotify", StringComparison.OrdinalIgnoreCase)) appName = "Spotify";
        else if (appName.Contains("chrome", StringComparison.OrdinalIgnoreCase)) appName = "Google Chrome";
        else if (appName.Contains("msedge", StringComparison.OrdinalIgnoreCase)) appName = "Microsoft Edge";
        else if (appName.Contains("firefox", StringComparison.OrdinalIgnoreCase)) appName = "Firefox";
        else if (appName.Contains("vlc", StringComparison.OrdinalIgnoreCase)) appName = "VLC Media Player";
        else if (string.IsNullOrWhiteSpace(appName)) appName = "Media Player";

        LiveAppBadge.Text = appName;

        if (snapshot.IsPlaying)
        {
            LiveStatusText.Text = "Playing";
            LiveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
            LivePlayPauseIcon.Data = PauseGeometry;
        }
        else
        {
            LiveStatusText.Text = "Paused";
            LiveStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));
            LivePlayPauseIcon.Data = PlayGeometry;
        }

        if (snapshot.Thumbnail != null)
        {
            LiveArtworkImage.Source = snapshot.Thumbnail;
            LiveArtworkFallback.Visibility = Visibility.Collapsed;
        }
        else
        {
            LiveArtworkImage.Source = null;
            LiveArtworkFallback.Visibility = Visibility.Visible;
        }
    }

    private void PlayerPrev_Click(object sender, RoutedEventArgs e) => _ = _mediaService?.SkipPreviousAsync();
    private void PlayerPlayPause_Click(object sender, RoutedEventArgs e) => _ = _mediaService?.TogglePlayPauseAsync();
    private void PlayerNext_Click(object sender, RoutedEventArgs e) => _ = _mediaService?.SkipNextAsync();

    // ----- Interactive Previews -----

    private void SimTaskbar_Changed(object sender, RoutedEventArgs e)
    {
        UpdateTaskbarPreview();
    }

    private void OpenWindowsTaskbarSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:taskbar",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.LogException("Failed to open Windows Taskbar Settings", ex);
        }
    }

    private void UpdateTaskbarPreview()
    {
        if (_loading || SimulatedWidget == null || DockStart == null || OpacitySlider == null) return;

        bool isDockStart = DockStart.IsChecked == true;
        bool isIconsCentered = SimTaskbarCentered?.IsChecked ?? true;

        // Position Windows 11 Start & App icons in preview
        if (SimulatedIcons != null)
        {
            SimulatedIcons.HorizontalAlignment = isIconsCentered ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
            SimulatedIcons.Margin = isIconsCentered ? new Thickness(0) : new Thickness(8, 0, 0, 0);
        }

        // Position Simulated Widget in preview
        if (isDockStart)
        {
            // When docking near Start (left):
            // If taskbar icons are centered, the widget sits in the left space with clean margins (no overlap)
            // If taskbar icons are left-aligned, it sits on the left overlapping the start button and warns the user
            SimulatedWidget.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            SimulatedWidget.Margin = isIconsCentered ? new Thickness(8, 0, 0, 0) : new Thickness(80, 0, 0, 0);

            if (PreviewWarningBanner != null)
            {
                PreviewWarningBanner.Visibility = isIconsCentered ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        else
        {
            // When docking near Tray (right):
            // Sits comfortably to the left of the tray clock with plenty of breathing room
            SimulatedWidget.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            SimulatedWidget.Margin = new Thickness(0, 0, 75, 0);

            if (PreviewWarningBanner != null)
            {
                PreviewWarningBanner.Visibility = Visibility.Collapsed;
            }
        }

        if (SimulatedTray != null)
        {
            SimulatedTray.Visibility = Visibility.Visible;
        }

        SimulatedWidget.Opacity = Math.Clamp(OpacitySlider.Value / 100.0, 0.3, 1.0);

        // Update recommendation text based on chosen dock side
        if (DockRecommendationText != null)
        {
            if (isDockStart)
            {
                DockRecommendationText.Text = "For the best visual experience when docking Near Start Button (Left), set Windows Taskbar Alignment to Center in Windows Settings. This gives the widget a dedicated slot on the left without crowding your Start button.";
            }
            else
            {
                DockRecommendationText.Text = "When docking Near System Tray (Right), the widget works seamlessly with both Centered and Left-aligned Windows taskbars, placing your media controls right next to the system clock.";
            }
        }
    }

    private void UpdateCornerPreview(string? corner)
    {
        if (_loading || PreviewCornerTL == null || PreviewCornerTR == null || PreviewCornerBL == null || PreviewCornerBR == null)
            return;

        var defaultBg = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        var defaultBorder = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        var activeBg = new SolidColorBrush(Color.FromArgb(0x33, 0x4C, 0xC2, 0xFF));
        var activeBorder = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));

        PreviewCornerTL.Background = defaultBg;
        PreviewCornerTL.BorderBrush = defaultBorder;
        PreviewCornerTR.Background = defaultBg;
        PreviewCornerTR.BorderBrush = defaultBorder;
        PreviewCornerBL.Background = defaultBg;
        PreviewCornerBL.BorderBrush = defaultBorder;
        PreviewCornerBR.Background = defaultBg;
        PreviewCornerBR.BorderBrush = defaultBorder;

        Border activeBorderElement = corner switch
        {
            "TopLeft" => PreviewCornerTL,
            "TopRight" => PreviewCornerTR,
            "BottomLeft" => PreviewCornerBL,
            _ => PreviewCornerBR
        };

        activeBorderElement.Background = activeBg;
        activeBorderElement.BorderBrush = activeBorder;
    }

    private void CornerPreview_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string corner)
        {
            switch (corner)
            {
                case "TopLeft": CornerTopLeft.IsChecked = true; break;
                case "TopRight": CornerTopRight.IsChecked = true; break;
                case "BottomLeft": CornerBottomLeft.IsChecked = true; break;
                default: CornerBottomRight.IsChecked = true; break;
            }
            UpdateCornerPreview(corner);
            SaveToSettings();
        }
    }

    private void CornerChip_Changed(object sender, RoutedEventArgs e)
    {
        string corner =
            CornerTopLeft.IsChecked == true ? "TopLeft" :
            CornerTopRight.IsChecked == true ? "TopRight" :
            CornerBottomLeft.IsChecked == true ? "BottomLeft" : "BottomRight";

        UpdateCornerPreview(corner);
        Setting_Changed(sender, e);
    }

    // ----- Navigation -----

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageHome == null) return;

        PageHome.Visibility = Visibility.Collapsed;
        PageWidget.Visibility = Visibility.Collapsed;
        PageFlyout.Visibility = Visibility.Collapsed;
        PageSystem.Visibility = Visibility.Collapsed;
        PageAbout.Visibility = Visibility.Collapsed;

        StackPanel page = NavList.SelectedIndex switch
        {
            1 => PageWidget,
            2 => PageFlyout,
            3 => PageSystem,
            4 => PageAbout,
            _ => PageHome
        };
        page.Visibility = Visibility.Visible;
    }

    private void NavToWidget_Click(object sender, RoutedEventArgs e)
    {
        NavList.SelectedIndex = 1;
    }

    // ----- Quick Toggles on Home & Settings Sync -----

    private void HomeWidgetToggle_Click(object sender, RoutedEventArgs e)
    {
        WidgetEnabledToggle.IsChecked = HomeWidgetToggle.IsChecked;
        Setting_Changed(sender, e);
    }

    private void HomeFlyoutToggle_Click(object sender, RoutedEventArgs e)
    {
        FlyoutEnabledToggle.IsChecked = HomeFlyoutToggle.IsChecked;
        Setting_Changed(sender, e);
    }

    private void HomeIdleToggle_Click(object sender, RoutedEventArgs e)
    {
        IdleAnimationToggle.IsChecked = HomeIdleToggle.IsChecked;
        Setting_Changed(sender, e);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || OpacityValue == null) return;
        OpacityValue.Text = $"{(int)e.NewValue}%";
        UpdateTaskbarPreview();
        Setting_Changed(sender, e);
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        // Sync toggle states between Home and Detail pages
        if (sender == WidgetEnabledToggle) HomeWidgetToggle.IsChecked = WidgetEnabledToggle.IsChecked;
        if (sender == FlyoutEnabledToggle) HomeFlyoutToggle.IsChecked = FlyoutEnabledToggle.IsChecked;
        if (sender == IdleAnimationToggle) HomeIdleToggle.IsChecked = IdleAnimationToggle.IsChecked;

        UpdateTaskbarPreview();
        SaveToSettings();
    }

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool isEnabled = (sender as Wpf.Ui.Controls.ToggleSwitch)?.IsChecked == true;
        StartupToggle.IsChecked = isEnabled;
        HomeStartupToggle.IsChecked = isEnabled;
        StartupManager.SetEnabled(isEnabled);
    }

    private void TestFlyout_Click(object sender, RoutedEventArgs e)
    {
        _testFlyoutAction?.Invoke();
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarMediaWidget");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.LogException("OpenSettingsFolder failed", ex);
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarMediaWidget", "log.txt");
            if (!File.Exists(logPath))
            {
                File.WriteAllText(logPath, "Echo Flyout Log\n");
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.LogException("OpenLogs failed", ex);
        }
    }

    private void SaveToSettings()
    {
        _settings.WidgetEnabled = WidgetEnabledToggle.IsChecked == true;
        _settings.FlyoutEnabled = FlyoutEnabledToggle.IsChecked == true;
        _settings.IdleAnimationEnabled = IdleAnimationToggle.IsChecked == true;
        _settings.FilterIncidentalAudio = FilterAdsToggle.IsChecked == true;

        _settings.MinimumTrackSeconds =
            MinLen30.IsChecked == true ? 30 :
            MinLen60.IsChecked == true ? 60 :
            MinLen90.IsChecked == true ? 90 : 0;
        _settings.Opacity = OpacitySlider.Value / 100.0;

        _settings.IdleHideTimeoutSeconds =
            IdlePermanent.IsChecked == true ? 0 :
            Idle1m.IsChecked == true ? 60 :
            Idle5m.IsChecked == true ? 300 : 600;

        _settings.DockSide = DockStart.IsChecked == true ? "Start" : "Tray";

        _settings.FlyoutPosition =
            CornerTopLeft.IsChecked == true ? "TopLeft" :
            CornerTopRight.IsChecked == true ? "TopRight" :
            CornerBottomLeft.IsChecked == true ? "BottomLeft" : "BottomRight";

        _settings.FlyoutDurationSeconds =
            Dur2.IsChecked == true ? 2 :
            Dur4.IsChecked == true ? 4 :
            Dur6.IsChecked == true ? 6 : 8;

        _settings.TargetMonitorKey = (DisplayCombo.SelectedItem as DisplayOption)?.Key;

        _settings.Save();
    }
}
