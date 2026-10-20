using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NodaTime;
using DeadlineCalendar.Core;

namespace DeadlineCalendar.App;

public sealed class MonthView : Grid
{
    private readonly Grid layout;
    private readonly UniformGrid days;
    private double dotDiameter = 14;
    public LocalDate Month { get; }
    public int WeekRows { get; }

    public MonthView(AppState state, LocalDate month, IReadOnlyDictionary<LocalDate,List<DeadlineOccurrence>> byDate)
    {
        Month = month;
        Margin = new Thickness(10,0,10,0);
        layout = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        Children.Add(layout);
        var culture = CultureInfo.GetCultureInfo(state.Preferences.Language == "zh-CN" ? "zh-CN" : "en-US");
        var accessibleMonth = month.ToDateTimeUnspecified().ToString(state.T("yyyy 年 M 月", "MMMM yyyy"), culture);
        AutomationProperties.SetName(this, accessibleMonth);
        int offset = state.Preferences.WeekStartsMonday ? (int)month.DayOfWeek - 1 : (int)month.DayOfWeek % 7;
        var total = CalendarSystem.Iso.GetDaysInMonth(month.Year, month.Month);
        WeekRows = (offset + total + 6) / 7;
        days = new UniformGrid { Rows = WeekRows, Columns = 7 };
        for (int i = 0; i < WeekRows * 7; i++)
        {
            int dayNumber = i - offset + 1;
            if (dayNumber < 1 || dayNumber > total) { days.Children.Add(new Border()); continue; }
            var day = new LocalDate(month.Year, month.Month, dayNumber);
            days.Children.Add(new DateDot(state, day, byDate.TryGetValue(day, out var entries) ? entries : []));
        }
        layout.Children.Add(days);
        SizeChanged += (_, _) => UpdateColumnMetrics();
        SetMetrics(20, 14, 12);
    }

    public void SetMetrics(double rowPitch, double dotDiameter, double extraMonthGap)
    {
        if (!double.IsFinite(rowPitch) || rowPitch <= 0) throw new ArgumentOutOfRangeException(nameof(rowPitch));
        if (!double.IsFinite(dotDiameter) || dotDiameter <= 0) throw new ArgumentOutOfRangeException(nameof(dotDiameter));
        if (!double.IsFinite(extraMonthGap) || extraMonthGap < 0) throw new ArgumentOutOfRangeException(nameof(extraMonthGap));
        this.dotDiameter = dotDiameter;
        layout.Height = WeekRows * rowPitch;
        days.Height = layout.Height;
        Height = layout.Height + extraMonthGap;
        foreach (var dot in days.Children.OfType<DateDot>()) dot.DotDiameter = dotDiameter;
        UpdateColumnMetrics();
    }

    private void UpdateColumnMetrics()
    {
        var preferredWidth = 7 * dotDiameter * 1.9;
        layout.Width = ActualWidth > 0 ? Math.Min(ActualWidth, preferredWidth) : preferredWidth;
    }
}
