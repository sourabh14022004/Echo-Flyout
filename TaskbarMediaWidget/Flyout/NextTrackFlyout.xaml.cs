using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Point = System.Windows.Point;
using TaskbarMediaWidget.Media;
using TaskbarMediaWidget.Settings;
using TaskbarMediaWidget.Windows;

namespace TaskbarMediaWidget.Flyout;

/// <summary>
/// Transient "now playing" popup shown on a real track change. Reuses one window instance for
/// the app's lifetime — repeated track changes while it's already visible just refresh the
/// content and reset the dismiss timer instead of stacking a second popup.
/// </summary>
public partial class NextTrackFlyout : Wpf.Ui.Controls.FluentWindow
{
    private readonly DispatcherTimer _dismissTimer;
    private bool _isShown;

    /// <summary>Whether the popup is currently on screen (so its content can be kept in sync).</summary>
    public bool IsShowing => _isShown;

    public NextTrackFlyout()
    {
        InitializeComponent();

        _dismissTimer = new DispatcherTimer();
        _dismissTimer.Tick += (_, _) => Dismiss();

        // Must run before the window is first composited, or DWM frosts the acrylic in light mode.
        SourceInitialized += (_, _) => WindowStyler.SetDarkMode(this, true);

        Loaded += (_, _) => WindowStyler.MakeToolWindow(this);
    }

    /// <summary>
    /// Updates the content of an already-visible popup without touching the dismiss timer.
    ///
    /// Artwork often arrives in a later metadata event than the title — browsers in particular
    /// report the track first and the cover a moment after — so without this the popup would keep
    /// showing whatever was available at the instant the track changed while the taskbar widget
    /// moved on to the real artwork.
    /// </summary>
    public void RefreshContent(MediaSnapshot snapshot)
    {
        if (!_isShown) return;
        ApplyContent(snapshot);
    }

    public void ShowFor(MediaSnapshot snapshot, Point topLeftDips, TimeSpan duration)
    {
        Logger.Log($"Flyout.ShowFor: Title='{snapshot.Title}' duration={duration} isShown={_isShown}");
        ApplyContent(snapshot);

        Left = topLeftDips.X;
        Top = topLeftDips.Y;

        if (!_isShown)
        {
            _isShown = true;
            Show();
            ((Storyboard)RootBorder.Resources["EnterStoryboard"]).Begin(RootBorder);
        }

        Logger.Log($"Flyout.ShowFor: positioned at Left={Left} Top={Top} ActualWidth={ActualWidth} ActualHeight={ActualHeight}");

        _dismissTimer.Stop();
        _dismissTimer.Interval = duration;
        _dismissTimer.Start();
        Logger.Log($"Flyout.ShowFor: dismiss timer (re)started for {duration}");
    }

    private void ApplyContent(MediaSnapshot snapshot)
    {
        TitleText.Text = snapshot.Title;
        ArtistText.Text = snapshot.Artist;
        AlbumArt.Source = snapshot.Thumbnail;
        Logger.Log($"Flyout.ApplyContent: thumbnail={(snapshot.Thumbnail == null ? "null" : $"{snapshot.Thumbnail.PixelWidth}x{snapshot.Thumbnail.PixelHeight}")}");
    }

    private void Dismiss()
    {
        Logger.Log($"Flyout.Dismiss: timer fired, isShown={_isShown}");
        _dismissTimer.Stop();
        if (!_isShown) return;

        // Storyboard.Completed's `sender` is an internal ClockGroup, not the Storyboard itself —
        // casting it throws. Unsubscribe via the captured resource reference instead.
        var exit = (Storyboard)RootBorder.Resources["ExitStoryboard"];
        exit.Completed += OnExitCompleted;
        exit.Begin(RootBorder);
        Logger.Log("Flyout.Dismiss: exit storyboard begun");
    }

    private void OnExitCompleted(object? sender, EventArgs e)
    {
        Logger.Log("Flyout.OnExitCompleted: hiding");
        var exit = (Storyboard)RootBorder.Resources["ExitStoryboard"];
        exit.Completed -= OnExitCompleted;
        _isShown = false;
        Hide();
    }
}
