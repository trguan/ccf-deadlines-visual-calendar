using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace DeadlineCalendar.App;

/// <summary>Wheel and automatic scrolling that can be interrupted by a calendar-anchor restore.</summary>
public sealed class SmoothScrollViewer : ScrollViewer
{
    private static readonly DependencyProperty AnimatedVerticalOffsetProperty = DependencyProperty.Register(
        "AnimatedVerticalOffset", typeof(double), typeof(SmoothScrollViewer),
        new FrameworkPropertyMetadata(0d, OnAnimatedVerticalOffsetChanged));

    private static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(220);
    private double targetOffset;
    private TimeSpan animationDuration = ScrollDuration;
    private bool animating;
    private bool suppressOffsetCallback;
    private int animationGeneration;

    public bool IsAnimating => animating;
    public bool IsAutomaticScrolling { get; private set; }

    public SmoothScrollViewer()
    {
        Unloaded += (_, _) => StopAnimation();
        ScrollChanged += (_, e) =>
        {
            if (!animating || (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)) return;
            var boundedTarget = ClampOffset(targetOffset);
            if (Math.Abs(boundedTarget - targetOffset) > .01) AnimateTo(boundedTarget, animationDuration, IsAutomaticScrolling);
        };
    }

    /// <summary>Adds a pixel/DIP delta to the pending wheel destination, retaining rapid wheel input.</summary>
    public void ScrollBySmooth(double delta)
    {
        VerifyAccess();
        if (!double.IsFinite(delta)) throw new ArgumentOutOfRangeException(nameof(delta));
        if (delta == 0)
        {
            if (IsAutomaticScrolling) StopAnimation();
            return;
        }
        // A wheel gesture takes over an automatic return at the position the user
        // currently sees; only successive wheel gestures accumulate their destination.
        var origin = ClampOffset(animating && !IsAutomaticScrolling ? targetOffset : VerticalOffset);
        var maximum = ClampOffset(double.MaxValue);
        // Clamp the operands before adding, so even an unusually large finite delta stays finite.
        var destination = delta > 0
            ? origin + Math.Min(delta, Math.Max(0, maximum - origin))
            : origin - Math.Min(-delta, Math.Max(0, origin));
        AnimateTo(ClampOffset(destination), ScrollDuration, automatic: false);
    }

    /// <summary>Scrolls to an absolute offset with a slow-fast-slow animation; zero duration is immediate.</summary>
    public void ScrollToSmooth(double destination, TimeSpan duration)
    {
        VerifyAccess();
        if (!double.IsFinite(destination)) throw new ArgumentOutOfRangeException(nameof(destination));
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        AnimateTo(ClampOffset(destination), duration, automatic: true);
    }

    /// <summary>Cancels any animation and immediately requests the specified calendar-anchor offset.</summary>
    public void SetVerticalOffsetImmediately(double offset)
    {
        VerifyAccess();
        if (!double.IsFinite(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        var boundedOffset = ClampOffset(offset);
        CancelClockAndSetBase(boundedOffset);
        ScrollToVerticalOffset(boundedOffset);
    }

    /// <summary>Stops at the currently displayed position rather than jumping to the animation destination.</summary>
    public void StopAnimation()
    {
        VerifyAccess();
        var current = ClampOffset(VerticalOffset);
        CancelClockAndSetBase(current);
        // Supersede any offset command queued by the previous animation frame.
        ScrollToVerticalOffset(current);
    }

    private void AnimateTo(double destination, TimeSpan duration, bool automatic)
    {
        var current = ClampOffset(VerticalOffset);
        destination = ClampOffset(destination);
        CancelClockAndSetBase(current);
        // Replace a previously queued animation-frame command before the new
        // clock starts, so interrupting an automatic return cannot jump a frame.
        ScrollToVerticalOffset(current);
        targetOffset = destination;
        // Calendar scrolling is an explicit in-app behavior. Windows' client-area
        // animation setting must not silently turn a requested smooth return into a jump.
        if (!IsLoaded || duration == TimeSpan.Zero || Math.Abs(destination - current) < .1)
        {
            SetVerticalOffsetImmediately(destination);
            return;
        }

        animating = true;
        IsAutomaticScrolling = automatic;
        animationDuration = duration;
        int generation = animationGeneration;
        var animation = new DoubleAnimation(current, destination, new Duration(duration))
        {
            EasingFunction = new CubicEase { EasingMode = automatic ? EasingMode.EaseInOut : EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            // A newer animation, wheel event, anchor restore, resize, or unload invalidates this completion.
            if (!animating || generation != animationGeneration) return;
            var finalOffset = ClampOffset(targetOffset);
            CancelClockAndSetBase(finalOffset);
            ScrollToVerticalOffset(finalOffset);
        };
        BeginAnimation(AnimatedVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void CancelClockAndSetBase(double value)
    {
        animationGeneration++;
        animating = false;
        IsAutomaticScrolling = false;
        targetOffset = value;
        suppressOffsetCallback = true;
        try
        {
            // Removing a clock briefly exposes the old base value; suppress that value so it cannot jump the scroll position.
            BeginAnimation(AnimatedVerticalOffsetProperty, null);
            SetCurrentValue(AnimatedVerticalOffsetProperty, value);
        }
        finally { suppressOffsetCallback = false; }
    }

    private double ClampOffset(double offset)
    {
        var maximum = double.IsFinite(ScrollableHeight) ? Math.Max(0, ScrollableHeight) : 0;
        return Math.Clamp(offset, 0, maximum);
    }

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var viewer = (SmoothScrollViewer)sender;
        if (!viewer.suppressOffsetCallback) viewer.ScrollToVerticalOffset(viewer.ClampOffset((double)args.NewValue));
    }
}
