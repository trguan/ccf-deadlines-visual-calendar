using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NodaTime;
using DeadlineCalendar.Core;
using Forms = System.Windows.Forms;

namespace DeadlineCalendar.App;

public sealed class DateDot : Control
{
    private static readonly ControlTemplate TooltipTemplate = CreateTooltipTemplate();
    private readonly AppState state;
    private readonly LocalDate date;
    private readonly List<DeadlineOccurrence> events;
    private readonly ToolTip popup = new()
    {
        Opacity = 1, MaxWidth = 430, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
        Padding = new Thickness(0), HasDropShadow = false, OverridesDefaultStyle = true, Template = TooltipTemplate
    };
    private readonly List<(TextBlock Text, DeadlineOccurrence Occurrence)> countdowns = [];
    private Window? hoverOwner;
    private static DateDot? active;
    public static event Action<Window>? HoverViewingChanged;
    public static readonly DependencyProperty HoverScaleProperty = DependencyProperty.Register(nameof(HoverScale), typeof(double), typeof(DateDot), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DotDiameterProperty = DependencyProperty.Register(nameof(DotDiameter), typeof(double), typeof(DateDot), new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender), value => value is double diameter && double.IsFinite(diameter) && diameter > 0);
    public double HoverScale { get => (double)GetValue(HoverScaleProperty); set => SetValue(HoverScaleProperty, value); }
    public double DotDiameter { get => (double)GetValue(DotDiameterProperty); set => SetValue(DotDiameterProperty, value); }
    public DateDot(AppState app, LocalDate day, IEnumerable<DeadlineOccurrence> occurrences)
    {
        state = app; date = day; events = occurrences.OrderBy(x => app.Priority(x.SeriesKey)).ThenBy(x => x.DeadlineUtc).ToList();
        Focusable = true;
        Cursor = events.Count > 0 ? Cursors.Hand : Cursors.Arrow;
        popup.PlacementTarget = this;
        ToolTip = popup;
        ToolTipService.SetInitialShowDelay(this, 180);
        ToolTipService.SetShowDuration(this, int.MaxValue);
        ToolTipService.SetHasDropShadow(this, false);
        AutomationProperties.SetName(this, date + (events.Count > 0 ? " " + string.Join(", ", events.Select(x => x.Title + " " + x.EditionYear)) : ""));
        popup.Opened += (_, _) =>
        {
            var owner = Window.GetWindow(this);
            if (owner is MainWindow { IsReturningToToday: true })
            {
                popup.IsOpen = false;
                return;
            }
            active = this;
            hoverOwner = owner;
            RefreshTooltip();
            if (owner is not null) HoverViewingChanged?.Invoke(owner);
        };
        popup.Closed += (_, _) => EndHoverViewing();
        MouseEnter += (_, _) => Animate(1.18);
        MouseLeave += (_, _) => Animate(1);
        GotKeyboardFocus += (_, _) => { Animate(1.18); popup.IsOpen = true; };
        LostKeyboardFocus += (_, _) => { Animate(1); popup.IsOpen = false; };
        MouseLeftButtonUp += (_, e) =>
        {
            if (events.Count == 0) return;
            e.Handled = true;
            OpenDetails();
        };
        KeyDown += (_, e) =>
        {
            if (events.Count == 0 || e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            OpenDetails();
        };
        Unloaded += (_, _) => { popup.IsOpen = false; EndHoverViewing(); };
    }
    public static bool IsViewingDetails(Window owner) => active is { } viewing && viewing.popup.IsOpen && Window.GetWindow(viewing) == owner;
    private void EndHoverViewing()
    {
        var owner = hoverOwner;
        hoverOwner = null;
        if (active == this) active = null;
        countdowns.Clear();
        // Closed can run during Unloaded; clearing the recorded owner first avoids
        // duplicate notifications while retaining it after visual-tree detachment.
        if (owner is not null) HoverViewingChanged?.Invoke(owner);
    }
    private static ControlTemplate CreateTooltipTemplate()
    {
        // Render only our content so the platform ToolTip chrome cannot leave an
        // opaque background or shadow behind the independently transparent surface.
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        return new ControlTemplate(typeof(ToolTip)) { VisualTree = presenter };
    }
    private void Animate(double target) => BeginAnimation(HoverScaleProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var now = SystemClock.Instance.GetCurrentInstant();
        var today = now.InZone(state.DisplayZone).Date;
        double r = DotDiameter / 2 * HoverScale;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var actual = events.FirstOrDefault(x => x.Kind == DeadlineKind.Published);
        var estimated = events.FirstOrDefault(x => x.Kind == DeadlineKind.Estimated);
        var visibleRadius = r;
        if (date == today)
        {
            // The today ring stays inside its row even during the hover animation.
            var stroke = Math.Clamp(DotDiameter * .15, 2, 3.2);
            var maximumRadius = Math.Max(1, (Math.Min(ActualWidth, ActualHeight) - stroke) / 2 - .6);
            var ringRadius = Math.Min(r + Math.Max(1.5, DotDiameter * .13), maximumRadius);
            var gap = Math.Max(1, DotDiameter * .08);
            var fillRadius = Math.Min(r, Math.Max(1, ringRadius - stroke / 2 - gap));
            var representative = actual ?? estimated;
            var color = representative is null ? state.Theme.Color("Accent") : state.ConferenceColor(representative.SeriesKey);
            var brush = new SolidColorBrush(color) { Opacity = representative is not null && representative.DeadlineUtc <= now ? state.Preferences.PastOpacity : 1 };
            if (actual is not null && estimated is not null)
            {
                // Retain both deadline colors when a mixed day is also today.
                var estimateStroke = Math.Clamp(DotDiameter * .08, 1, 1.6);
                var estimateRadius = Math.Max(.5, fillRadius - estimateStroke / 2);
                dc.DrawEllipse(brush, null, center, estimateRadius * .6, estimateRadius * .6);
                var estimateBrush = new SolidColorBrush(state.ConferenceColor(estimated.SeriesKey)) { Opacity = estimated.DeadlineUtc <= now ? state.Preferences.PastOpacity : 1 };
                dc.DrawEllipse(null, new Pen(estimateBrush, estimateStroke), center, estimateRadius, estimateRadius);
            }
            else
            {
                // A small center distinguishes an estimated-only today from a published date.
                var innerRadius = actual is null && estimated is not null ? fillRadius * .38 : fillRadius;
                dc.DrawEllipse(brush, null, center, innerRadius, innerRadius);
            }
            dc.DrawEllipse(null, new Pen(brush, stroke), center, ringRadius, ringRadius);
            visibleRadius = ringRadius + stroke / 2;
        }
        else if (events.Count == 0)
        {
            var brush = state.Theme.Brush(date < today ? "Past" : "Accent");
            brush.Opacity = date < today ? state.Preferences.PastOpacity : 1;
            dc.DrawEllipse(brush, null, center, r, r);
        }
        else
        {
            if (actual != null)
            {
                var fill = new SolidColorBrush(state.ConferenceColor(actual.SeriesKey)) { Opacity = actual.DeadlineUtc <= now ? state.Preferences.PastOpacity : 1 };
                dc.DrawEllipse(fill, null, center, estimated == null ? r : r * .68, estimated == null ? r : r * .68);
            }
            if (estimated != null)
            {
                var brush = new SolidColorBrush(state.ConferenceColor(estimated.SeriesKey)) { Opacity = estimated.DeadlineUtc <= now ? state.Preferences.PastOpacity : 1 };
                dc.DrawEllipse(null, new Pen(brush, Math.Max(1.2, r * .22)), center, r, r);
            }
        }
        if (events.Count > 1)
        {
            var badge = state.TextStyle("Badge");
            var typeface = new Typeface(state.TextFont("Badge"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var text = new FormattedText(events.Count.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, badge.FontSize, state.Theme.Brush("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(center.X + visibleRadius + 2, Math.Max(0, center.Y - visibleRadius - 3)));
        }
    }
    public static void Tick() { active?.UpdateCountdowns(); }
    public static string Countdown(AppState app, Instant deadline, bool estimated = false, bool showPrefix = true)
    {
        var remaining = (deadline - SystemClock.Instance.GetCurrentInstant()).ToTimeSpan();
        if (remaining <= TimeSpan.Zero) return app.T(estimated ? "预计日期已过" : "已截止", estimated ? "Estimated date passed" : "Deadline passed");
        var prefix = showPrefix ? app.T(estimated ? "预计剩余 " : "剩余 ", estimated ? "Estimated · " : "Remaining · ") : "";
        return prefix + $"{(int)remaining.TotalDays}{app.T("天", "d")} {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }
    public static string FormatInstant(AppState app, Instant instant)
    {
        var zoned = instant.InZone(app.DisplayZone);
        var offset = zoned.Offset.ToTimeSpan();
        var sign = offset < TimeSpan.Zero ? "−" : "+";
        return zoned.LocalDateTime.ToDateTimeUnspecified().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + $"  UTC{sign}{Math.Abs((int)offset.TotalHours):00}:{Math.Abs(offset.Minutes):00}";
    }
    private static string FormatHoverInstant(AppState app, Instant instant)
    {
        var zoned = instant.InZone(app.DisplayZone);
        var offset = zoned.Offset.ToTimeSpan();
        var sign = offset < TimeSpan.Zero ? "−" : "+";
        return zoned.LocalDateTime.ToDateTimeUnspecified().ToString("yyyy-MM-dd, ddd HH:mm:ss", CultureInfo.InvariantCulture)
            + $"  UTC{sign}{Math.Abs((int)offset.TotalHours):00}:{Math.Abs(offset.Minutes):00}";
    }
    private void RefreshTooltip()
    {
        countdowns.Clear();
        var content = new StackPanel();
        var titleStyle = state.TextStyle("TooltipTitle");
        var bodyStyle = state.TextStyle("TooltipBody");
        content.Children.Add(new TextBlock { Text = date.ToDateTimeUnspecified().ToString("yyyy-MM-dd, ddd", CultureInfo.InvariantCulture), FontFamily = state.TextFont("TooltipTitle"), FontSize = titleStyle.FontSize, FontWeight = FontWeights.SemiBold, Foreground = state.Theme.Brush("Foreground"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,6) });
        if (events.Count > 1)
        {
            content.Children.Add(new TextBlock
            {
                Text = state.T($"{events.Count} 项截止", $"{events.Count} deadlines"),
                FontFamily = state.TextFont("TooltipBody"), FontSize = bodyStyle.FontSize, Foreground = state.Theme.Brush("Accent"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0,4,0,2)
            });
        }
        foreach (var occurrence in events.Take(4))
        {
            var group = new StackPanel { Margin = new Thickness(0,10,0,2) };
            group.Children.Add(new TextBlock { Text = $"{occurrence.Title} {occurrence.EditionYear}" + (occurrence.Kind == DeadlineKind.Estimated ? state.T(" · 预计", " · Estimated") : ""), Foreground = new SolidColorBrush(state.ConferenceColor(occurrence.SeriesKey)), FontFamily = state.TextFont("TooltipTitle"), FontWeight = FontWeights.SemiBold, FontSize = titleStyle.FontSize, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(occurrence.Comment)) group.Children.Add(Line(occurrence.Comment));
            group.Children.Add(Line(FormatHoverInstant(state, occurrence.DeadlineUtc)));
            var countdown = Line(Countdown(state, occurrence.DeadlineUtc, occurrence.Kind == DeadlineKind.Estimated));
            countdowns.Add((countdown, occurrence));
            group.Children.Add(countdown);
            if (occurrence.Prediction is { } basis)
                group.Children.Add(Line(state.T("参考 ", "Based on ") + $"{occurrence.Title} {basis.EditionYear}" +
                    (string.IsNullOrWhiteSpace(basis.Comment) ? "" : " · " + basis.Comment)));
            if (occurrence.NeedsReview) group.Children.Add(Line(state.T("新轮次／名称变化，请检查筛选", "New or changed round — review selection")));
            content.Children.Add(group);
        }
        var workingWidth = SystemParameters.WorkArea.Width;
        var workingHeight = SystemParameters.WorkArea.Height;
        if (Window.GetWindow(this) is { } owner)
        {
            var workingArea = Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(owner);
            workingWidth = workingArea.Width / dpi.DpiScaleX;
            workingHeight = workingArea.Height / dpi.DpiScaleY;
        }
        popup.MaxWidth = Math.Min(430, Math.Max(64, workingWidth - 32));
        popup.MaxHeight = Math.Max(64, workingHeight - 32);
        var opacity = double.IsFinite(state.Preferences.TooltipBackgroundOpacity)
            ? Math.Clamp(state.Preferences.TooltipBackgroundOpacity, 0, 1) : 1;
        var background = state.Theme.Brush("TooltipBackground");
        var border = state.Theme.Brush("Separator");
        background.Opacity = opacity;
        border.Opacity = opacity;
        popup.Content = new Border
        {
            Background = background, BorderBrush = border,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16),
            Child = new ScrollViewer
            {
                Content = content, MaxHeight = Math.Max(32, popup.MaxHeight - 34),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };
    }
    private void UpdateCountdowns()
    {
        foreach (var item in countdowns) item.Text.Text = Countdown(state, item.Occurrence.DeadlineUtc, item.Occurrence.Kind == DeadlineKind.Estimated);
    }
    private void OpenDetails()
    {
        popup.IsOpen = false;
        DayDetailsWindow.ShowFor(state, date, Window.GetWindow(this) ?? Application.Current.MainWindow);
    }
    private TextBlock Line(string text) => new() { Text = text, Foreground = state.Theme.Brush("Muted"), FontFamily = state.TextFont("TooltipBody"), FontSize = state.TextStyle("TooltipBody").FontSize, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,3,0,0) };
}
