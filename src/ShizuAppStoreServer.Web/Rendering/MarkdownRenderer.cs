using Ganss.Xss;
using Markdig;

namespace ShizuAppStoreServer.Web.Rendering;

/// <summary>
/// Renders upstream markdown (descriptions, usage reports, changelogs) to
/// sanitized HTML. Relative links and images resolve against the app's
/// upstream URL, mirroring how the client displays README images.
/// </summary>
public sealed class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        // Upstream markdown keeps image/layout attributes; the default
        // allowlist already strips scripts, styles and event handlers.
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("loading");
        sanitizer.AllowedAttributes.Add("srcset");
        sanitizer.AllowedSchemes.Add("mailto");
        return sanitizer;
    }

    public string Render(string? markdown, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var html = Markdown.ToHtml(markdown, Pipeline);
        var baseUri = NormalizeBaseUrl(baseUrl);
        return baseUri is null ? Sanitizer.Sanitize(html) : Sanitizer.Sanitize(html, baseUri);
    }

    // A repo URL without a trailing slash would resolve "img/x.png" one level
    // too high, so make the base directory explicit.
    private static string? NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }
}
