using System.Text.Json;
using System.Text.Json.Serialization;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sync;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// One Exodus Privacy tracker signature entry. Only the code signature is
/// used: the server does no dynamic traffic analysis, so network signatures
/// (regexes over URL fragments) are not matched and trackers detectable only
/// by network stay undetected by design. Treat absence as "not detected",
/// never as "clean".
/// </summary>
public sealed record TrackerSignature(
    int Id,
    string Name,
    string CodeSignature,
    IReadOnlyList<string> Categories);

/// <summary>Supplies the Exodus tracker signature catalog for DEX scans.</summary>
public interface ITrackerCatalog
{
    /// <summary>
    /// Current signatures; empty when no catalog is configured or reachable
    /// and nothing was cached. Never throws for a fetch or cache failure.
    /// </summary>
    Task<IReadOnlyList<TrackerSignature>> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Fetches the Exodus tracker list (<c>/api/trackers</c>) and caches it in
/// memory and, when <see cref="EnrichmentOptions.ExodusTrackerCachePath"/> is
/// set, on disk. The endpoint is fetched at most once per
/// <see cref="EnrichmentOptions.ExodusTrackerRefreshInterval"/>. A fetch
/// failure keeps serving the last good catalog: tracker detection is a
/// garnish signal and a vendor outage must not silently drop detections from
/// fresh analyses. Detection is skipped entirely when
/// <see cref="EnrichmentOptions.ExodusTrackerUrl"/> is null.
/// </summary>
public sealed class ExodusTrackerCatalog(HttpClient http, EnrichmentOptions options, IRunLog? runLog = null) : ITrackerCatalog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<TrackerSignature>? _catalog;
    private DateTimeOffset _fetchedAt;

    public async Task<IReadOnlyList<TrackerSignature>> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_catalog is not null
                && DateTimeOffset.UtcNow - _fetchedAt < options.ExodusTrackerRefreshInterval)
            {
                return _catalog;
            }

            var fetched = await TryFetchAsync(ct);
            if (fetched is not null)
            {
                _catalog = fetched;
                _fetchedAt = DateTimeOffset.UtcNow;
                return fetched;
            }

            if (_catalog is not null)
            {
                return _catalog;
            }

            var disk = await TryReadCacheAsync(ct);
            _catalog = disk ?? [];
            _fetchedAt = DateTimeOffset.UtcNow;
            return _catalog;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<TrackerSignature>?> TryFetchAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ExodusTrackerUrl))
        {
            return null;
        }

        try
        {
            var json = await http.GetByteArrayAsync(options.ExodusTrackerUrl, ct);
            var catalog = ExodusTrackerParser.Parse(json);
            WriteCache(json);
            runLog?.Detail($"exodus catalog: {catalog.Count} tracker signatures");
            return catalog;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            runLog?.Detail($"exodus catalog fetch failed ({ex.Message}), using cache");
            return null;
        }
    }

    private void WriteCache(byte[] json)
    {
        if (string.IsNullOrWhiteSpace(options.ExodusTrackerCachePath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(options.ExodusTrackerCachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(options.ExodusTrackerCachePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            runLog?.Detail($"exodus catalog cache write failed ({ex.Message})");
        }
    }

    private async Task<IReadOnlyList<TrackerSignature>?> TryReadCacheAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ExodusTrackerCachePath)
            || !File.Exists(options.ExodusTrackerCachePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllBytesAsync(options.ExodusTrackerCachePath, ct);
            return ExodusTrackerParser.Parse(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            runLog?.Detail($"exodus catalog cache read failed ({ex.Message})");
            return null;
        }
    }
}

/// <summary>Parses the Exodus <c>/api/trackers</c> payload.</summary>
public static class ExodusTrackerParser
{
    /// <summary>
    /// Pure parse of <c>{"trackers": {"&lt;id&gt;": {...}}}</c>. Entries
    /// without a code signature (network-only trackers such as Google Ads)
    /// cannot be matched statically and are dropped; the result is ordered by
    /// tracker id so match evidence is deterministic.
    /// </summary>
    public static IReadOnlyList<TrackerSignature> Parse(byte[] json)
    {
        var payload = JsonSerializer.Deserialize<PayloadDto>(json);
        var result = new List<TrackerSignature>();
        if (payload?.Trackers is null)
        {
            return result;
        }

        foreach (var (idText, tracker) in payload.Trackers)
        {
            if (tracker is null
                || !int.TryParse(idText, out var id)
                || string.IsNullOrWhiteSpace(tracker.CodeSignature))
            {
                continue;
            }

            result.Add(new TrackerSignature(
                id,
                tracker.Name ?? idText,
                tracker.CodeSignature,
                tracker.Categories ?? []));
        }

        return result.OrderBy(t => t.Id).ToList();
    }

    private sealed class PayloadDto
    {
        [JsonPropertyName("trackers")]
        public Dictionary<string, TrackerDto?>? Trackers { get; set; }
    }

    private sealed class TrackerDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("code_signature")]
        public string? CodeSignature { get; set; }

        [JsonPropertyName("categories")]
        public List<string>? Categories { get; set; }
    }
}
