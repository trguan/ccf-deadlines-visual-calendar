using System.Globalization;
using NodaTime;

namespace DeadlineCalendar.Core;

public sealed class FollowedConference
{
    public string Key { get; set; } = "";
    public string ColorHex { get; set; } = "#60A5FA";
    public int RecurrenceYears { get; set; } = 1;
}

public sealed class RoundExclusion
{
    public string SeriesKey { get; set; } = "";
    public string EditionId { get; set; } = "";
    public string RoundKey { get; set; } = "";
    public string NormalizedComment { get; set; } = "";
    public bool IsExcluded { get; set; } = true;
}

public sealed class UserPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public string Language { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en";
    public string DisplayTimeZoneId { get; set; } = DefaultZone();
    public bool WeekStartsMonday { get; set; } = true;
    public bool LimitCalendarToOneYear { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool StartWithWindows { get; set; }
    public bool AutoReturnToTodayEnabled { get; set; } = true;
    public int AutoReturnToTodayDelaySeconds { get; set; } = 10;
    public double BackgroundOpacity { get; set; } = 0.9;
    public double ContentOpacity { get; set; } = 1;
    public double PastOpacity { get; set; } = 0.35;
    public double TooltipBackgroundOpacity { get; set; } = 1;
    public Dictionary<string, string> ColorOverrides { get; set; } = [];
    public Dictionary<string, TextStylePreference> TextStyles { get; set; } = [];
    public List<FollowedConference> Followed { get; set; } = [];
    public string? HeaderConferenceKey { get; set; }
    public List<RoundExclusion> Exclusions { get; set; } = [];
    public WindowBounds Bounds { get; set; } = new();
    public bool FirstRunCompleted { get; set; }
    private static string DefaultZone()
    {
        try { return DateTimeZoneProviders.Tzdb.GetSystemDefault().Id; }
        catch { return "UTC"; }
    }
}
