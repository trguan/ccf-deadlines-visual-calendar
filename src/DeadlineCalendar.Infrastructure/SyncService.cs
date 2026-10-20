using System.Net;
using DeadlineCalendar.Core;

namespace DeadlineCalendar.Infrastructure;

/// <summary>Checks the daily 12:00 UTC anchor, catches up on resume, and retries without blocking the UI.</summary>
public sealed class SyncService : IDisposable
{
    public static readonly Uri CatalogUri = new("https://ccfddl.com/conference/allconf.json");
    public static readonly Uri FallbackCatalogUri = new("https://ccfddl.top/");
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];
    private readonly AppStorage _storage;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private ITimer? _timer;
    private CachedCatalog? _cached;
    private DateTimeOffset? _nextRetryUtc;
    private int _failureCount;
    private bool _initialized;
    private volatile bool _disposed;

    public SyncService(AppStorage storage, HttpClient? client = null, TimeProvider? timeProvider = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _ownsClient = client is null;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler? Updated;
    public CatalogSnapshot? Catalog { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public string? LastError { get; private set; }
    public bool IsSyncing { get; private set; }

    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initializeGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            _cached = _storage.LoadCache();
            Catalog = _cached?.Snapshot;
            LastSuccessUtc = _cached?.FetchedAt;
            _initialized = true;
            _timer = _timeProvider.CreateTimer(_ => { _ = PollAsync(); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            RaiseUpdated();
        }
        finally { _initializeGate.Release(); }
        await CheckDueAsync().ConfigureAwait(false);
    }

    public Task CheckDueAsync() => RefreshAsync(force: false);

    public async Task RefreshAsync(bool force = false)
    {
        if (_disposed) return;
        if (!await _refreshGate.WaitAsync(0, _lifetime.Token).ConfigureAwait(false)) return;
        try
        {
            if (_disposed) return;
            var now = _timeProvider.GetUtcNow();
            if (!force && (!IsDue(now) || (_nextRetryUtc is not null && now < _nextRetryUtc))) return;
            IsSyncing = true;
            RaiseUpdated();
            try
            {
                var downloaded = await FetchCatalogWithFallbackAsync().ConfigureAwait(false);
                await _storage.SaveCacheAsync(downloaded.RawJson, downloaded.FetchedAt, downloaded.ETag, _lifetime.Token).ConfigureAwait(false);
                _cached = downloaded;
                Catalog = downloaded.Snapshot;
                LastSuccessUtc = downloaded.FetchedAt;
                LastError = null;
                _failureCount = 0;
                _nextRetryUtc = null;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or FormatException or OperationCanceledException)
            {
                LastError = ex is OperationCanceledException ? "The synchronization request timed out." : ex.Message;
                _nextRetryUtc = _timeProvider.GetUtcNow() + RetryDelays[Math.Min(_failureCount, RetryDelays.Length - 1)];
                _failureCount++;
                _storage.Log("Catalog synchronization failed; retained the last successful catalog.", ex);
            }
            finally
            {
                IsSyncing = false;
                RaiseUpdated();
            }
        }
        finally { _refreshGate.Release(); }
    }

    private async Task<CachedCatalog> FetchCatalogWithFallbackAsync()
    {
        try { return await FetchCatalogAsync(CatalogUri).ConfigureAwait(false); }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested &&
            ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or FormatException or OperationCanceledException)
        {
            _storage.Log("Primary catalog download failed; trying https://ccfddl.top/.", ex);
            return await FetchCatalogAsync(FallbackCatalogUri).ConfigureAwait(false);
        }
    }

    private async Task<CachedCatalog> FetchCatalogAsync(Uri uri)
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        // Each site gets a separate timeout, so a primary timeout cannot cancel the fallback.
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        var requestToken = requestTimeout.Token;
        var isPrimary = uri == CatalogUri;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("DeadlineCalendar/1.0");
        request.Headers.Accept.ParseAdd(isPrimary ? "application/json" : "text/html");
        if (isPrimary && !string.IsNullOrWhiteSpace(_cached?.ETag))
            request.Headers.TryAddWithoutValidation("If-None-Match", _cached.ETag);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
        string rawJson;
        CatalogSnapshot snapshot;
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (_cached is null || !request.Headers.Contains("If-None-Match"))
                throw new InvalidDataException("The server returned 304 without a matching conditional request.");
            rawJson = _cached.RawJson;
            snapshot = _cached.Snapshot;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 20 * 1024 * 1024) throw new InvalidDataException("The catalog response is unexpectedly large.");
            await response.Content.LoadIntoBufferAsync(20 * 1024 * 1024, requestToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(requestToken).ConfigureAwait(false);
            if (body.Length > 20 * 1024 * 1024) throw new InvalidDataException("The catalog response is unexpectedly large.");
            rawJson = isPrimary ? body : FallbackCatalogParser.Parse(body, _cached?.RawJson);
            snapshot = AppStorage.ParseValidCatalog(rawJson);
        }
        requestToken.ThrowIfCancellationRequested();
        // Cache validators belong to the primary origin; fallback HTML is converted to JSON.
        var etag = isPrimary
            ? response.Headers.ETag?.ToString() ?? (response.StatusCode == HttpStatusCode.NotModified ? _cached?.ETag : null)
            : null;
        return new CachedCatalog(snapshot, _timeProvider.GetUtcNow(), etag, rawJson);
    }

    private bool IsDue(DateTimeOffset now)
    {
        var anchor = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(12);
        if (now < anchor) anchor = anchor.AddDays(-1);
        return LastSuccessUtc is null || LastSuccessUtc < anchor || LastError is not null;
    }

    private async Task PollAsync()
    {
        try { await CheckDueAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception ex) { _storage.Log("Synchronization timer failed.", ex); }
    }

    private void RaiseUpdated()
    {
        if (_disposed) return;
        var handlers = Updated;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { _storage.Log("A synchronization status subscriber failed.", ex); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _lifetime.Cancel();
        if (_ownsClient) _client.Dispose();
        // Active asynchronous operations may still observe the token and release their gates.
        // Do not dispose these synchronization primitives until those operations have completed.
        Updated = null;
    }
}
