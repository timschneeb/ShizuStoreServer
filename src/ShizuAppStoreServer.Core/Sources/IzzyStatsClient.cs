using System.Net;
using System.Text.Json;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// IzzyOnDroid download statistics. f-droid.org publishes no per-app download
/// counts, but IzzyOnDroid's collector exposes rolling windows as a flat
/// <c>packageName -> count</c> JSON map; the yearly file is the broadest
/// signal. Thin over <see cref="HttpClient"/> so tests stub the handler.
/// </summary>
public sealed class IzzyStatsClient(HttpClient http)
{
    public const string RollingYearUrl =
        "https://dlstats.izzyondroid.org/iod-stats-collector/stats/basic/yearly/rolling.json";

    /// <returns>Stats ETag + package->count map, or null on <c>304 Not Modified</c>.</returns>
    /// <exception cref="HttpRequestException">Non-success status.</exception>
    public async Task<(string? Etag, IReadOnlyDictionary<string, long> Counts)?> GetRollingYearAsync(
        string? etag, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RollingYearUrl);
        request.ApplyIfNoneMatch(etag);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Izzy stats {RollingYearUrl} answered HTTP {(int)response.StatusCode}.");
        }

        var counts = await JsonSerializer.DeserializeAsync<Dictionary<string, long>>(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct)
            ?? new Dictionary<string, long>();
        return (response.Headers.ETag?.ToString(), counts);
    }
}

/// <summary>
/// Process-wide cache over the single Izzy stats file (one fetch serves every
/// app), so parallel enrichments do not each pull it. Revalidates with the
/// cached ETag; a 304 keeps the previous map. Callers treat failures as
/// "unknown count", never as an enrich failure.
/// </summary>
public sealed class IzzyStatsProvider(IzzyStatsClient client)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string? Etag, IReadOnlyDictionary<string, long> Counts)? _cache;

    public async Task<long?> GetDownloadsAsync(string packageName, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var fetched = await client.GetRollingYearAsync(_cache?.Etag, ct);
            if (fetched is not null)
            {
                _cache = fetched;
            }

            return _cache is { } cached && cached.Counts.TryGetValue(packageName, out var count)
                ? count
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
