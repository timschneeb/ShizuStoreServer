using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Core.Sources;

public sealed class PlayStoreException(string message) : Exception(message);

/// <summary>
/// Listing metadata scraped from the public Play page. Every field is
/// best-effort: the page layout changes and a missing field is not an error.
/// </summary>
public sealed record PlayAppDetails(
    string? IconUrl,
    string? Name,
    string? DeveloperId,
    string? DeveloperName,
    string? DeveloperUrl,
    string? VersionName,
    string? FullDescription,
    DateTimeOffset? UpdatedAt);

public interface IPlayStoreClient
{
    /// <summary>
    /// Resolves the listing icon URL for a package, or null when the page
    /// carries no icon. Throws <see cref="PlayStoreException"/> on HTTP errors.
    /// </summary>
    Task<string?> GetIconUrlAsync(string packageId, CancellationToken ct = default);

    /// <summary>
    /// Scrapes identity, developer and description from the listing. Null on
    /// any failure; best-effort metadata never fails an enrich. Default impl
    /// keeps test doubles that only care about icons simple.
    /// </summary>
    Task<PlayAppDetails?> GetAppDetailsAsync(string packageId, CancellationToken ct = default) =>
        Task.FromResult<PlayAppDetails?>(null);
}

/// <summary>
/// Scrapes the public Play Store listing page for the app icon and the
/// metadata the awesome list does not carry (developer, version, description).
/// There is no anonymous metadata API, so the rendered page is parsed.
/// </summary>
public sealed class PlayStoreClient(HttpClient http) : IPlayStoreClient
{
    private static readonly Regex IconMeta = new(
        "<meta[^>]+property=\"og:image\"[^>]+content=\"(?<url>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AppNameSpan = new(
        "itemprop=\"name\"[^>]*>(?<name>[^<]+)<",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DeveloperLink = new(
        "<div class=\"Vbfug[^\"]*\"><a href=\"/store/apps/dev\\?id=(?<id>\\d+)\"[^>]*><span>(?<name>[^<]+)</span>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VersionName = new(
        "\"141\":\\[\\[\\[\"(?<version>[^\"]+)\"\\]\\]",
        RegexOptions.Compiled);

    private static readonly Regex DescriptionBlock = new(
        "<div[^>]*data-g-id=\"description\"[^>]*>(?<html>.*?)</div>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex UpdatedOn = new(
        "\"146\":\\[\\[\"(?<date>[^\"]+)\"",
        RegexOptions.Compiled);

    public async Task<string?> GetIconUrlAsync(string packageId, CancellationToken ct = default)
    {
        var html = await FetchPageAsync(packageId, ct);
        var match = IconMeta.Match(html);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["url"].Value) : null;
    }

    public async Task<PlayAppDetails?> GetAppDetailsAsync(string packageId, CancellationToken ct = default)
    {
        var html = await FetchPageAsync(packageId, ct);

        var icon = IconMeta.Match(html);
        var name = AppNameSpan.Match(html);
        var developer = DeveloperLink.Match(html);
        var version = VersionName.Match(html);
        var description = DescriptionBlock.Match(html);
        var updated = UpdatedOn.Match(html);

        var developerId = developer.Success ? developer.Groups["id"].Value : null;

        var details = new PlayAppDetails(
            icon.Success ? WebUtility.HtmlDecode(icon.Groups["url"].Value) : null,
            name.Success ? WebUtility.HtmlDecode(name.Groups["name"].Value) : null,
            developerId,
            developer.Success ? WebUtility.HtmlDecode(developer.Groups["name"].Value) : null,
            developerId is null ? null : $"https://play.google.com/store/apps/dev?id={developerId}",
            version.Success ? version.Groups["version"].Value : null,
            description.Success ? ToPlainText(description.Groups["html"].Value) : null,
            ParseUpdatedOn(updated));

        // A consent or error page parses nothing; treat that as no details.
        return details.IconUrl is null
            && details.Name is null
            && details.VersionName is null
            && details.FullDescription is null
                ? null
                : details;
    }

    private async Task<string> FetchPageAsync(string packageId, CancellationToken ct)
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

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Play stores the description as rendered HTML; clients render plain text.</summary>
    private static string? ToPlainText(string html)
    {
        var text = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text).Replace("\r\n", "\n");
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        return text.Trim();
    }

    private static DateTimeOffset? ParseUpdatedOn(Match match)
    {
        if (!match.Success)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            match.Groups["date"].Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
