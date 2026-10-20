using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeadlineCalendar.Core;

namespace DeadlineCalendar.Infrastructure;

public sealed record CachedCatalog(CatalogSnapshot Snapshot, DateTimeOffset FetchedAt, string? ETag, string RawJson);

/// <summary>Per-user preferences and an atomic raw-catalog/freshness bundle.</summary>
public sealed partial class AppStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _preferencesLock = new();
    private readonly object _logLock = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public AppStorage(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeadlineCalendar")
            : directory);
    }

    public string DirectoryPath { get; }

    public UserPreferences LoadPreferences()
    {
        lock (_preferencesLock)
        {
            var path = Path.Combine(DirectoryPath, "preferences.json");
            if (!File.Exists(path)) return DefaultPreferences.Create();
            try
            {
                var preferences = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path), JsonOptions);
                return preferences is null ? DefaultPreferences.Create() : Normalize(preferences);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                Log("Preferences could not be read; using defaults.", ex);
                return DefaultPreferences.Create();
            }
        }
    }

    /// <summary>Saves a sanitized copy without modifying the caller's preferences.</summary>
    public void SavePreferences(UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var json = JsonSerializer.Serialize(Normalize(preferences), JsonOptions);
        lock (_preferencesLock) WriteAtomic(Path.Combine(DirectoryPath, "preferences.json"), Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Exports the complete current configuration without changing the active preferences or caller's model.</summary>
    public void ExportPreferences(UserPreferences preferences, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        var activePreferences = Path.GetFullPath(Path.Combine(DirectoryPath, "preferences.json"));
        if (string.Equals(destination, activePreferences, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a different destination from the active preferences file.", nameof(destinationPath));

        var json = JsonSerializer.Serialize(preferences, JsonOptions);
        WriteAtomic(destination, Encoding.UTF8.GetBytes(json));
    }

    public CachedCatalog? LoadCache()
    {
        var path = Path.Combine(DirectoryPath, "catalog-cache.json");
        if (!File.Exists(path)) return null;
        try
        {
            var cache = JsonSerializer.Deserialize<CacheEnvelope>(File.ReadAllText(path), JsonOptions);
            if (cache is null || cache.SchemaVersion != 1 || cache.FetchedAt == default || string.IsNullOrWhiteSpace(cache.RawJson))
                throw new InvalidDataException("The cached catalog envelope is incomplete.");
            var snapshot = ParseValidCatalog(cache.RawJson);
            return new CachedCatalog(snapshot, cache.FetchedAt.ToUniversalTime(), cache.ETag, cache.RawJson);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            Log("Cached catalog could not be read.", ex);
            return null;
        }
    }

    public async Task SaveCacheAsync(string rawJson, DateTimeOffset fetchedAt, string? etag, CancellationToken cancellationToken = default)
    {
        ParseValidCatalog(rawJson);
        if (fetchedAt == default) throw new ArgumentOutOfRangeException(nameof(fetchedAt));
        var envelope = new CacheEnvelope { RawJson = rawJson, FetchedAt = fetchedAt.ToUniversalTime(), ETag = etag };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "catalog-cache.json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally { TryDelete(temporary); }
        }
        finally { _cacheLock.Release(); }
    }

    internal static CatalogSnapshot ParseValidCatalog(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) throw new InvalidDataException("The catalog is empty.");
        var snapshot = CatalogParser.Parse(rawJson);
        if (snapshot.Series.Count == 0 || snapshot.Series.Any(s => string.IsNullOrWhiteSpace(s.Key) || string.IsNullOrWhiteSpace(s.Title)))
            throw new InvalidDataException("The catalog contains no valid conference series.");
        return snapshot;
    }

    internal void Log(string message, Exception? exception = null)
    {
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Move(path, path + ".1", overwrite: true);
                var suffix = exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message.Replace('\r', ' ').Replace('\n', ' ')})";
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{suffix}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static UserPreferences Normalize(UserPreferences source)
    {
        var defaults = new UserPreferences();
        var bounds = source.Bounds ?? defaults.Bounds;
        var followed = (source.Followed ?? []).Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Key))
            .GroupBy(f => f.Key.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(f => new FollowedConference { Key = f.Key.Trim(), ColorHex = ValidColor(f.ColorHex) ?? "#60A5FA", RecurrenceYears = Math.Clamp(f.RecurrenceYears, 0, 2) }).ToList();
        return new UserPreferences
        {
            SchemaVersion = 1,
            Language = source.Language is "zh-CN" or "en" ? source.Language : defaults.Language,
            DisplayTimeZoneId = !string.IsNullOrWhiteSpace(source.DisplayTimeZoneId) && DeadlineTime.ResolveZone(source.DisplayTimeZoneId) is not null ? source.DisplayTimeZoneId.Trim() : defaults.DisplayTimeZoneId,
            WeekStartsMonday = source.WeekStartsMonday,
            LimitCalendarToOneYear = source.LimitCalendarToOneYear,
            AlwaysOnTop = source.AlwaysOnTop,
            StartWithWindows = source.StartWithWindows,
            AutoReturnToTodayEnabled = source.AutoReturnToTodayEnabled,
            AutoReturnToTodayDelaySeconds = Math.Clamp(source.AutoReturnToTodayDelaySeconds, 1, 3600),
            BackgroundOpacity = FiniteClamp(source.BackgroundOpacity, 0, 1, defaults.BackgroundOpacity),
            ContentOpacity = FiniteClamp(source.ContentOpacity, 0, 1, defaults.ContentOpacity),
            PastOpacity = FiniteClamp(source.PastOpacity, 0, 1, defaults.PastOpacity),
            TooltipBackgroundOpacity = FiniteClamp(source.TooltipBackgroundOpacity, 0, 1, defaults.TooltipBackgroundOpacity),
            ColorOverrides = (source.ColorOverrides ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Key) && ValidColor(p.Value) is not null)
                .ToDictionary(p => p.Key, p => ValidColor(p.Value)!, StringComparer.Ordinal),
            TextStyles = (source.TextStyles ?? []).Where(p => TypographyDefaults.Roles.Contains(p.Key, StringComparer.Ordinal))
                .ToDictionary(p => p.Key, p => TypographyDefaults.Normalize(p.Key, p.Value), StringComparer.Ordinal),
            Followed = followed,
            HeaderConferenceKey = followed.FirstOrDefault(f => string.Equals(f.Key, source.HeaderConferenceKey?.Trim(), StringComparison.OrdinalIgnoreCase))?.Key,
            Exclusions = (source.Exclusions ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.SeriesKey)).Select(e => new RoundExclusion
            {
                SeriesKey = e.SeriesKey.Trim(), EditionId = e.EditionId ?? "", RoundKey = e.RoundKey ?? "", NormalizedComment = e.NormalizedComment ?? "", IsExcluded = e.IsExcluded
            }).ToList(),
            Bounds = new WindowBounds
            {
                Left = FiniteClamp(bounds.Left, -1_000_000, 1_000_000, -1), Top = FiniteClamp(bounds.Top, -1_000_000, 1_000_000, -1),
                Width = FiniteClamp(bounds.Width, 100, 32768, defaults.Bounds.Width), Height = FiniteClamp(bounds.Height, 100, 32768, defaults.Bounds.Height)
            },
            FirstRunCompleted = source.FirstRunCompleted
        };
    }

    private static double FiniteClamp(double value, double minimum, double maximum, double fallback) => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    private static string? ValidColor(string? value) => value is not null && HexColor().IsMatch(value) ? value.ToUpperInvariant() : null;
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class CacheEnvelope
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTimeOffset FetchedAt { get; set; }
        public string? ETag { get; set; }
        public string RawJson { get; set; } = "";
    }
}
