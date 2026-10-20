using NodaTime;

namespace DeadlineCalendar.Core;

/// <summary>Selects the header deadline while retaining the global boundary needed to refresh projections.</summary>
public static class HeaderSelection
{
    public static HeaderSelectionResult Select(ProjectionResult projection, UserPreferences preferences, Instant now)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(preferences);

        var requestedKey = preferences.HeaderConferenceKey?.Trim();
        string? pinnedKey = null;
        if (!string.IsNullOrEmpty(requestedKey))
        {
            // Preserve distinct source keys when casing differs, but accept a loosely
            // formatted preference when it can be resolved to an actual followed key.
            pinnedKey = preferences.Followed.FirstOrDefault(item => StringComparer.Ordinal.Equals(item.Key, requestedKey))?.Key
                ?? preferences.Followed.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Key, requestedKey))?.Key;
        }

        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < preferences.Followed.Count; index++)
            priorities.TryAdd(preferences.Followed[index].Key, index);

        DeadlineOccurrence? next = null;
        Instant? nextBoundary = null;
        foreach (var occurrence in projection.Occurrences)
        {
            if (occurrence.DeadlineUtc <= now) continue;
            if (nextBoundary is null || occurrence.DeadlineUtc < nextBoundary.Value)
                nextBoundary = occurrence.DeadlineUtc;

            if (pinnedKey is not null && !StringComparer.Ordinal.Equals(occurrence.SeriesKey, pinnedKey)) continue;
            if (next is null || occurrence.DeadlineUtc < next.DeadlineUtc ||
                occurrence.DeadlineUtc == next.DeadlineUtc &&
                priorities.GetValueOrDefault(occurrence.SeriesKey, int.MaxValue) < priorities.GetValueOrDefault(next.SeriesKey, int.MaxValue))
                next = occurrence;
        }

        var pendingCount = pinnedKey is null ? projection.Pending.Count
            : projection.Pending.Count(item => StringComparer.Ordinal.Equals(item.SeriesKey, pinnedKey));
        return new HeaderSelectionResult(pinnedKey, next, nextBoundary, pendingCount);
    }
}

public sealed record HeaderSelectionResult(
    string? PinnedSeriesKey,
    DeadlineOccurrence? Next,
    Instant? NextBoundaryUtc,
    int PendingCount);
