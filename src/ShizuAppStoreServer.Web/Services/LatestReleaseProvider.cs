using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace ShizuAppStoreServer.Web.Services;

/// <summary>
/// Reads the latest GitHub release of the client once an hour and returns its
/// APK asset URL; every failure falls back to the releases page.
/// </summary>
public sealed class LatestReleaseProvider(
    HttpClient http,
    IMemoryCache cache,
    ILogger<LatestReleaseProvider> log) : IAppReleaseProvider
{
    private const string ReleasesApi = "https://api.github.com/repos/timschneeb/ShizuStore/releases/latest";
    private const string FallbackUrl = "https://github.com/timschneeb/ShizuStore/releases/latest";
    private const string CacheKey = "shizustore-latest-apk";

    public async Task<string> GetLatestApkUrlAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var url = FallbackUrl;
        try
        {
            using var response = await http.GetAsync(ReleasesApi, ct);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                url = document.RootElement.GetProperty("assets").EnumerateArray()
                    .Select(asset => asset.GetProperty("browser_download_url").GetString())
                    .FirstOrDefault(candidate =>
                        candidate is not null &&
                        candidate.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                    ?? FallbackUrl;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not resolve the latest ShizuStore release; using the releases page.");
        }

        cache.Set(CacheKey, url, TimeSpan.FromHours(1));
        return url;
    }
}
