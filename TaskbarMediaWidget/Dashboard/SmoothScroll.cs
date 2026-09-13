using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace TaskbarMediaWidget.Dashboard;

/// <summary>
/// Animated mouse-wheel scrolling for a <see cref="ScrollViewer"/>. Opt in with
/// <c>dash:SmoothScroll.IsEnabled="True"</c>.
///
/// WPF's default wheel handling jumps three lines instantly, which reads as a stutter next to the
/// glide every other modern app has. This intercepts the wheel and eases the offset to a target
/// instead.
///
/// The mechanism is roundabout for a reason: <see cref="ScrollViewer.VerticalOffset"/> is
/// read-only and can't be animated. So an attached <c>TargetOffset</c> property is animated
/// instead, and its change callback pushes each interpolated value into
/// <see cref="ScrollViewer.ScrollToVerticalOffset"/>.
/// </summary>
public static class SmoothScroll
{
    /// <summary>Pixels travelled per unit of delta. A full notch is ±120, so ~102px per notch.</summary>
    private const double StepMultiplier = 0.85;

    /// <summary>
    /// Short on purpose. A long glide only feels good for isolated wheel notches; a trackpad
    /// streams events far faster than any long animation can finish, and the result reads as lag.
    /// At 100ms this is just enough to take the edge off a wheel notch without the content ever
    /// trailing behind a finger on the trackpad.
    /// </summary>
    private static readonly Duration GlideDuration = new(TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// How far the destination may run ahead of what is actually on screen.
    ///
    /// This is the whole fix for trackpad lag. Without a cap, each rapid event stacks another step
    /// onto the target, so a fast swipe queues up hundreds of pixels and the view spends its time
    /// chasing a destination that keeps receding — which is precisely what "laggy" feels like.
    /// At roughly half a notch the content stays attached to the finger however fast the events
    /// arrive; raising it lets quick wheel clicks combine further at the cost of some slack.
    /// </summary>
    private const double MaxLeadPixels = 60;

    // ----- Public opt-in -----

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    // ----- Internals -----

    /// <summary>The animatable stand-in for the read-only VerticalOffset.</summary>
    private static readonly DependencyProperty TargetOffsetProperty =
        DependencyProperty.RegisterAttached(
            "TargetOffset", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(0.0, OnTargetOffsetChanged));

    /// <summary>Whether a glide is currently in flight, so successive notches can accumulate.</summary>
    private static readonly DependencyProperty IsGlidingProperty =
        DependencyProperty.RegisterAttached(
            "IsGliding", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer) return;

        if ((bool)e.NewValue)
        {
            scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void OnTargetOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer) return;

        // Nothing to scroll — leave the event alone so a parent scroller (or anything else that
        // cares about the wheel) still gets a look at it.
        if (scrollViewer.ScrollableHeight <= 0) return;

        // Continue from the in-flight destination so consecutive events combine instead of each
        // one restarting the glide from scratch. When idle, read the real offset — that keeps us
        // honest after a scrollbar drag or keyboard scroll moved things behind our back.
        double current = scrollViewer.VerticalOffset;
        double origin = (bool)scrollViewer.GetValue(IsGlidingProperty)
            ? (double)scrollViewer.GetValue(TargetOffsetProperty)
            : current;

        // ...but never let that destination drift further than MaxLeadPixels from what's actually
        // on screen. This is what stops fast input (a trackpad swipe) from queueing up a backlog
        // the view can never catch, which is what made it feel laggy.
        origin = Math.Clamp(origin, current - MaxLeadPixels, current + MaxLeadPixels);

        double target = Math.Clamp(origin - e.Delta * StepMultiplier, 0, scrollViewer.ScrollableHeight);

        // Already parked at that end — just swallow the notch and stay put.
        //
        // Note what must NOT happen here: clearing the animation with
        // BeginAnimation(TargetOffsetProperty, null). That reverts the property to its base value
        // of 0, which fires the change callback and scrolls straight back to the top — so hitting
        // the bottom and nudging once more would fling you to the start of the page. Leave the
        // held animation exactly where it is.
        if (Math.Abs(target - scrollViewer.VerticalOffset) < 0.5)
        {
            e.Handled = true;
            return;
        }

        // One path for every input device. A wheel notch arrives alone and gets a visible glide;
        // a trackpad's rapid stream keeps replacing this animation, and because the destination
        // can't run away (see the clamp above) the content simply follows the finger.
        var glide = new DoubleAnimation
        {
            From = current,
            To = target,
            Duration = GlideDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        scrollViewer.SetValue(IsGlidingProperty, true);
        glide.Completed += (_, _) => scrollViewer.SetValue(IsGlidingProperty, false);

        scrollViewer.BeginAnimation(TargetOffsetProperty, glide);
        e.Handled = true;
    }
}
