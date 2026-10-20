using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeadlineCalendar.Core;
using NodaTime;
using Forms = System.Windows.Forms;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace DeadlineCalendar.App;

public sealed class MainWindow : Window
{
    private readonly AppState state;
    private readonly Border background = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
    private readonly Grid content = new();
    private readonly StackPanel months = new();
    private readonly SmoothScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, CanContentScroll = false, PanningMode = PanningMode.VerticalOnly };
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Forms.NotifyIcon tray;
    private readonly Stopwatch activityClock = Stopwatch.StartNew();
    private CalendarIdleReturn idleReturn;
    private int autoReturnDelaySeconds;
    private bool autoReturnEnabled;
    private bool returnWasBlocked;
    private TimeSpan lastUserInput = TimeSpan.MinValue;
    private Point? lastPointerPosition;
    private SettingsWindow? settings;
    private TextBlock headline = new(), deadline = new(), remaining = new();
    private LocalDate startMonth, endMonth, currentMonth, today;
    private Instant? nextBoundary;
    private double rowPitch = 20, dotDiameter = 13.6, monthGap = 6.4;
    private readonly record struct ScrollAnchor(LocalDate Month, double Fraction);
    private ScrollAnchor? pendingAnchor;
    private int layoutRevision;
    private bool initialized, refreshing, exiting, applyingBounds, boundsCaptureQueued;
    internal bool IsReturningToToday => scroll.IsAutomaticScrolling;

    public MainWindow(AppState app)
    {
        state = app;
        autoReturnDelaySeconds = Math.Clamp(state.Preferences.AutoReturnToTodayDelaySeconds, 1, 3600);
        autoReturnEnabled = state.Preferences.AutoReturnToTodayEnabled;
        idleReturn = new CalendarIdleReturn(TimeSpan.FromSeconds(autoReturnDelaySeconds));
        Title = "Deadline Calendar";
        Icon = AppIcon.WindowImage;
        AllowsTransparency = true; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
        Background = Brushes.Transparent; ShowInTaskbar = false;
        MinWidth = 240; MinHeight = Math.Min(480, SystemParameters.WorkArea.Height * .8);
        // Preserve saved dimensions until the HWND identifies the appropriate monitor.
        Width = Math.Max(state.Preferences.Bounds.Width, MinWidth);
        Height = Math.Max(state.Preferences.Bounds.Height, MinHeight);
        if (state.Preferences.Bounds.Left != -1 || state.Preferences.Bounds.Top != -1)
        {
            Left = state.Preferences.Bounds.Left; Top = state.Preferences.Bounds.Top;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - Width - 28; Top = SystemParameters.WorkArea.Top + 28;
        }
        today = SystemClock.Instance.GetCurrentInstant().InZone(state.DisplayZone).Date;
        currentMonth = new LocalDate(today.Year, today.Month, 1);
        var initialRange = CalendarMonthRange.AroundToday(today);
        startMonth = initialRange.Start; endMonth = initialRange.EndExclusive;
        var outer = new Grid { Background = new SolidColorBrush(Color.FromArgb(1,0,0,0)) };
        outer.Children.Add(background);
        content.Margin = new Thickness(8);
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1,GridUnitType.Star) });
        outer.Children.Add(content);
        scroll.Content = months;
        Content = outer;
        AddResizeHandles(outer);
        tray = new Forms.NotifyIcon { Text = "Deadline Calendar", Icon = AppIcon.CreateTrayIcon(), Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(Restore);
        state.Changed += Rebuild;
        state.WindowSizeRequested += ApplyRequestedSize;
        DateDot.HoverViewingChanged += OnHoverViewingChanged;
        PreviewMouseMove += (_, e) =>
        {
            var position = e.GetPosition(this);
            if (lastPointerPosition is { } previous && (position - previous).Length < .25) return;
            lastPointerPosition = position;
            RegisterCalendarActivity();
        };
        MouseLeave += (_, _) => { lastPointerPosition = null; RegisterCalendarActivity(); };
        PreviewMouseDown += (_, _) => RegisterCalendarActivity();
        PreviewMouseWheel += (_, _) => RegisterCalendarActivity();
        PreviewKeyDown += (_, _) => RegisterCalendarActivity();
        PreviewTouchDown += (_, _) => RegisterCalendarActivity();
        PreviewTouchMove += (_, _) => RegisterCalendarActivity();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { idleReturn.Cancel(); scroll.StopAnimation(); }
            else if (initialized) RegisterCalendarActivity(userScroll: Math.Abs(scroll.VerticalOffset - TodayOffset()) > 1);
        };
        clock.Tick += (_, _) => Tick(); clock.Start();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveWindowBounds(); };
        LocationChanged += (_, _) =>
        {
            if (!initialized || applyingBounds) return;
            PublishWindowBounds(state.Preferences.Bounds.Width, state.Preferences.Bounds.Height);
            ScheduleSave();
        };
        SizeChanged += (_, e) =>
        {
            ResizeMonths();
            if (!initialized || applyingBounds) return;
            PublishWindowBounds(e.NewSize.Width, e.NewSize.Height);
            ScheduleSave();
        };
        scroll.SizeChanged += (_, _) => { ResizeMonths(); UpdateEdgeFade(); };
        scroll.ScrollChanged += (_, e) =>
        {
            if (e.ViewportHeightChange != 0) ResizeMonths();
            if (e.VerticalChange != 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) UpdateEdgeFade();
            // Wheel, keyboard and touch scrolling all count; layout restoration
            // and automatic animation frames must not arm a fresh return.
            if (initialized && !refreshing && autoReturnEnabled && !scroll.IsAutomaticScrolling
                && e.VerticalChange != 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0
                && lastUserInput >= TimeSpan.Zero && activityClock.Elapsed - lastUserInput < TimeSpan.FromSeconds(2))
                idleReturn.RecordScroll(activityClock.Elapsed);
            if (initialized && !refreshing && e.VerticalChange < 0 && scroll.VerticalOffset < rowPitch * 1.5) PrependHistory();
            if (initialized && !refreshing && e.VerticalChange > 0 && scroll.ScrollableHeight - scroll.VerticalOffset < rowPitch * 1.5) AppendKnownFuture();
        };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (!initialized || refreshing) return;
            RegisterCalendarActivity(userScroll: true);
            // Wheel input still arrives at an edge where the current range cannot move.
            if (e.Delta < 0 && scroll.ScrollableHeight - scroll.VerticalOffset < rowPitch * 1.5) AppendKnownFuture();
            if (e.Delta > 0 && scroll.VerticalOffset < rowPitch * 1.5) PrependHistory();
            double lines = SystemParameters.WheelScrollLines;
            double step = lines < 0 ? scroll.ViewportHeight * .85 : rowPitch * lines;
            double delta = -e.Delta / 120.0 * step;
            if (refreshing) Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => scroll.ScrollBySmooth(delta));
            else scroll.ScrollBySmooth(delta);
        };
        Loaded += (_, _) =>
        {
            ClampToScreen();
            Rebuild();
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                ResizeMonths(); GoToday(); initialized = true;
                PublishWindowBounds(ActualWidth, ActualHeight);
                ScheduleSave();
            });
        };
        SourceInitialized += (_, _) => (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WindowMessage);
        KeyDown += (_, e) => { if (e.Key == Key.Home) { GoToday(); e.Handled = true; } };
    }
    private void Rebuild()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Rebuild); return; }
        bool interruptedReturn = scroll.IsAutomaticScrolling;
        ConfigureIdleReturn();
        var anchor = CaptureScrollAnchor();
        scroll.StopAnimation();
        refreshing = true;
        today = SystemClock.Instance.GetCurrentInstant().InZone(state.DisplayZone).Date;
        currentMonth = new LocalDate(today.Year, today.Month, 1);
        var range = CalendarMonthRange.AroundToday(today);
        if (state.Preferences.LimitCalendarToOneYear)
        {
            startMonth = range.Start; endMonth = range.EndExclusive;
        }
        else
        {
            if (startMonth > range.Start) startMonth = range.Start;
            if (endMonth < range.EndExclusive) endMonth = range.EndExclusive;
        }
        Topmost = state.Preferences.AlwaysOnTop;
        var fill = state.Theme.Brush("Background"); fill.Opacity = state.Preferences.BackgroundOpacity;
        background.Background = fill;
        var edge = state.Theme.Brush("Separator"); edge.Opacity = state.Preferences.BackgroundOpacity;
        background.BorderBrush = edge;
        content.Opacity = state.Preferences.ContentOpacity;
        content.Children.Clear();
        var header = BuildHeader(); Grid.SetRow(header,0); content.Children.Add(header);
        Grid.SetRow(scroll,1); content.Children.Add(scroll);
        RebuildMonths();
        UpdateHeadline(); UpdateTray();
        ScheduleScrollRestore(anchor);
        UpdateEdgeFade();
        if (interruptedReturn && autoReturnEnabled) idleReturn.RecordScroll(activityClock.Elapsed);
    }
    private UIElement BuildHeader()
    {
        var header = new StackPanel { Margin = new Thickness(12,5,12,8), Background = Brushes.Transparent };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            e.Handled = true;
            try { DragMove(); } catch (InvalidOperationException) { }
        };
        headline = new TextBlock { FontSize = state.TextStyle("Headline").FontSize, FontFamily = state.TextFont("Headline"), FontWeight = state.TextWeight("Headline"), FontStyle = state.TextSlant("Headline"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0,4,0,0) };
        deadline = new TextBlock { FontSize = state.TextStyle("Deadline").FontSize, FontFamily = state.TextFont("Deadline"), FontWeight = state.TextWeight("Deadline"), FontStyle = state.TextSlant("Deadline"), Foreground = state.Theme.Brush("Foreground"), Margin = new Thickness(0,5,0,0), TextWrapping = TextWrapping.Wrap };
        remaining = new TextBlock { FontSize = state.TextStyle("Countdown").FontSize, FontFamily = state.TextFont("Countdown"), FontWeight = state.TextWeight("Countdown"), FontStyle = state.TextSlant("Countdown"), Foreground = state.Theme.Brush("Muted"), Margin = new Thickness(0,5,0,0), TextWrapping = TextWrapping.Wrap };
        header.Children.Add(headline); header.Children.Add(deadline); header.Children.Add(remaining);
        return header;
    }
    private void RebuildMonths()
    {
        var byDate = state.Projection.Occurrences.GroupBy(x => x.DeadlineUtc.InZone(state.DisplayZone).Date).ToDictionary(x => x.Key,x => x.ToList());
        months.Children.Clear();
        for (var month = startMonth; month < endMonth; month = month.PlusMonths(1))
        {
            var view = new MonthView(state, month, byDate);
            view.SetMetrics(rowPitch, dotDiameter, monthGap);
            months.Children.Add(view);
        }
    }
    private void ResizeMonths()
    {
        if (scroll.ViewportHeight <= 0 || months.Children.Count == 0) return;
        var anchor = CaptureScrollAnchor();
        // Keep a stable scale while scrolling; fit the current month and its next five months.
        int weeks = months.Children.OfType<MonthView>().Where(m => m.Month >= currentMonth).Take(6).Sum(m => m.WeekRows);
        if (weeks == 0) return;
        double pitch = scroll.ViewportHeight / (weeks + 6 * .32);
        double diameter = Math.Min(pitch * .68, Math.Max(4, (scroll.ActualWidth - 28) / 13.3));
        pitch = (scroll.ViewportHeight + 6 * diameter) / (weeks + 6);
        if (Math.Abs(rowPitch - pitch) < .01 && Math.Abs(dotDiameter - diameter) < .01) return;
        rowPitch = pitch; dotDiameter = diameter; monthGap = pitch - diameter;
        // Within a month the edge gap is pitch - diameter. Across months it is twice that.
        foreach (MonthView month in months.Children) month.SetMetrics(rowPitch, dotDiameter, monthGap);
        UpdateEdgeFade();
        if (initialized || pendingAnchor != null) ScheduleScrollRestore(anchor);
    }
    private void UpdateEdgeFade()
    {
        if (scroll.ActualHeight <= 0) return;
        double edge = Math.Min(.25, Math.Clamp(rowPitch * 3, 40, 96) / scroll.ActualHeight);
        // At a hard range boundary there are no further dates to scroll into view.
        // Ease out the edge fade so the first and last month remain fully readable.
        double fadeHeight = edge * scroll.ActualHeight;
        byte EdgeAlpha(double distance) => state.Preferences.LimitCalendarToOneYear
            ? (byte)Math.Round(255 * (1 - Math.Clamp(distance / fadeHeight, 0, 1))) : (byte)0;
        var topEdge = Color.FromArgb(EdgeAlpha(scroll.VerticalOffset), 0, 0, 0);
        var bottomEdge = Color.FromArgb(EdgeAlpha(scroll.ScrollableHeight - scroll.VerticalOffset), 0, 0, 0);
        var mask = new LinearGradientBrush { StartPoint = new Point(0,0), EndPoint = new Point(0,1) };
        mask.GradientStops.Add(new GradientStop(topEdge, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, edge));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - edge));
        mask.GradientStops.Add(new GradientStop(bottomEdge, 1));
        mask.Freeze();
        scroll.OpacityMask = mask;
    }
    private ScrollAnchor CaptureScrollAnchor()
    {
        if (pendingAnchor is { } pending) return pending;
        double top = 0;
        foreach (MonthView month in months.Children)
        {
            if (scroll.VerticalOffset < top + month.Height - .001)
                return new ScrollAnchor(month.Month, Math.Clamp((scroll.VerticalOffset - top) / month.Height, 0, 1));
            top += month.Height;
        }
        return new ScrollAnchor(startMonth, 0);
    }
    private void ScheduleScrollRestore(ScrollAnchor anchor)
    {
        // A setting or display-timezone change can remove the visible month.
        // Clamp the anchor before queuing layout, including an existing pending restore.
        if (anchor.Month < startMonth) anchor = new ScrollAnchor(startMonth, 0);
        else if (anchor.Month >= endMonth) anchor = new ScrollAnchor(endMonth.PlusMonths(-1), 1);
        if (scroll.IsAutomaticScrolling && autoReturnEnabled) idleReturn.RecordScroll(activityClock.Elapsed);
        pendingAnchor = anchor; refreshing = true;
        int revision = ++layoutRevision;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (revision != layoutRevision) return;
            ResizeMonths();
            if (revision != layoutRevision) return;
            double top = 0;
            foreach (MonthView month in months.Children)
            {
                if (month.Month >= anchor.Month) { if (month.Month == anchor.Month) top += anchor.Fraction * month.Height; break; }
                top += month.Height;
            }
            scroll.SetVerticalOffsetImmediately(top);
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                if (revision != layoutRevision) return;
                pendingAnchor = null; refreshing = false;
            });
        });
    }
    private void PrependHistory()
    {
        if (state.Preferences.LimitCalendarToOneYear) return;
        var earliest = state.Projection.Occurrences.Count > 0 ? state.Projection.Occurrences.Min(x => x.DeadlineUtc).InZone(state.DisplayZone).Date : currentMonth.PlusYears(-5);
        var minimum = new LocalDate(Math.Min(earliest.Year,currentMonth.Year - 5),1,1);
        if (startMonth <= minimum) return;
        var anchor = CaptureScrollAnchor();
        refreshing = true;
        startMonth = startMonth.PlusMonths(-12);
        RebuildMonths();
        ScheduleScrollRestore(anchor);
    }
    private void GoToday()
    {
        scroll.StopAnimation();
        idleReturn.Cancel();
        ScheduleScrollRestore(new ScrollAnchor(currentMonth, 0));
    }
    private double TodayOffset()
    {
        double offset = 0;
        foreach (MonthView month in months.Children)
        {
            if (month.Month >= currentMonth) break;
            offset += month.Height;
        }
        return Math.Clamp(offset, 0, Math.Max(0, scroll.ScrollableHeight));
    }
    private void ConfigureIdleReturn()
    {
        var delay = Math.Clamp(state.Preferences.AutoReturnToTodayDelaySeconds, 1, 3600);
        var enabled = state.Preferences.AutoReturnToTodayEnabled;
        if (delay == autoReturnDelaySeconds && enabled == autoReturnEnabled) return;
        autoReturnDelaySeconds = delay;
        autoReturnEnabled = enabled;
        idleReturn = new CalendarIdleReturn(TimeSpan.FromSeconds(delay));
        returnWasBlocked = false;
        if (scroll.IsAutomaticScrolling) scroll.StopAnimation();
        if (initialized && enabled && Math.Abs(scroll.VerticalOffset - TodayOffset()) > 1)
            idleReturn.RecordScroll(activityClock.Elapsed);
    }
    private void RegisterCalendarActivity(bool userScroll = false)
    {
        if (!initialized || exiting) return;
        lastUserInput = activityClock.Elapsed;
        bool interruptedReturn = scroll.IsAutomaticScrolling;
        if (interruptedReturn) scroll.StopAnimation();
        if (autoReturnEnabled && (userScroll || interruptedReturn)) idleReturn.RecordScroll(lastUserInput);
        else idleReturn.RecordActivity(lastUserInput);
    }
    private void OnHoverViewingChanged(Window owner)
    {
        if (owner != this || scroll.IsAutomaticScrolling) return;
        // In particular, closing a tooltip starts a full new waiting period.
        idleReturn.RecordActivity(activityClock.Elapsed);
    }
    private void CheckIdleReturn()
    {
        if (!initialized || !autoReturnEnabled || !IsVisible || WindowState == WindowState.Minimized) return;
        bool viewingOrInteracting = refreshing || scroll.IsAnimating || DateDot.IsViewingDetails(this)
            || IsMouseCaptureWithin || (IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed)
            || OwnedWindows.Cast<Window>().Any(window => window.IsVisible && window.WindowState != WindowState.Minimized)
            || tray.ContextMenuStrip?.Visible == true;
        if (viewingOrInteracting)
        {
            returnWasBlocked = true;
            idleReturn.TryBeginReturn(activityClock.Elapsed, true);
            return;
        }
        if (returnWasBlocked)
        {
            // Closing/minimizing an owned detail window may not route an input
            // event through this window; wait a full interval after viewing ends.
            returnWasBlocked = false;
            idleReturn.RecordActivity(activityClock.Elapsed);
            return;
        }
        if (!idleReturn.TryBeginReturn(activityClock.Elapsed, viewingOrInteracting)) return;
        var destination = TodayOffset();
        var distance = Math.Abs(destination - scroll.VerticalOffset);
        if (distance < 1) return;
        var pages = distance / Math.Max(1, scroll.ViewportHeight);
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(700 + 250 * Math.Sqrt(pages), 800, 1600));
        scroll.ScrollToSmooth(destination, duration);
    }
    private void AppendKnownFuture()
    {
        if (state.Preferences.LimitCalendarToOneYear) return;
        // The initial future range is twelve months. Keep the next biennial edition reachable.
        if (state.Projection.Occurrences.Count == 0) return;
        var lastDate = state.Projection.Occurrences.Max(x => x.DeadlineUtc).InZone(state.DisplayZone).Date;
        if (lastDate < endMonth) return;
        var anchor = CaptureScrollAnchor();
        refreshing = true;
        endMonth = endMonth.PlusMonths(12);
        RebuildMonths();
        ScheduleScrollRestore(anchor);
    }
    private void UpdateHeadline()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var selection = HeaderSelection.Select(state.Projection, state.Preferences, now);
        var next = selection.Next;
        // Even while the header is pinned, other conferences must refresh their
        // forecasts as their last published deadline passes.
        nextBoundary = selection.NextBoundaryUtc;
        if (next is null && selection.PinnedSeriesKey is { } pinnedKey)
        {
            headline.Text = state.Catalog.Series.FirstOrDefault(series => series.Key == pinnedKey)?.Title
                ?? state.Projection.Occurrences.FirstOrDefault(item => item.SeriesKey == pinnedKey)?.Title
                ?? pinnedKey;
            headline.Foreground = new SolidColorBrush(state.ConferenceColor(pinnedKey));
            deadline.Text = state.T("暂无未来截止", "No upcoming deadline");
            remaining.Text = selection.PendingCount > 0 ? state.T($"{selection.PendingCount} 项日期待定／待核验", $"{selection.PendingCount} dates pending / under review") : "";
        }
        else if (next is null)
        {
            headline.Text = state.Preferences.Followed.Count == 0 ? state.T("关注你的下一站", "Your next chapter") : state.T("暂无未来截止", "No upcoming deadline");
            headline.Foreground = state.Theme.Brush("Foreground");
            deadline.Text = state.Preferences.Followed.Count == 0 ? state.T("在设置中选择你关注的会议", "Choose conferences in Settings") : state.T("可以查看历史，或检查待定日期", "Browse history or review pending dates");
            remaining.Text = selection.PendingCount > 0 ? state.T($"{selection.PendingCount} 项日期待定／待核验", $"{selection.PendingCount} dates pending / under review") : "";
        }
        else
        {
            headline.Text = $"{next.Title} {next.EditionYear}" + (next.Kind == DeadlineKind.Estimated ? state.T(" · 预计", " · Est.") : "");
            headline.Foreground = new SolidColorBrush(state.ConferenceColor(next.SeriesKey));
            deadline.Text = next.DeadlineUtc.InZone(state.DisplayZone).LocalDateTime.ToDateTimeUnspecified().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            remaining.Text = DateDot.Countdown(state,next.DeadlineUtc,next.Kind == DeadlineKind.Estimated,showPrefix: false);
        }
    }
    private void Tick()
    {
        if (!IsVisible) return;
        var now = SystemClock.Instance.GetCurrentInstant();
        if (now.InZone(state.DisplayZone).Date != today || nextBoundary is { } boundary && now >= boundary) { state.Refresh(); return; }
        UpdateHeadline(); DateDot.Tick(); CheckIdleReturn();
    }
    public void ShowSettings()
    {
        RegisterCalendarActivity();
        if (settings != null) { settings.Show(); settings.Activate(); return; }
        settings = new SettingsWindow(state) { Owner = this };
        settings.Closed += (_, _) => { settings = null; RegisterCalendarActivity(); };
        settings.Show(); settings.Activate();
    }
    public void Restore()
    {
        Show(); WindowState = WindowState.Normal; ClampToScreen(); state.Refresh(); Activate();
    }
    private void UpdateTray()
    {
        var previous = tray.ContextMenuStrip;
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) => RegisterCalendarActivity();
        menu.Closed += (_, _) => RegisterCalendarActivity();
        menu.Items.Add(state.T("显示日历", "Show calendar"), null, (_, _) => Dispatcher.BeginInvoke(Restore));
        menu.Items.Add(state.T("隐藏日历", "Hide calendar"), null, (_, _) => Dispatcher.BeginInvoke(Hide));
        menu.Items.Add(state.T("回到今天", "Return to Today"), null, (_, _) => Dispatcher.BeginInvoke(() => { Restore(); GoToday(); }));
        menu.Items.Add(state.T("设置", "Settings"), null, (_, _) => Dispatcher.BeginInvoke(ShowSettings));
        menu.Items.Add(state.T("立即更新", "Refresh now"), null, (_, _) => Dispatcher.BeginInvoke(async () => await state.RefreshAsync()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(state.T("退出", "Exit"), null, (_, _) => Dispatcher.BeginInvoke(Exit));
        tray.ContextMenuStrip = menu; previous?.Dispose();
    }
    private void Exit()
    {
        exiting = true; SaveWindowBounds(); clock.Stop(); saveTimer.Stop();
        state.Changed -= Rebuild;
        state.WindowSizeRequested -= ApplyRequestedSize;
        DateDot.HoverViewingChanged -= OnHoverViewingChanged;
        idleReturn.Cancel(); scroll.StopAnimation();
        settings?.Close(); tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose();
        Application.Current.Shutdown();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!exiting) { e.Cancel = true; SaveWindowBounds(); Hide(); }
        base.OnClosing(e);
    }
    private void ScheduleSave() { if (!initialized) return; saveTimer.Stop(); saveTimer.Start(); }
    private void SaveWindowBounds()
    {
        PublishWindowBounds(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        state.SaveBounds();
    }
    private void PublishWindowBounds(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        bool previous = applyingBounds;
        applyingBounds = true;
        try
        {
            state.UpdateWindowBounds(new WindowBounds { Left = Left, Top = Top, Width = width, Height = height });
        }
        finally { applyingBounds = previous; }
    }
    private void ApplyRequestedSize(double width, double height)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ApplyRequestedSize(width, height)); return; }
        if (exiting || applyingBounds) return;
        ApplyWindowBounds(width, height, Left, Top, MonitorWorkArea(), clampPosition: true);
        // An explicit settings submission is durable before the editor reports success.
        // Continuous edge dragging and later native size corrections remain debounced.
        state.SaveBounds();
    }
    private Rect MonitorWorkArea()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var monitor = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var area = monitor.WorkingArea;
        return new Rect(area.Left / dpi.DpiScaleX,area.Top / dpi.DpiScaleY,area.Width / dpi.DpiScaleX,area.Height / dpi.DpiScaleY);
    }
    private void ApplyWindowBounds(double width, double height, double left, double top, Rect working, bool clampPosition)
    {
        if (exiting || applyingBounds) return;
        applyingBounds = true;
        try
        {
            MinHeight = Math.Min(480,working.Height * .8); MinWidth = Math.Min(240,working.Width);
            Width = Math.Clamp(width,MinWidth,working.Width);
            Height = Math.Clamp(height,MinHeight,working.Height);
            Left = clampPosition ? Math.Clamp(left,working.Left,Math.Max(working.Left,working.Right - Width)) : left;
            Top = clampPosition ? Math.Clamp(top,working.Top,Math.Max(working.Top,working.Bottom - Height)) : top;
            // Width/Height already contain the accepted request; ActualWidth/ActualHeight can
            // still describe the previous layout until WPF raises SizeChanged.
            PublishWindowBounds(Width,Height);
        }
        finally { applyingBounds = false; }
        ScheduleSave();
        if (boundsCaptureQueued) return;
        boundsCaptureQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            boundsCaptureQueued = false;
            if (exiting || !initialized) return;
            PublishWindowBounds(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
            ScheduleSave();
        });
    }
    private void ClampToScreen()
    {
        ApplyWindowBounds(Width,Height,Left,Top,MonitorWorkArea(),clampPosition: true);
    }
    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x007E || msg == 0x02E0) Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,ClampToScreen);
        return IntPtr.Zero;
    }
    private void AddResizeHandles(Grid outer)
    {
        foreach (var direction in new[] { "L", "R", "T", "B", "LT", "RT", "LB", "RB" })
        {
            bool left = direction.Contains('L'), right = direction.Contains('R'), top = direction.Contains('T'), bottom = direction.Contains('B');
            var thumb = new Thumb { Background = Brushes.Transparent, HorizontalAlignment = left ? HorizontalAlignment.Left : right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch, VerticalAlignment = top ? VerticalAlignment.Top : bottom ? VerticalAlignment.Bottom : VerticalAlignment.Stretch, Cursor = direction is "LT" or "RB" ? Cursors.SizeNWSE : direction is "RT" or "LB" ? Cursors.SizeNESW : left || right ? Cursors.SizeWE : Cursors.SizeNS };
            if (left || right) thumb.Width = top || bottom ? 10 : 5;
            if (top || bottom) thumb.Height = left || right ? 10 : 5;
            var factory = new FrameworkElementFactory(typeof(Border)); factory.SetValue(Border.BackgroundProperty,Brushes.Transparent);
            thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = factory };
            thumb.DragDelta += (_, e) =>
            {
                var working = MonitorWorkArea();
                var minimumWidth = Math.Min(240,working.Width);
                var minimumHeight = Math.Min(480,working.Height * .8);
                double width = Width, height = Height, x = Left, y = Top;
                if (left) { width = Math.Clamp(Width - e.HorizontalChange,minimumWidth,working.Width); x += Width - width; }
                if (right) width = Math.Clamp(Width + e.HorizontalChange,minimumWidth,working.Width);
                if (top) { height = Math.Clamp(Height - e.VerticalChange,minimumHeight,working.Height); y += Height - height; }
                if (bottom) height = Math.Clamp(Height + e.VerticalChange,minimumHeight,working.Height);
                ApplyWindowBounds(width,height,x,y,working,clampPosition: false);
            };
            outer.Children.Add(thumb);
        }
    }
}
