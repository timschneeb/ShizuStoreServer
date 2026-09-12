using System.Net;
using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Core.Sources;

public sealed class PlayStoreException(string message) : Exception(message);

public interface IPlayStoreClient
{
    /// <summary>
    /// Resolves the listing icon URL for a package, or null when the page
    /// carries no icon. Throws <see cref="PlayStoreException"/> on HTTP errors.
    /// </summary>
    Task<string?> GetIconUrlAsync(string packageId, CancellationToken ct = default);
}

/// <summary>
/// Scrapes the public Play Store listing page for the app icon. There is no
/// anonymous icon API, so the page's og:image meta tag (a 512px raster on
/// play-lh.googleusercontent.com) is used.
/// </summary>
public sealed class PlayStoreClient(HttpClient http) : IPlayStoreClient
{
    private static readonly Regex IconMeta = new(
        "<meta[^>]+property=\"og:image\"[^>]+content=\"(?<url>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<string?> GetIconUrlAsync(string packageId, CancellationToken ct = default)
    {
        var url = $"https://play.google.com/store/apps/details?id={Uri.EscapeDataString(packageId)}&hl=en&gl=US";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new PlayStoreException($"Play Store answered {(int)response.StatusCode} for {packageId}.");
        }

        var html = await response.Content.ReadAsStringAsync(ct);
        var match = IconMeta.Match(html);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["url"].Value) : null;
    }
}
