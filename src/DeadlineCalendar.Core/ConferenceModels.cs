using NodaTime;

namespace DeadlineCalendar.Core;

public sealed class ConferenceSeries
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Rank { get; set; } = "N";
    public List<ConferenceEdition> Editions { get; set; } = [];
}

public sealed class ConferenceEdition
{
    public string Id { get; set; } = "";
    public int Year { get; set; }
    public string Link { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public List<SubmissionRound> Timeline { get; set; } = [];
}

public sealed class SubmissionRound
{
    public int Index { get; set; }
    public string DeadlineText { get; set; } = "TBD";
    public string Comment { get; set; } = "";
}

public sealed class CatalogSnapshot
{
    public List<ConferenceSeries> Series { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
}

public enum DeadlineKind { Published, Estimated }
public sealed record PredictionSource(string EditionId, int EditionYear, string DeadlineText, string TimeZone, string Comment);

public sealed class DeadlineOccurrence
{
    public string Id { get; set; } = "";
    public string SeriesKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string EditionId { get; set; } = "";
    public int EditionYear { get; set; }
    public string RoundKey { get; set; } = "";
    public string Comment { get; set; } = "";
    public string SourceLocalTimeText { get; set; } = "";
    public string SourceTimeZone { get; set; } = "";
    public Instant DeadlineUtc { get; set; }
    public DeadlineKind Kind { get; set; }
    public PredictionSource? Prediction { get; set; }
    public bool NeedsReview { get; set; }
}

public sealed record PendingDeadline(string SeriesKey, string Title, int EditionYear, string Comment, string Reason);
public sealed class ProjectionResult
{
    public List<DeadlineOccurrence> Occurrences { get; set; } = [];
    public List<PendingDeadline> Pending { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
}
