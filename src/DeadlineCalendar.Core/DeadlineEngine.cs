using NodaTime;

namespace DeadlineCalendar.Core;

public static class DeadlineEngine
{
    public static ProjectionResult Build(CatalogSnapshot catalog, UserPreferences preferences, Instant now)
    {
        var result = new ProjectionResult { Diagnostics = [.. catalog.Diagnostics] };
        var priorities = preferences.Followed.Select((followed, index) => (followed, index))
            .GroupBy(item => item.followed.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        foreach (var followed in preferences.Followed.DistinctBy(item => item.Key))
        {
            var series = catalog.Series.FirstOrDefault(item => item.Key == followed.Key);
            if (series is null) { result.Diagnostics.Add($"Followed conference is absent from the source: {followed.Key}."); continue; }
            var editions = series.Editions.OrderBy(edition => edition.Year).ToList();
            foreach (var edition in editions)
            foreach (var round in edition.Timeline)
            {
                if (DeadlineTime.TryParse(round.DeadlineText, edition.TimeZone, out var deadline, out var error))
                {
                    if (!RoundSelection.IsExcluded(series, edition, round, preferences))
                        result.Occurrences.Add(Occurrence(series, edition, round, deadline, preferences));
                }
                else if (!DeadlineTime.IsTbd(round.DeadlineText))
                    result.Diagnostics.Add($"{series.Title} {edition.Year} ({round.Comment}): {error}");
            }
            if (editions.Count == 0) continue;
            var latest = editions[^1];
            // The catalog can announce several future venues at once (for example MICCAI).
            // Project only the immediate next edition after the latest completed real edition,
            // rather than skipping a nearer TBD edition for the highest announced year.
            var completedBaseline = editions.LastOrDefault(edition => AllValid(edition) && edition.Timeline.All(round =>
                DeadlineTime.TryParse(round.DeadlineText, edition.TimeZone, out var instant, out _) && instant <= now));
            if (completedBaseline is not null)
                latest = editions.FirstOrDefault(edition => edition.Year > completedBaseline.Year) ?? completedBaseline;
            if (latest.Timeline.Count == 0)
            {
                result.Pending.Add(new PendingDeadline(series.Key, series.Title, latest.Year, "", "No submission timeline was published."));
                continue;
            }

            var interval = followed.RecurrenceYears;
            if (interval is < 0 or > 2)
            {
                interval = series.Key is "AI/iccv" or "AI/eccv" ? 2 : 1;
                result.Diagnostics.Add($"{series.Title}: invalid recurrence interval; using {interval} year(s).");
            }
            if (interval == 0)
            {
                foreach (var round in latest.Timeline)
                {
                    if (DeadlineTime.TryParse(round.DeadlineText, latest.TimeZone, out _, out _) ||
                        RoundSelection.IsExcluded(series, latest, round, preferences)) continue;
                    result.Pending.Add(new PendingDeadline(series.Key, series.Title, latest.Year, round.Comment,
                        DeadlineTime.IsTbd(round.DeadlineText) ? "Estimation is disabled for this conference." : "Published date or timezone is invalid; no estimate was substituted."));
                }
                continue;
            }

            var allTbd = latest.Timeline.All(round => DeadlineTime.IsTbd(round.DeadlineText));
            if (allTbd)
            {
                var baseline = editions.LastOrDefault(edition => edition.Year < latest.Year && AllValid(edition));
                if (baseline is null)
                    AddPendingUnknowns(result, series, latest, preferences, "No fully dated previous edition is available for estimation.");
                else
                    EstimateWholeEdition(result, series, baseline, latest, preferences);
            }
            else if (!AllValid(latest))
            {
                foreach (var targetRound in latest.Timeline)
                {
                    if (DeadlineTime.TryParse(targetRound.DeadlineText, latest.TimeZone, out _, out _)) continue;
                    if (RoundSelection.IsExcluded(series, latest, targetRound, preferences)) continue;
                    if (!DeadlineTime.IsTbd(targetRound.DeadlineText))
                    {
                        result.Pending.Add(new PendingDeadline(series.Key, series.Title, latest.Year, targetRound.Comment, "Published date or timezone is invalid; no estimate was substituted."));
                        continue;
                    }
                    var label = RoundSelection.Normalize(targetRound.Comment);
                    var baseline = RoundSelection.IsUnique(latest, label) ? editions.LastOrDefault(edition =>
                        edition.Year < latest.Year && RoundSelection.IsUnique(edition, label) &&
                        edition.Timeline.Any(round => RoundSelection.Normalize(round.Comment) == label && DeadlineTime.TryParse(round.DeadlineText, edition.TimeZone, out _, out _))) : null;
                    if (baseline is null)
                        result.Pending.Add(new PendingDeadline(series.Key, series.Title, latest.Year, targetRound.Comment, "TBD round has no unique matching historical label."));
                    else
                    {
                        var sourceRound = baseline.Timeline.Single(round => RoundSelection.Normalize(round.Comment) == label);
                        AddEstimate(result, series, baseline, sourceRound, latest, targetRound, preferences);
                    }
                }
            }
            else if (latest.Timeline.All(round => DeadlineTime.TryParse(round.DeadlineText, latest.TimeZone, out var instant, out _) && instant <= now))
            {
                if (latest.Year + interval > 9999)
                {
                    result.Diagnostics.Add($"{series.Title}: next edition year is outside the supported calendar.");
                    continue;
                }
                var target = new ConferenceEdition { Id = $"estimated:{series.Key}:{latest.Year + interval}", Year = latest.Year + interval, TimeZone = latest.TimeZone };
                EstimateWholeEdition(result, series, latest, target, preferences);
            }
        }
        result.Occurrences = result.Occurrences.OrderBy(item => item.DeadlineUtc)
            .ThenBy(item => priorities.GetValueOrDefault(item.SeriesKey, int.MaxValue))
            .ThenBy(item => item.EditionYear).ThenBy(item => item.RoundKey, StringComparer.Ordinal).ToList();
        return result;
    }

    private static bool AllValid(ConferenceEdition edition) => edition.Timeline.Count > 0 && edition.Timeline.All(round => DeadlineTime.TryParse(round.DeadlineText, edition.TimeZone, out _, out _));

    private static void AddPendingUnknowns(ProjectionResult result, ConferenceSeries series, ConferenceEdition edition, UserPreferences preferences, string reason)
    {
        foreach (var round in edition.Timeline.Where(round => !RoundSelection.IsExcluded(series, edition, round, preferences)))
            result.Pending.Add(new PendingDeadline(series.Key, series.Title, edition.Year, round.Comment, reason));
    }

    private static void EstimateWholeEdition(ProjectionResult result, ConferenceSeries series, ConferenceEdition baseline, ConferenceEdition target, UserPreferences preferences)
    {
        var placeholderChoice = target.Timeline.Count == 1
            ? RoundSelection.DirectChoice(series, target, target.Timeline[0], preferences) : null;
        if (placeholderChoice == true) return;
        // A wholly unpublished edition borrows the previous edition's complete structure.
        // Keep the real target record untouched; the projection is never a later source.
        var projected = new ConferenceEdition { Id = target.Id, Year = target.Year, TimeZone = baseline.TimeZone, Link = target.Link,
            Timeline = baseline.Timeline.Select(round => new SubmissionRound { Index = round.Index, DeadlineText = round.DeadlineText, Comment = round.Comment }).ToList() };
        foreach (var sourceRound in baseline.Timeline)
        {
            var targetRound = projected.Timeline.Single(round => round.Index == sourceRound.Index);
            var label = RoundSelection.Normalize(sourceRound.Comment);
            // A labelled TBD placeholder may itself have an exclusion in the source catalog.
            var actualPlaceholder = RoundSelection.IsUnique(target, label) ? target.Timeline.Single(round => RoundSelection.Normalize(round.Comment) == label) : null;
            var selected = actualPlaceholder is not null ? RoundSelection.Choice(series, target, actualPlaceholder, preferences) : placeholderChoice;
            if (selected == true) continue;
            AddEstimate(result, series, baseline, sourceRound, projected, targetRound, preferences, selected);
        }
    }

    private static void AddEstimate(ProjectionResult result, ConferenceSeries series, ConferenceEdition baseline, SubmissionRound sourceRound,
        ConferenceEdition target, SubmissionRound targetRound, UserPreferences preferences, bool? targetChoiceOverride = null)
    {
        var targetChoice = targetChoiceOverride ?? RoundSelection.Choice(series, target, targetRound, preferences);
        if (targetChoice ?? RoundSelection.IsExcluded(series, baseline, sourceRound, preferences)) return;
        if (!DeadlineTime.TryParse(sourceRound.DeadlineText, baseline.TimeZone, out _, out _)) return;
        try
        {
            var original = DeadlineTime.LocalPattern.Parse(sourceRound.DeadlineText.Trim()).Value;
            var shifted = original.PlusYears(target.Year - baseline.Year); // NodaTime clamps Feb 29 to Feb 28.
            var shiftedText = DeadlineTime.LocalPattern.Format(shifted);
            if (!DeadlineTime.TryParse(shiftedText, baseline.TimeZone, out var instant, out var error))
            { result.Pending.Add(new PendingDeadline(series.Key, series.Title, target.Year, targetRound.Comment, $"Estimated local time cannot be resolved: {error}")); return; }
            var occurrence = Occurrence(series, target, targetRound, instant, preferences);
            occurrence.Id += ":estimated";
            occurrence.Kind = DeadlineKind.Estimated;
            occurrence.SourceLocalTimeText = shiftedText;
            occurrence.SourceTimeZone = baseline.TimeZone;
            occurrence.Prediction = new PredictionSource(baseline.Id, baseline.Year, sourceRound.DeadlineText, baseline.TimeZone, sourceRound.Comment);
            result.Occurrences.Add(occurrence);
        }
        catch (ArgumentOutOfRangeException)
        { result.Pending.Add(new PendingDeadline(series.Key, series.Title, target.Year, targetRound.Comment, "Estimated date is outside the supported calendar.")); }
    }

    private static DeadlineOccurrence Occurrence(ConferenceSeries series, ConferenceEdition edition, SubmissionRound round, Instant deadline, UserPreferences preferences)
    {
        var roundKey = RoundSelection.Identity(edition, round);
        return new DeadlineOccurrence { Id = $"{series.Key}:{edition.Id}:{roundKey}", SeriesKey = series.Key, Title = series.Title,
            EditionId = edition.Id, EditionYear = edition.Year, RoundKey = roundKey, Comment = round.Comment,
            SourceLocalTimeText = round.DeadlineText, SourceTimeZone = edition.TimeZone, DeadlineUtc = deadline,
            Kind = DeadlineKind.Published, NeedsReview = RoundSelection.NeedsReview(series, edition, round, preferences) };
    }
}
