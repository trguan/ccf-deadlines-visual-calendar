using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeadlineCalendar.Core;
using NodaTime;
using Forms = System.Windows.Forms;

namespace DeadlineCalendar.App;

/// <summary>Interactive details for one local calendar date in the currently selected display zone.</summary>
public sealed class DayDetailsWindow : Window
{
    private readonly AppState state;
    private readonly ScrollViewer scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        PanningMode = PanningMode.VerticalOnly
    };
    private readonly List<(TextBlock Text, DeadlineOccurrence Occurrence)> countdowns = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool closed;

    public LocalDate SelectedDate { get; }

    public DayDetailsWindow(AppState app, LocalDate selectedDate)
    {
        state = app;
        Icon = AppIcon.WindowImage;
        SelectedDate = selectedDate;
        Width = 540;
        Height = Math.Min(660, SystemParameters.WorkArea.Height * .85);
        MinWidth = Math.Min(360, SystemParameters.WorkArea.Width * .8);
        MinHeight = Math.Min(280, SystemParameters.WorkArea.Height * .6);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Opacity = 1;
        UseLayoutRounding = true;
        Content = scroll;
        Rebuild();
        state.Changed += OnAppStateChanged;
        timer.Tick += (_, _) => { if (IsVisible) UpdateCountdowns(); };
        timer.Start();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Loaded += (_, _) => ClampToScreen();
        Closed += (_, _) =>
        {
            closed = true;
            timer.Stop();
            state.Changed -= OnAppStateChanged;
            countdowns.Clear();
        };
    }

    internal static void ShowFor(AppState state, LocalDate date, Window? owner)
    {
        var existing = Application.Current.Windows.OfType<DayDetailsWindow>().FirstOrDefault(window => !window.closed && ReferenceEquals(window.state, state) && window.SelectedDate == date);
        if (existing is not null)
        {
            existing.Show();
            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var details = new DayDetailsWindow(state, date);
        if (owner is not null) details.Owner = owner;
        details.Show();
        details.Activate();
    }

    private void OnAppStateChanged()
    {
        if (closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnAppStateChanged); return; }
        Rebuild();
    }

    private void Rebuild()
    {
        var previousOffset = scroll.VerticalOffset;
        countdowns.Clear();
        Topmost = state.Preferences.AlwaysOnTop;
        Background = state.Theme.Brush("Background");
        Foreground = state.Theme.Brush("Foreground");
        var titleStyle = state.TextStyle("DetailTitle");
        var bodyStyle = state.TextStyle("DetailBody");
        FontFamily = state.TextFont("DetailBody");
        FontSize = bodyStyle.FontSize;
        var culture = CultureInfo.GetCultureInfo(state.Preferences.Language == "zh-CN" ? "zh-CN" : "en-US");
        var dateLabel = SelectedDate.ToDateTimeUnspecified().ToString("D", culture);
        Title = state.T("每日截止详情 · ", "Daily deadline details · ") + dateLabel;
        AutomationProperties.SetName(this, Title);
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock
        {
            Text = dateLabel, FontFamily = state.TextFont("DetailTitle"), FontSize = titleStyle.FontSize, FontWeight = FontWeights.SemiBold,
            Foreground = state.Theme.Brush("Foreground"), TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(Line(state.DisplayZone.Id));
        var occurrences = state.Projection.Occurrences
            .Where(occurrence => occurrence.DeadlineUtc.InZone(state.DisplayZone).Date == SelectedDate)
            .OrderBy(occurrence => state.Priority(occurrence.SeriesKey)).ThenBy(occurrence => occurrence.DeadlineUtc).ToList();
        content.Children.Add(Line(state.T($"{occurrences.Count} 项关注会议截止", $"{occurrences.Count} deadlines from followed conferences")));
        if (occurrences.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = state.T("当前显示时区下，这一天没有关注会议的截止时间。更改时区后，截止时间可能移到相邻日期。", "There are no followed deadlines on this date in the current display time zone. Changing the time zone can move deadlines to a neighboring date."),
                Foreground = state.Theme.Brush("Muted"), TextWrapping = TextWrapping.Wrap,
                FontFamily = state.TextFont("DetailBody"), FontSize = bodyStyle.FontSize, Margin = new Thickness(0,24,0,0)
            });
        }
        foreach (var occurrence in occurrences)
        {
            var group = new StackPanel();
            group.Children.Add(new TextBlock
            {
                Text = $"{occurrence.Title} {occurrence.EditionYear}" + (occurrence.Kind == DeadlineKind.Estimated ? state.T(" · 预计", " · Estimated") : state.T(" · 已公布", " · Published")),
                FontFamily = state.TextFont("DetailTitle"), FontSize = titleStyle.FontSize, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(state.ConferenceColor(occurrence.SeriesKey))
            });
            if (!string.IsNullOrWhiteSpace(occurrence.Comment)) group.Children.Add(Line(occurrence.Comment));
            group.Children.Add(new TextBlock
            {
                Text = DateDot.FormatInstant(state, occurrence.DeadlineUtc), FontFamily = state.TextFont("DetailBody"), FontSize = bodyStyle.FontSize,
                Foreground = state.Theme.Brush("Foreground"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0,10,0,0)
            });
            var countdown = Line(DateDot.Countdown(state, occurrence.DeadlineUtc, occurrence.Kind == DeadlineKind.Estimated));
            countdowns.Add((countdown, occurrence));
            group.Children.Add(countdown);
            if (occurrence.Prediction is { } basis)
            {
                group.Children.Add(Line(state.T("推测依据：", "Estimate based on: ") + $"{basis.EditionYear} · {basis.DeadlineText} {basis.TimeZone}"));
                if (!string.IsNullOrWhiteSpace(basis.Comment)) group.Children.Add(Line(state.T("参考轮次：", "Source round: ") + basis.Comment));
                group.Children.Add(Line(state.T("此日期为历史推测，请以会议正式公布为准。", "This date is estimated from a previous edition; confirm against the official announcement.")));
            }
            else if (!string.IsNullOrWhiteSpace(occurrence.SourceLocalTimeText))
            {
                group.Children.Add(Line(state.T("来源时间：", "Source time: ") + occurrence.SourceLocalTimeText + " " + occurrence.SourceTimeZone));
            }
            if (occurrence.NeedsReview) group.Children.Add(Line(state.T("新轮次／名称变化，请在设置中检查筛选。", "New or changed round — review its selection in Settings.")));
            content.Children.Add(new Border
            {
                Child = group, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                BorderBrush = state.Theme.Brush("Separator"), Padding = new Thickness(16), Margin = new Thickness(0,18,0,0)
            });
        }
        scroll.Content = content;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (!closed) scroll.ScrollToVerticalOffset(previousOffset); });
    }

    private TextBlock Line(string text) => new()
    {
        Text = text, FontFamily = state.TextFont("DetailBody"), FontSize = state.TextStyle("DetailBody").FontSize, Foreground = state.Theme.Brush("Muted"), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0,6,0,0)
    };

    private void UpdateCountdowns()
    {
        foreach (var item in countdowns) item.Text.Text = DateDot.Countdown(state, item.Occurrence.DeadlineUtc, item.Occurrence.Kind == DeadlineKind.Estimated);
    }

    private void ClampToScreen()
    {
        var monitor = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = monitor.WorkingArea;
        var working = new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
        MinWidth = Math.Min(MinWidth, working.Width);
        MinHeight = Math.Min(MinHeight, working.Height);
        Width = Math.Min(Width, working.Width);
        Height = Math.Min(Height, working.Height);
        Left = Math.Clamp(Left, working.Left, Math.Max(working.Left, working.Right - Width));
        Top = Math.Clamp(Top, working.Top, Math.Max(working.Top, working.Bottom - Height));
    }
}
