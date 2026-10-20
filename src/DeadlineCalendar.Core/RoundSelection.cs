using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeadlineCalendar.Core;

public static class RoundSelection
{
    public static string Identity(ConferenceEdition edition, SubmissionRound round)
    {
        var comment = Normalize(round.Comment);
        if (comment.Length > 0 && IsUnique(edition, comment)) return "comment:" + Hash(comment);
        // This identity is edition-local. Unlabelled/duplicate labels never match across years.
        return "local:" + Hash($"{edition.Id}\n{round.Index}\n{comment}\n{round.DeadlineText}");
    }

    public static bool IsExcluded(ConferenceSeries series, ConferenceEdition edition, SubmissionRound round, UserPreferences preferences)
        => Choice(series, edition, round, preferences) ?? false;

    internal static bool? Choice(ConferenceSeries series, ConferenceEdition edition, SubmissionRound round, UserPreferences preferences)
    {
        var comment = Normalize(round.Comment);
        var direct = DirectChoice(series, edition, round, preferences);
        if (direct.HasValue) return direct;
        return preferences.Exclusions.Select((choice, index) => (choice, index))
            .Where(item => item.choice.SeriesKey == series.Key && CanInherit(series, edition, comment, item.choice))
            .OrderByDescending(item => series.Editions.First(source => source.Id == item.choice.EditionId).Year)
            .ThenByDescending(item => item.index).Select(item => (bool?)item.choice.IsExcluded).FirstOrDefault();
    }

    internal static bool? DirectChoice(ConferenceSeries series, ConferenceEdition edition, SubmissionRound round, UserPreferences preferences)
    {
        var identity = Identity(edition, round);
        return preferences.Exclusions.LastOrDefault(choice => choice.SeriesKey == series.Key && choice.EditionId == edition.Id && choice.RoundKey == identity)?.IsExcluded;
    }

    public static void SetExcluded(ConferenceSeries series, ConferenceEdition edition, SubmissionRound round, UserPreferences preferences, bool excluded)
    {
        var identity = Identity(edition, round);
        var comment = Normalize(round.Comment);
        preferences.Exclusions.RemoveAll(exclusion => exclusion.SeriesKey == series.Key &&
            exclusion.EditionId == edition.Id && exclusion.RoundKey == identity);
        preferences.Exclusions.Add(new RoundExclusion { SeriesKey = series.Key, EditionId = edition.Id, RoundKey = identity,
            NormalizedComment = IsUnique(edition, comment) ? comment : "", IsExcluded = excluded });
    }

    public static bool NeedsReview(ConferenceSeries series, ConferenceEdition edition, SubmissionRound round, UserPreferences preferences)
    {
        if (Choice(series, edition, round, preferences).HasValue) return false;
        var comment = Normalize(round.Comment);
        if (IsUnique(edition, comment) && series.Editions.Any(source => source.Year < edition.Year && IsUnique(source, comment))) return false;
        // Only newly unmatched rounds need review. A later inclusion supersedes an earlier
        // exclusion for the same identifiable label, while other editions retain their choice.
        var applicable = preferences.Exclusions.Select((choice, index) => new
        {
            Choice = choice, Index = index, Source = series.Editions.FirstOrDefault(source => source.Id == choice.EditionId)
        }).Where(item => item.Choice.SeriesKey == series.Key && (item.Source is null || item.Source.Year <= edition.Year));
        var currentChoices = applicable.GroupBy(item => item.Source is not null && IsUnique(item.Source, item.Choice.NormalizedComment)
            ? "label:" + item.Choice.NormalizedComment : $"local:{item.Choice.EditionId}:{item.Choice.RoundKey}")
            .Select(group => group.OrderByDescending(item => item.Source?.Year ?? int.MinValue).ThenByDescending(item => item.Index).First());
        return currentChoices.Any(item => item.Choice.IsExcluded && !edition.Timeline.Any(candidate =>
            (item.Choice.EditionId == edition.Id && item.Choice.RoundKey == Identity(edition, candidate)) ||
            CanInherit(series, edition, Normalize(candidate.Comment), item.Choice)));
    }

    internal static string Normalize(string? comment) => Regex.Replace(comment?.Trim() ?? "", @"\s+", " ").ToUpperInvariant();
    internal static bool IsUnique(ConferenceEdition edition, string comment) => comment.Length > 0 && edition.Timeline.Count(round => Normalize(round.Comment) == comment) == 1;

    private static bool CanInherit(ConferenceSeries series, ConferenceEdition target, string comment, RoundExclusion exclusion)
    {
        if (comment.Length == 0 || exclusion.NormalizedComment != comment || !IsUnique(target, comment)) return false;
        var source = series.Editions.FirstOrDefault(edition => edition.Id == exclusion.EditionId);
        return source is not null && source.Year < target.Year && IsUnique(source, comment);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20].ToLowerInvariant();
}
