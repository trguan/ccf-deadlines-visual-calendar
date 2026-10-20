using NodaTime;

namespace DeadlineCalendar.Core;

/// <summary>A calendar browsing interval with an inclusive first month and exclusive end.</summary>
public readonly record struct CalendarMonthRange(LocalDate Start, LocalDate EndExclusive)
{
    public static CalendarMonthRange AroundToday(LocalDate today)
    {
        var month = new LocalDate(today.Year, today.Month, 1);
        // Twelve full months on each side, plus the whole current month.
        return new CalendarMonthRange(month.PlusMonths(-12), month.PlusMonths(13));
    }
}
