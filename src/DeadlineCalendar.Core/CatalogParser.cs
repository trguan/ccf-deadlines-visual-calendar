using System.Text.Json;

namespace DeadlineCalendar.Core;

public static class CatalogParser
{
    public static CatalogSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("The conference catalog must be a JSON array.");
        var snapshot = new CatalogSnapshot();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { snapshot.Diagnostics.Add("Skipped a non-object conference entry."); continue; }
            var key = Text(item, "conference_key");
            var title = Text(item, "title");
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title))
            { snapshot.Diagnostics.Add("Skipped a conference without conference_key or title."); continue; }
            if (!keys.Add(key)) throw new FormatException($"Duplicate conference identity: {key}.");
            var series = new ConferenceSeries { Key = key, Title = title, Description = Text(item, "description"), Category = Text(item, "sub") };
            if (item.TryGetProperty("rank", out var rank) && rank.ValueKind == JsonValueKind.Object) series.Rank = Text(rank, "ccf", "N");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (!item.TryGetProperty("confs", out var editions) || editions.ValueKind != JsonValueKind.Array)
            { snapshot.Diagnostics.Add($"{key}: missing editions array."); snapshot.Series.Add(series); continue; }
            foreach (var sourceEdition in editions.EnumerateArray())
            {
                if (sourceEdition.ValueKind != JsonValueKind.Object || !sourceEdition.TryGetProperty("year", out var yearValue) || yearValue.ValueKind != JsonValueKind.Number || !yearValue.TryGetInt32(out var year) || year is < 1 or > 9999)
                { snapshot.Diagnostics.Add($"{key}: skipped an edition with an invalid year."); continue; }
                var id = Text(sourceEdition, "id");
                if (string.IsNullOrWhiteSpace(id)) { snapshot.Diagnostics.Add($"{key} {year}: skipped an edition without id."); continue; }
                if (!ids.Add(id)) throw new FormatException($"{key}: duplicate edition id {id}.");
                var edition = new ConferenceEdition { Id = id, Year = year, Link = Text(sourceEdition, "link"), TimeZone = Text(sourceEdition, "timezone") };
                if (sourceEdition.TryGetProperty("timeline", out var timeline) && timeline.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var sourceRound in timeline.EnumerateArray())
                    {
                        if (sourceRound.ValueKind != JsonValueKind.Object)
                        { edition.Timeline.Add(new SubmissionRound { Index = index++, DeadlineText = "", Comment = "Invalid source entry" }); snapshot.Diagnostics.Add($"{key}/{id}: invalid timeline item."); continue; }
                        edition.Timeline.Add(new SubmissionRound { Index = index++, DeadlineText = Text(sourceRound, "deadline", ""), Comment = Text(sourceRound, "comment") });
                    }
                }
                else snapshot.Diagnostics.Add($"{key}/{id}: missing timeline array.");
                series.Editions.Add(edition);
            }
            snapshot.Series.Add(series);
        }
        if (snapshot.Series.Count == 0) throw new FormatException("The catalog contains no valid conference series.");
        return snapshot;
    }

    private static string Text(JsonElement item, string name, string fallback = "") =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
}
