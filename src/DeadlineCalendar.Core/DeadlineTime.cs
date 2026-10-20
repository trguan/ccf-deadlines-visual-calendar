using System.Text.RegularExpressions;
using NodaTime;
using NodaTime.Text;

namespace DeadlineCalendar.Core;

public static class DeadlineTime
{
    internal static readonly LocalDateTimePattern LocalPattern = LocalDateTimePattern.CreateWithInvariantCulture("uuuu-MM-dd HH:mm:ss");

    public static DateTimeZone? ResolveZone(string zone)
    {
        var name = zone?.Trim() ?? "";
        if (name.Equals("AoE", StringComparison.OrdinalIgnoreCase)) return DateTimeZone.ForOffset(Offset.FromHours(-12));
        if (name.Equals("PT", StringComparison.OrdinalIgnoreCase)) return DateTimeZoneProviders.Tzdb["America/Los_Angeles"];
        if (name.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return DateTimeZone.Utc;
        var match = Regex.Match(name, @"^UTC([+-])(\d{1,2})(?::([0-5]\d))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            var hours = int.Parse(match.Groups[2].Value);
            var minutes = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
            if (hours > 14 || (hours == 14 && minutes != 0)) return null;
            var seconds = (hours * 60 + minutes) * 60 * (match.Groups[1].Value == "-" ? -1 : 1);
            return DateTimeZone.ForOffset(Offset.FromSeconds(seconds));
        }
        return DateTimeZoneProviders.Tzdb.GetZoneOrNull(name);
    }

    public static bool TryParse(string local, string zone, out Instant instant, out string error)
    {
        instant = default;
        if (IsTbd(local)) { error = "Deadline is TBD."; return false; }
        var parsed = LocalPattern.Parse(local?.Trim() ?? "");
        if (!parsed.Success) { error = "Invalid local date or time."; return false; }
        var resolved = ResolveZone(zone);
        if (resolved is null) { error = $"Unknown timezone: {zone}."; return false; }
        var mapping = resolved.MapLocal(parsed.Value);
        if (mapping.Count != 1)
        {
            error = mapping.Count == 0 ? "Local time does not exist during a daylight-saving transition." : "Local time is ambiguous during a daylight-saving transition.";
            return false;
        }
        instant = mapping.Single().ToInstant();
        error = "";
        return true;
    }

    internal static bool IsTbd(string? text) => string.Equals(text?.Trim(), "TBD", StringComparison.OrdinalIgnoreCase);
}
