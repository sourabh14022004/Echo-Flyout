using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using TaskbarMediaWidget.Dashboard;
using TaskbarMediaWidget.Flyout;
using TaskbarMediaWidget.Media;
using TaskbarMediaWidget.Settings;
using TaskbarMediaWidget.Windows;

namespace TaskbarMediaWidget;

public partial class MainWindow : Window
{
    private const double FallbackTrayInset = 190; // used only when the notify-area lookup fails
    private const double EdgeMargin = 8;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly MediaSessionService _mediaService;
    private readonly ThemeWatcher _themeWatcher = new();
    private readonly NextTrackFlyout _flyout = new();
    private readonly TrayIconManager _trayIcon = new();
    private readonly SettingsWatcher _settingsWatcher = new();
    private readonly DispatcherTimer _idleTimer;
    private TaskbarTracker? _taskbarTracker;
    private DashboardWindow? _dashboardWindow;

    private TaskbarInfo? _lastTaskbarInfo;
    private bool _taskbarVisible;
    private bool _shouldShowForMedia;
    private bool _idleBannerActive;
    private bool _windowCurrentlyVisible;
    private bool _lastKnownIsPlaying;
    private MediaSnapshot _lastSnapshot = MediaSnapshot.Idle;

    private readonly DispatcherTimer _flyoutDwellTimer;
    private MediaSnapshot? _pendingFlyoutSnapshot;

    public MainWindow()
    {
        InitializeComponent();

        _mediaService = new MediaSessionService(_settings);

        // A track has to still be the current one after this long before the flyout announces it.
        // Browsers churn through sessions constantly — an ad starting, a clip ending, a tab
        // swapping — and without a settling period every one of those blips pops a notification.
        _flyoutDwellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _flyoutDwellTimer.Tick += (_, _) =>
        {
            _flyoutDwellTimer.Stop();

            // Present the *latest* snapshot, not the one captured when the track changed. They
            // describe the same track, but the newer one carries artwork that may have arrived
            // since — otherwise the popup and the taskbar widget show different covers.
            if (_pendingFlyoutSnapshot is { } pending && pending.IsSameTrack(_lastSnapshot))
            {
                PresentFlyout(_lastSnapshot);
            }
            _pendingFlyoutSnapshot = null;
        };

        _idleTimer = new DispatcherTimer();
        _idleTimer.Tick += (_, _) =>
        {
            Logger.Log("Idle timer elapsed — hiding.");
            _idleTimer.Stop();
            _shouldShowForMedia = false;
            UpdateWindowVisibility();
        };

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        WindowStyler.MakeToolWindow(this);
        // Deliberately NOT applying DWM rounded corners here: on Windows 11 that attribute also
        // makes DWM paint a drop shadow *outside* the window rect, which lands on the desktop
        // above the taskbar and reads as the widget spilling off the bar. The flyout still uses
        // it — a floating card should be rounded; a strip embedded in the taskbar should not.
        WindowStyler.SetDarkMode(this, _themeWatcher.IsDarkMode);
        ApplyTheme(_themeWatcher.IsDarkMode);

        _themeWatcher.ThemeChanged += (_, isDark) => Dispatcher.Invoke(() =>
        {
            WindowStyler.SetDarkMode(this, isDark);
            ApplyTheme(isDark);
        });

        _mediaService.MediaChanged += (_, snapshot) => Dispatcher.Invoke(() => HandleMediaSnapshot(snapshot));
        _mediaService.TrackChanged += (_, snapshot) => Dispatcher.Invoke(() => ShowFlyout(snapshot));

        _taskbarTracker = new TaskbarTracker(ResolveTargetMonitor);
        _taskbarTracker.TaskbarChanged += (_, info) => Dispatcher.Invoke(() => ApplyTaskbarInfo(info));
        _taskbarTracker.Refreshed += (_, info) => Dispatcher.Invoke(() => ReassertZOrder(info));
        _taskbarTracker.Start();

        _trayIcon.SettingsRequested += (_, _) => Dispatcher.Invoke(OpenSettingsDashboard);
        _trayIcon.ExitRequested += (_, _) => Dispatcher.Invoke(() => Application.Current.Shutdown());

        _settingsWatcher.SettingsChanged += (_, _) => Dispatcher.Invoke(OnSettingsChanged);

        RenderMedia(MediaSnapshot.Idle);

        try
        {
            await _mediaService.InitializeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow: media service initialization failed", ex);
            RenderMedia(MediaSnapshot.Idle);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _taskbarTracker?.Dispose();
        _themeWatcher.Dispose();
        _trayIcon.Dispose();
        _settingsWatcher.Dispose();
        _idleTimer.Stop();
        _ = _mediaService.DisposeAsync();
        _flyout.Close();
    }

    /// <summary>
    /// Reloads AppSettings after a write (from the dashboard, or an external edit of the file) and
    /// re-applies everything that isn't already read live on each use: opacity/theme, the master
    /// enable switch, position (dock side may have changed), and the idle-hide state.
    /// </summary>
    private void OnSettingsChanged()
    {
        _settings.CopyFrom(AppSettings.Load());
        Logger.Log($"OnSettingsChanged: WidgetEnabled={_settings.WidgetEnabled} FlyoutEnabled={_settings.FlyoutEnabled} Opacity={_settings.Opacity} DockSide={_settings.DockSide} FlyoutPosition={_settings.FlyoutPosition} IdleHideTimeoutSeconds={_settings.IdleHideTimeoutSeconds}");

        ApplyTheme(_themeWatcher.IsDarkMode);

        // Re-run the layer swap so toggling the idle animation applies immediately.
        RenderMedia(_lastSnapshot);

        if (_lastTaskbarInfo is { IsVisible: true } info)
        {
            PositionForTaskbar(info);
        }

        // The master switch may have just flipped — apply it without waiting for a media event.
        UpdateWindowVisibility();

        // Idle-hide may have just become Permanent (or stopped being Permanent) — re-evaluate
        // against the current playback state rather than waiting for the next media event.
        if (_shouldShowForMedia)
        {
            _idleTimer.Stop();
            if (_settings.IdleHideTimeoutSeconds > 0 && !_lastKnownIsPlaying)
            {
                _idleTimer.Interval = TimeSpan.FromSeconds(_settings.IdleHideTimeoutSeconds);
                _idleTimer.Start();
            }
        }
    }

    /// <summary>The live settings instance, shared with the dashboard so edits apply in-process.</summary>
    public AppSettings Settings => _settings;

    /// <summary>Opens the settings dashboard, or focuses it if it's already up.</summary>
    public void OpenSettingsDashboard()
    {
        if (_dashboardWindow != null)
        {
            if (_dashboardWindow.WindowState == WindowState.Minimized)
            {
                _dashboardWindow.WindowState = WindowState.Normal;
            }
            _dashboardWindow.Activate();
            return;
        }

        _dashboardWindow = new DashboardWindow(_settings, _mediaService, TriggerTestFlyout);
        _dashboardWindow.Closed += (_, _) => _dashboardWindow = null;
        _dashboardWindow.Show();
    }

    /// <summary>Fires a preview flyout notification so the user can test flyout placement and duration from the dashboard.</summary>
    public void TriggerTestFlyout()
    {
        MediaSnapshot snapshot = _mediaService.CurrentSnapshot.HasSession
            ? _mediaService.CurrentSnapshot
            : new MediaSnapshot(
                HasSession: true,
                Title: "Starboy (Flyout Preview)",
                Artist: "The Weeknd ft. Daft Punk",
                Album: "Starboy",
                Thumbnail: null,
                IsPlaying: true,
                SourceAppId: "Preview");

        IntPtr monitor = ResolveTargetMonitor();
        Rect workArea = DisplayManager.GetWorkAreaBounds(monitor);
        const double flyoutWidth = 320;
        const double flyoutHeight = 96;
        const double margin = 16;

        Point position = _settings.FlyoutPosition switch
        {
            "BottomLeft" => new Point(workArea.Left + margin, workArea.Bottom - flyoutHeight - margin),
            "TopRight" => new Point(workArea.Right - flyoutWidth - margin, workArea.Top + margin),
            "TopLeft" => new Point(workArea.Left + margin, workArea.Top + margin),
            _ /* BottomRight */ => new Point(workArea.Right - flyoutWidth - margin, workArea.Bottom - flyoutHeight - margin)
        };

        _flyout.ShowFor(snapshot, position, TimeSpan.FromSeconds(Math.Max(2, _settings.FlyoutDurationSeconds)));
    }

    private IntPtr ResolveTargetMonitor()
    {
        if (!string.IsNullOrEmpty(_settings.TargetMonitorKey))
        {
            string[] parts = _settings.TargetMonitorKey.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
            {
                IntPtr? found = DisplayManager.FindMonitorByPosition(x, y);
                if (found.HasValue) return found.Value;
            }
        }

        return DisplayManager.PrimaryMonitor;
    }

    // ----- Taskbar tracking -----

    private void ApplyTaskbarInfo(TaskbarInfo? info)
    {
        Logger.Log(info == null
            ? "ApplyTaskbarInfo: no taskbar found."
            : $"ApplyTaskbarInfo: Edge={info.Edge} Bounds={info.Bounds} AutoHide={info.AutoHideEnabled} Collapsed={info.IsCollapsed} Obscured={info.IsObscured} IsVisible={info.IsVisible}");

        _lastTaskbarInfo = info;
        _taskbarVisible = info?.IsVisible ?? false;

        if (info == null || !info.IsVisible)
        {
            UpdateWindowVisibility();
            return;
        }

        PositionForTaskbar(info);
        WindowStyler.PinAboveTaskbar(this, info.Hwnd);
        UpdateWindowVisibility();
    }

    /// <summary>
    /// Re-asserts topmost on every tracker refresh, not just on geometry changes. Win+D (and
    /// anything else that makes the shell re-assert the taskbar's own z-order) leaves the widget
    /// stranded behind the bar otherwise, because the taskbar's rect never changed and so nothing
    /// would have prompted a re-pin.
    /// </summary>
    private void ReassertZOrder(TaskbarInfo? info)
    {
        if (!_windowCurrentlyVisible || info is not { IsVisible: true }) return;
        WindowStyler.PinAboveTaskbar(this, info.Hwnd);
    }

    private void PositionForTaskbar(TaskbarInfo info)
    {
        Rect barDips = DisplayManager.ToDips(info.Bounds, info.MonitorHandle);

        Rect? notifyPhysical = TaskbarTracker.TryGetNotifyAreaBounds(info.Hwnd);
        Rect? notifyDips = notifyPhysical.HasValue
            ? DisplayManager.ToDips(notifyPhysical.Value, info.MonitorHandle)
            : null;

        switch (info.Edge)
        {
            case TaskbarEdge.Top:
            case TaskbarEdge.Bottom:
            {
                // Fill the taskbar's own thickness exactly (flush top and bottom) so the widget
                // reads as part of the bar instead of a floating card poking above/below it.
                // MinHeight/MaxHeight (not just Height) are required here: with SizeToContent
                // set to "Width", WPF's own remeasure can silently override a plain Height
                // assignment back to the content's natural size — hard Min/Max constraints are
                // what actually stick through that remeasure.
                double flushHeight = Math.Max(28, barDips.Height);
                MinHeight = flushHeight;
                MaxHeight = flushHeight;
                Height = flushHeight;
                ResizeAlbumArt(flushHeight);

                double width = ActualWidth > 0 ? ActualWidth : 250;
                double left = _settings.DockSide == "Start"
                    ? barDips.Left + EdgeMargin
                    : notifyDips.HasValue
                        ? notifyDips.Value.Left - width - EdgeMargin
                        : barDips.Right - width - FallbackTrayInset;
                Left = Clamp(left, barDips.Left, Math.Max(barDips.Left, barDips.Right - width));
                Top = barDips.Top;

                // Read the result back in physical pixels and log it beside the taskbar's own
                // physical rect, so "is the widget exactly on the bar?" is a checkable fact
                // rather than a visual judgement.
                Rect actual = WindowStyler.GetPhysicalBounds(this);
                Logger.Log($"PositionForTaskbar: taskbar(px)={info.Bounds} widget(px)={actual} " +
                           $"topDelta={actual.Top - info.Bounds.Top} bottomDelta={actual.Bottom - info.Bounds.Bottom}");
                break;
            }

            case TaskbarEdge.Left:
            case TaskbarEdge.Right:
            {
                double height = ActualHeight > 0 ? ActualHeight : 58;
                double top = _settings.DockSide == "Start"
                    ? barDips.Top + EdgeMargin
                    : notifyDips.HasValue
                        ? notifyDips.Value.Top - height - EdgeMargin
                        : barDips.Bottom - height - FallbackTrayInset;
                Top = Clamp(top, barDips.Top, Math.Max(barDips.Top, barDips.Bottom - height));

                double width = ActualWidth > 0 ? ActualWidth : 250;
                Left = barDips.Left + (barDips.Width - width) / 2;
                break;
            }
        }
    }

    /// <summary>
    /// Keeps the album art inside the taskbar band with breathing room on both sides, so content
    /// can never be what forces the widget taller than the bar. A 48-DIP bar gives 32px art,
    /// in line with Windows 11's own taskbar iconography.
    /// </summary>
    private void ResizeAlbumArt(double windowHeight)
    {
        double size = Clamp(windowHeight - 10, 16, 38);
        AlbumArtBorder.Width = size;
        AlbumArtBorder.Height = size;
    }

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), max);

    // ----- Media state -----

    private void HandleMediaSnapshot(MediaSnapshot snapshot)
    {
        Logger.Log($"HandleMediaSnapshot: HasSession={snapshot.HasSession} IsPlaying={snapshot.IsPlaying} Title='{snapshot.Title}'");
        RenderMedia(snapshot);
        _lastSnapshot = snapshot;
        _lastKnownIsPlaying = snapshot.IsPlaying;

        // IdleHideTimeoutSeconds <= 0 means "Permanent" — never auto-hide from inactivity.
        bool idleHideEnabled = _settings.IdleHideTimeoutSeconds > 0;

        if (snapshot.HasSession)
        {
            _shouldShowForMedia = true;
            UpdateWindowVisibility();

            _idleTimer.Stop();
            if (!snapshot.IsPlaying && idleHideEnabled)
            {
                // Paused, not stopped: stay visible but start the countdown to auto-hide.
                _idleTimer.Interval = TimeSpan.FromSeconds(_settings.IdleHideTimeoutSeconds);
                _idleTimer.Start();
            }
        }
        else
        {
            // No session — show the idle banner (including on a fresh launch, before anything has
            // ever played) and run the same countdown-then-hide the paused state uses.
            _shouldShowForMedia = true;
            UpdateWindowVisibility();

            _idleTimer.Stop();
            if (idleHideEnabled)
            {
                _idleTimer.Interval = TimeSpan.FromSeconds(_settings.IdleHideTimeoutSeconds);
                _idleTimer.Start();
            }
        }
    }

    private void RenderMedia(MediaSnapshot snapshot)
    {
        TitleText.Text = snapshot.Title;
        ArtistText.Text = snapshot.Artist;
        AlbumArt.Source = snapshot.Thumbnail;
        PlayIcon.Visibility = snapshot.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseIcon.Visibility = snapshot.IsPlaying ? Visibility.Visible : Visibility.Collapsed;

        ShowIdleBanner(!snapshot.HasSession && _settings.IdleAnimationEnabled);

        // Keep a visible popup in step with the widget — late-arriving artwork for the same track
        // should land in both places, not just here.
        if (snapshot.HasSession && _flyout.IsShowing && snapshot.IsSameTrack(_lastSnapshot))
        {
            _flyout.RefreshContent(snapshot);
        }
    }

    /// <summary>
    /// Swaps between the media row and the idle mascot. The banner's animations are started and
    /// stopped with its visibility so nothing is ever animating off screen.
    /// </summary>
    private void ShowIdleBanner(bool showBanner)
    {
        _idleBannerActive = showBanner;

        ContentGrid.Visibility = showBanner ? Visibility.Collapsed : Visibility.Visible;
        IdleBanner.Visibility = showBanner ? Visibility.Visible : Visibility.Collapsed;

        if (showBanner && _windowCurrentlyVisible)
        {
            IdleBanner.Start();
        }
        else
        {
            IdleBanner.Stop();
        }
    }

    // ----- Visibility (taskbar auto-hide AND idle-hide both gate the same window) -----

    private void UpdateWindowVisibility()
    {
        // WidgetEnabled is the dashboard's master switch — it overrides every other reason to show.
        bool shouldBeVisible = _settings.WidgetEnabled && _taskbarVisible && _shouldShowForMedia;
        Logger.Log($"UpdateWindowVisibility: taskbarVisible={_taskbarVisible} shouldShowForMedia={_shouldShowForMedia} shouldBeVisible={shouldBeVisible} currentlyVisible={_windowCurrentlyVisible}");
        if (shouldBeVisible == _windowCurrentlyVisible) return;
        _windowCurrentlyVisible = shouldBeVisible;

        if (shouldBeVisible)
        {
            Logger.Log($"UpdateWindowVisibility: showing at Left={Left} Top={Top} Width={Width} Height={Height} ActualWidth={ActualWidth} ActualHeight={ActualHeight}");
            if (!IsVisible) Show();
            BeginStoryboard("FadeInStoryboard");
            if (_idleBannerActive) IdleBanner.Start();

            // ActualHeight is still pre-layout at this point, so confirm the on-screen rect once
            // the layout/render pass for the newly shown content has actually run.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                Logger.Log($"UpdateWindowVisibility: post-layout widget(px)={WindowStyler.GetPhysicalBounds(this)} idleBanner={_idleBannerActive}"));
        }
        else
        {
            var fadeOut = (Storyboard)RootBorder.Resources["FadeOutStoryboard"];
            void OnCompleted(object? s, EventArgs e)
            {
                fadeOut.Completed -= OnCompleted;
                if (!_windowCurrentlyVisible)
                {
                    Hide();
                    IdleBanner.Stop(); // nothing should animate behind a hidden window
                }
            }
            fadeOut.Completed += OnCompleted;
            fadeOut.Begin(RootBorder);
        }
    }

    private void BeginStoryboard(string key) => ((Storyboard)RootBorder.Resources[key]).Begin(RootBorder);

    // ----- Theme -----

    private void ApplyTheme(bool isDark)
    {
        // The widget has no background of its own any more (it blends into the taskbar), so the
        // Opacity setting scales the content itself rather than tinting a card.
        ContentRoot.Opacity = Clamp(_settings.Opacity, 0.3, 1.0);

        Resources["WidgetForegroundBrush"] = new SolidColorBrush(isDark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A));
        Resources["WidgetSecondaryForegroundBrush"] = new SolidColorBrush(isDark
            ? Color.FromRgb(0xB3, 0xB3, 0xB3)
            : Color.FromRgb(0x5F, 0x5F, 0x5F));
    }

    // ----- Next-track flyout -----

    /// <summary>
    /// Queues a track change for announcement. Nothing is shown until the dwell timer confirms
    /// the track is still playing, so momentary sessions never reach the screen.
    /// </summary>
    private void ShowFlyout(MediaSnapshot snapshot)
    {
        Logger.Log($"MainWindow.ShowFlyout: TrackChanged event received for '{snapshot.Title}' HasSession={snapshot.HasSession}");
        if (!_settings.FlyoutEnabled || !snapshot.HasSession || _lastTaskbarInfo == null) return;

        _pendingFlyoutSnapshot = snapshot;
        _flyoutDwellTimer.Stop();
        _flyoutDwellTimer.Start();
    }

    private void PresentFlyout(MediaSnapshot snapshot)
    {
        if (!_settings.FlyoutEnabled || _lastTaskbarInfo == null) return;

        Point position = ComputeFlyoutPosition(_lastTaskbarInfo);
        _flyout.ShowFor(snapshot, position, TimeSpan.FromSeconds(_settings.FlyoutDurationSeconds));
    }

    /// <summary>
    /// Screen-corner placement (user-configurable via AppSettings.FlyoutPosition), independent of
    /// where the taskbar itself is docked. Uses the work area (excludes the taskbar on whichever
    /// edge it occupies) so the flyout never overlaps it regardless of DockSide/taskbar edge.
    /// </summary>
    private Point ComputeFlyoutPosition(TaskbarInfo info)
    {
        const double flyoutWidth = 320;
        const double flyoutHeight = 96;
        const double margin = 16;

        Rect workArea = DisplayManager.GetWorkAreaBounds(info.MonitorHandle);

        Point result = _settings.FlyoutPosition switch
        {
            "BottomLeft" => new Point(workArea.Left + margin, workArea.Bottom - flyoutHeight - margin),
            "TopRight" => new Point(workArea.Right - flyoutWidth - margin, workArea.Top + margin),
            "TopLeft" => new Point(workArea.Left + margin, workArea.Top + margin),
            _ /* BottomRight */ => new Point(workArea.Right - flyoutWidth - margin, workArea.Bottom - flyoutHeight - margin)
        };

        Logger.Log($"ComputeFlyoutPosition: FlyoutPosition={_settings.FlyoutPosition} workArea={workArea} result={result}");
        return result;
    }

    // ----- Transport controls -----

    private void PrevButton_Click(object sender, RoutedEventArgs e) => _ = _mediaService.SkipPreviousAsync();
    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _ = _mediaService.TogglePlayPauseAsync();
    private void NextButton_Click(object sender, RoutedEventArgs e) => _ = _mediaService.SkipNextAsync();
}
