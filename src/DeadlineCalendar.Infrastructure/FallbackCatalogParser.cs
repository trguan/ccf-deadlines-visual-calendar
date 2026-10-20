using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeadlineCalendar.Core;

namespace DeadlineCalendar.Infrastructure;

/// <summary>Reads ccfddl.top's static row literals without evaluating JavaScript.</summary>
public static class FallbackCatalogParser
{
    private static readonly Regex RowStart = new(@"\brows\s*\.\s*push\s*\(\s*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> Categories = ["AI", "CG", "CT", "DB", "DS", "HI", "MX", "NW", "SC", "SE"];
    private static readonly Dictionary<string, (string Slug, string Title)> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NeurIPS"] = ("nips", "NeurIPS"), ["ACM MM"] = ("mm", "ACM MM"),
        ["ACM SIGGRAPH"] = ("sig", "ACM SIGGRAPH"), ["ACM SIGGRAPH ASIA"] = ("siga", "ACM SIGGRAPH ASIA"),
        ["ECML-PKDD"] = ("pkdd", "ECML-PKDD"), ["Euro-Par"] = ("europar", "Euro-Par"),
        ["IFIP WG 11.9"] = ("ifip119", "IFIP WG 11.9"), ["S&P"] = ("sp", "S&P"),
        ["IH&MMSec"] = ("ih&mmsec", "IH&MMSec"),
        ["USENIX Security"] = ("uss", "USENIX Security"), ["UbiComp/ISWC"] = ("ubicomp", "UbiComp/ISWC"),
        ["IEEE VIS"] = ("vis", "IEEE VIS"), ["ASP-DAC"] = ("aspdac", "ASP-DAC"),
        ["IEEE VR"] = ("vr", "IEEE VR"), ["EuroS&P"] = ("eurosp", "EuroS&P"),
        ["Eurographics"] = ("eg", "Eurographics"), ["APWeb-WAIM"] = ("apweb", "APWeb-WAIM"),
        ["IEEE/ACM CGO"] = ("cgo", "IEEE/ACM CGO"), ["Hot Chips"] = ("hotchips", "Hot Chips"),
        ["DFRWS APAC"] = ("dfrws-apac", "DFRWS APAC"), ["RuleML+RR"] = ("rulemlrr", "RuleML+RR"),
        ["IEEE CEC"] = ("cec", "IEEE CEC"), ["CEC"] = ("cec", "IEEE CEC"),
        ["SIGOPS ATC"] = ("atc", "SIGOPS ATC"), ["USENIX ATC"] = ("atc", "SIGOPS ATC"),
        ["CODES+ISSS"] = ("codesisss", "CODES+ISSS"), ["EvoApplications"] = ("evoapps", "EvoApplications"),
        ["IEEE CoG"] = ("cog", "IEEE CoG"), ["SAI"] = ("sai-computing", "SAI"),
        ["SAI Computing Conference"] = ("sai-computing", "SAI"), ["ACM TURC"] = ("acmturc", "ACM TURC"),
        ["ESEC/FSE"] = ("fse", "FSE"), ["ITS"] = ("iss", "ISS")
    };

    public static string Parse(string html, string? cachedJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);
        var rows = new List<Row>();
        foreach (var match in FindRows(html))
        {
            var fields = new LiteralReader(html, match.Index + match.Length).ReadRow();
            var title = Required(fields, "shortName");
            var category = Required(fields, "type").ToUpperInvariant();
            var rank = Required(fields, "ccfRank").ToUpperInvariant();
            var timezone = Required(fields, "timezone");
            var deadline = Required(fields, "deadline");
            var link = Required(fields, "link");
            if (!Categories.Contains(category) || rank is not ("A" or "B" or "C" or "N")
                || !int.TryParse(Required(fields, "year"), NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year is < 1 or > 9999
                || DeadlineTime.ResolveZone(timezone) is null
                || (!deadline.Equals("TBD", StringComparison.OrdinalIgnoreCase) && !DeadlineTime.TryParse(deadline, timezone, out _, out _))
                || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                throw new FormatException("The fallback catalog contains an invalid conference row.");
            var (slug, canonicalTitle) = Identity(title);
            rows.Add(new Row(category + "/" + slug, canonicalTitle, category, rank, year, timezone, deadline, link,
                fields.GetValueOrDefault("date", ""), fields.GetValueOrDefault("place", ""),
                title.Equals(canonicalTitle, StringComparison.OrdinalIgnoreCase)));
        }
        if (rows.Count == 0) throw new FormatException("The fallback page contains no conference data.");

        var incoming = new JsonArray();
        foreach (var series in rows.GroupBy(r => r.Key))
        {
            var first = series.FirstOrDefault(r => r.IsCanonical) ?? series.First();
            var editions = new JsonArray();
            foreach (var year in series.GroupBy(r => r.Year))
            {
                // Renamed conferences can appear twice. Prefer the current name's row.
                var selected = year.Any(r => r.IsCanonical) ? year.Where(r => r.IsCanonical).ToArray() : year.ToArray();
                var row = selected[0];
                if (selected.Any(r => !SameZone(row.Timezone, r.Timezone)))
                    throw new FormatException($"Conflicting fallback timezones for {row.Key} {row.Year}.");
                var timeline = new JsonArray();
                foreach (var deadline in selected.Select(r => r.Deadline).Distinct().Order(StringComparer.Ordinal))
                    timeline.Add(new JsonObject { ["deadline"] = deadline });
                editions.Add(new JsonObject
                {
                    ["id"] = row.Key.Split('/')[1] + row.Year.ToString(CultureInfo.InvariantCulture),
                    ["year"] = row.Year, ["timezone"] = row.Timezone, ["timeline"] = timeline,
                    ["link"] = row.Link, ["date"] = row.Date, ["place"] = row.Place
                });
            }
            incoming.Add(new JsonObject
            {
                ["conference_key"] = first.Key, ["title"] = first.Title, ["sub"] = first.Category,
                ["rank"] = new JsonObject { ["ccf"] = first.Rank }, ["confs"] = editions
            });
        }

        var result = cachedJson is null ? incoming : Merge(incoming, cachedJson);
        var json = result.ToJsonString();
        AppStorage.ParseValidCatalog(json);
        return json;
    }

    private static JsonArray Merge(JsonArray incoming, string cachedJson)
    {
        AppStorage.ParseValidCatalog(cachedJson);
        var cached = JsonNode.Parse(cachedJson)!.AsArray();
        foreach (var source in incoming.OfType<JsonObject>())
        {
            var target = cached.OfType<JsonObject>().FirstOrDefault(c =>
                Text(c, "conference_key") == Text(source, "conference_key")
                || (Text(c, "sub").Equals(Text(source, "sub"), StringComparison.OrdinalIgnoreCase)
                    && Identity(Text(c, "title")).Slug == Identity(Text(source, "title")).Slug));
            if (target is null) { cached.Add(source.DeepClone()); continue; }
            var targetEditions = target["confs"] as JsonArray;
            if (targetEditions is null) { target["confs"] = source["confs"]!.DeepClone(); continue; }
            foreach (var edition in source["confs"]!.AsArray().OfType<JsonObject>())
            {
                var matches = targetEditions.OfType<JsonObject>().Where(e => e["year"]?.ToJsonString() == edition["year"]!.ToJsonString()).ToArray();
                if (matches.Length == 0) { targetEditions.Add(edition.DeepClone()); continue; }
                if (matches.Length != 1) continue;
                var existing = matches[0];
                var oldTimeline = existing["timeline"] as JsonArray;
                // The fallback page doesn't identify rounds. Never guess which of several cached rounds changed.
                if (oldTimeline is { Count: > 1 }) continue;
                var timeline = edition["timeline"]!.DeepClone().AsArray();
                if (oldTimeline is { Count: 1 } && timeline.Count == 1 && oldTimeline[0] is JsonObject oldRound)
                {
                    var round = oldRound.DeepClone().AsObject();
                    round["deadline"] = timeline[0]!["deadline"]!.DeepClone();
                    timeline[0] = round;
                }
                existing["timeline"] = timeline;
                existing["timezone"] = edition["timezone"]!.DeepClone();
            }
        }
        return cached;
    }

    private static (string Slug, string Title) Identity(string title)
    {
        if (Aliases.TryGetValue(title, out var identity)) return identity;
        var slug = new string(title.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        if (slug.Length == 0) throw new FormatException("The fallback conference name has no usable identity.");
        return (slug, title);
    }

    private static bool SameZone(string first, string second) => DeadlineTime.ResolveZone(first)!.Equals(DeadlineTime.ResolveZone(second));
    private static Match[] FindRows(string html)
    {
        try { return RowStart.Matches(html).Cast<Match>().ToArray(); }
        catch (RegexMatchTimeoutException ex) { throw new FormatException("The fallback page could not be parsed within the time limit.", ex); }
    }
    private static string Text(JsonObject source, string key) => source[key]?.GetValue<string>() ?? "";
    private static string Required(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim() : throw new FormatException($"The fallback row is missing {key}.");

    private sealed record Row(string Key, string Title, string Category, string Rank, int Year, string Timezone,
        string Deadline, string Link, string Date, string Place, bool IsCanonical);

    // Only quoted strings and nonnegative integers are permitted; expressions, calls and nested values are rejected.
    private sealed class LiteralReader(string text, int position)
    {
        public Dictionary<string, string> ReadRow()
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            Expect('{');
            while (true)
            {
                var key = ReadString();
                Expect(':');
                SkipSpace();
                string value;
                if (position < text.Length && text[position] is '\'' or '"') value = ReadString();
                else
                {
                    var start = position;
                    while (position < text.Length && char.IsAsciiDigit(text[position])) position++;
                    if (start == position) throw Invalid();
                    value = text[start..position];
                }
                if (!fields.TryAdd(key, value)) throw Invalid();
                SkipSpace();
                if (position < text.Length && text[position] == '}') { position++; break; }
                Expect(',');
            }
            Expect(')');
            return fields;
        }

        private string ReadString()
        {
            SkipSpace();
            if (position >= text.Length || text[position] is not ('\'' or '"')) throw Invalid();
            var quote = text[position++];
            var result = new StringBuilder();
            while (position < text.Length)
            {
                var ch = text[position++];
                if (ch == quote) return result.ToString();
                if (ch is '\r' or '\n' || ch < ' ') throw Invalid();
                if (ch != '\\') { result.Append(ch); continue; }
                if (position >= text.Length) throw Invalid();
                ch = text[position++];
                result.Append(ch switch
                {
                    '\'' => '\'', '"' => '"', '\\' => '\\', '/' => '/',
                    'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v',
                    'u' => ReadHex(4), 'x' => ReadHex(2),
                    _ => throw Invalid()
                });
            }
            throw Invalid();
        }

        private char ReadHex(int length)
        {
            if (position + length > text.Length || !ushort.TryParse(text.AsSpan(position, length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                throw Invalid();
            position += length;
            return (char)value;
        }

        private void Expect(char expected)
        {
            SkipSpace();
            if (position >= text.Length || text[position++] != expected) throw Invalid();
        }

        private void SkipSpace() { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }
        private static FormatException Invalid() => new("The fallback page contains an invalid static conference row.");
    }
}
