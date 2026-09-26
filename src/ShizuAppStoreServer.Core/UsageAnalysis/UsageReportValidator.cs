using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>
/// Validated, display-ready AI usage report. The model returns the three
/// markdown sections separately; the server owns the section headings and
/// composes the served text. <see cref="Markdown"/> only exists so stored logs
/// written before the split still deserialize.
/// </summary>
public sealed record UsageReport(
    string Short,
    string MarkdownUsage,
    string? MarkdownApiUsage = null,
    string? MarkdownNotableDetails = null,
    string? Markdown = null)
{
    /// <summary>Full markdown as served and rendered, with the server-owned headings.</summary>
    public string ComposedMarkdown => !string.IsNullOrWhiteSpace(Markdown)
        ? Markdown
        : Compose(MarkdownUsage, MarkdownApiUsage, MarkdownNotableDetails);

    public static string Compose(string usage, string? apiUsage, string? notableDetails)
    {
        var builder = new StringBuilder(usage.Trim());
        if (!string.IsNullOrWhiteSpace(apiUsage))
        {
            builder.Append("\n\n## Android APIs or commands used\n\n").Append(apiUsage.Trim());
        }

        if (!string.IsNullOrWhiteSpace(notableDetails))
        {
            builder.Append("\n\n## Notable details\n\n").Append(notableDetails.Trim());
        }

        return builder.ToString();
    }
}

/// <summary>
/// Accepts or repairs the model's final answer. The visible text must stay
/// free of file paths, line numbers, file names and other implementation
/// details: the report is written for end users. Raw HTML, images and
/// off-allowlist links are removed so untrusted repository content cannot
/// smuggle markup or tracking into the app.
/// </summary>
public static partial class UsageReportValidator
{
    public const int MaxShortChars = 200;
    public const int MaxUsageChars = 3500;
    public const int MaxApiChars = 3000;
    public const int MaxNotableChars = 1000;

    private static readonly string[] LinkHosts =
        ["github.com", "gitlab.com", "raw.githubusercontent.com"];

    [GeneratedRegex(@"\[([^\]\r\n]{1,200})\]\(\s*([^)\s]{1,500})(?:\s+""[^""]*"")?\s*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"(?is)<(script|style)\b[^>]*>.*?</\1\s*>")]
    private static partial Regex ScriptBlock();

    [GeneratedRegex(@"<[^>]{1,500}>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"(?<![\w/])([A-Za-z0-9_./\-]{2,200}\.(?:kt|java|aidl|xml|gradle|kts|toml|pro)):(\d{1,6})", RegexOptions.IgnoreCase)]
    private static partial Regex BacktickCitation();

    [GeneratedRegex(@"/blob/(?:[0-9a-f]{7,64})/([^#\s\)]+)#L(\d{1,6})")]
    private static partial Regex BlobCitation();

    // Onboarding and Shizuku permission narration is setup, not usage. The
    // model still emits it as a capability bullet, so a rejection here forces
    // the correction round to drop it. Keep the matches Shizuku-scoped: other
    // apps' permission requests are a legitimate capability.
    [GeneratedRegex(@"(?i)\bonboarding\b|\b(?:grant(?:s|ed|ing)?|approv(?:e|es|ed|ing)|request(?:s|ed|ing)?|confirm(?:s|ed|ing)?|allow(?:s|ed|ing)?)\s+(?:the\s+)?Shizuku|Shizuku\s+permission\s+(?:request|dialog|prompt)")]
    private static partial Regex SetupNarration();

    // The API list names Android platform APIs. Shizuku's SDK helpers and its
    // service interface are neither Android nor the app, so they must never
    // appear; the model still lists them as the privileged entry point.
    [GeneratedRegex(@"\bShizuku\.[A-Za-z_]\w*|\bIShizukuService\b|\bSystemServiceHelper\b|\bShizukuBinderWrapper\b")]
    private static partial Regex ShizukuSdkHelper();

    // The capability section is a bullet list by contract. The model sometimes
    // emits plain lines there, which markdown renders as one paragraph blob,
    // so a usage report (one with the API list) must show real list items.
    [GeneratedRegex(@"(?m)^[ \t]*[-*+][ \t]+\*\*[^*\r\n]+\*\*")]
    private static partial Regex BulletItem();

    // Any list item, bold label or not: the API list is one bullet per API.
    [GeneratedRegex(@"(?m)^[ \t]*[-*+][ \t]+\S")]
    private static partial Regex AnyBulletItem();

    /// <summary>
    /// Parses and validates the model output. On failure returns null and an
    /// error describing what to fix, so the caller can ask for one correction.
    /// </summary>
    public static UsageReport? Validate(string? content, out string? error)
    {
        error = null;
        var json = ExtractJson(content);
        if (json is null)
        {
            error = "response was not a JSON object with the fields 'short', 'markdown_usage', 'markdown_api_usage' and 'markdown_notable_details'";
            return null;
        }

        JsonObject root;
        string? shortText;
        string? usage;
        string? apiUsage;
        string? notable;
        try
        {
            root = json;
            shortText = root["short"]?.GetValue<string>();
            usage = root["markdown_usage"]?.GetValue<string>();
            apiUsage = root["markdown_api_usage"] is null ? null : root["markdown_api_usage"]!.GetValue<string>();
            notable = root["markdown_notable_details"] is null ? null : root["markdown_notable_details"]!.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            error = "'short' and 'markdown_usage' must be strings; 'markdown_api_usage' and 'markdown_notable_details' may be strings or null";
            return null;
        }

        if (string.IsNullOrWhiteSpace(shortText) || string.IsNullOrWhiteSpace(usage))
        {
            error = "'short' and 'markdown_usage' must both be non-empty";
            return null;
        }

        var shortClean = NormalizeText(shortText);
        if (shortClean.Length > MaxShortChars)
        {
            shortClean = shortClean[..MaxShortChars].TrimEnd();
        }

        var usageClean = Cap(NormalizeMarkdown(usage), MaxUsageChars);
        var apiClean = string.IsNullOrWhiteSpace(apiUsage)
            ? null
            : Cap(NormalizeApiList(NormalizeMarkdown(apiUsage)), MaxApiChars);
        var notableClean = string.IsNullOrWhiteSpace(notable) ? null : Cap(NormalizeMarkdown(notable), MaxNotableChars);

        if (usageClean.Length < 20)
        {
            error = "'markdown_usage' is too short";
            return null;
        }

        var visible = string.Join('\n',
            new[] { shortClean, usageClean, apiClean, notableClean }.Where(part => part is not null));
        if (BacktickCitation().IsMatch(visible) || BlobCitation().IsMatch(visible))
        {
            error = "the visible text must not mention file paths or line numbers; describe the capability in user terms";
            return null;
        }

        if (SetupNarration().IsMatch(visible))
        {
            error = "the report must not describe setup: no onboarding or Shizuku permission requests, grants or approval steps, only what the app does with its privileged calls";
            return null;
        }

        if (ShizukuSdkHelper().IsMatch(visible))
        {
            error = "list Android platform APIs and system commands only: never the app's own classes or Shizuku SDK helpers such as Shizuku.newProcess, Shizuku.getBinder, IShizukuService, SystemServiceHelper or ShizukuBinderWrapper";
            return null;
        }

        if (apiClean is not null && !BulletItem().IsMatch(usageClean))
        {
            error = "'markdown_usage' must be a markdown bullet list: one \"- \" bullet per capability, each starting with a bold label such as **Install apps**, instead of plain lines or unbolded bullets";
            return null;
        }

        if (apiClean is not null)
        {
            var lines = apiClean.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0 || lines.Any(line => !AnyBulletItem().IsMatch(line)))
            {
                error = "'markdown_api_usage' must be one \"- \" bullet per Android platform API or command, with no prose lines";
                return null;
            }

            if (!apiClean.Contains('`'))
            {
                error = "'markdown_api_usage' bullets must wrap each API or command in backticks";
                return null;
            }
        }

        return new UsageReport(shortClean, usageClean, apiClean, notableClean);
    }

    private static string Cap(string text, int max) => text.Length > max ? text[..max].TrimEnd() : text;

    // The model sometimes drops the bullet marker on the first API line. A
    // backticked line is unambiguous, so repair it instead of rejecting.
    private static string NormalizeApiList(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '`' && !AnyBulletItem().IsMatch(lines[i]))
            {
                lines[i] = "- " + trimmed;
            }
        }

        return string.Join('\n', lines);
    }

    private static JsonObject? ExtractJson(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline)
            {
                text = text[(firstNewline + 1)..lastFence].Trim();
            }
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text[start..(end + 1)]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeMarkdown(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n").Replace('\u2014', '-').Replace('\u2013', '-').Trim();
        text = ScriptBlock().Replace(text, string.Empty);
        text = MarkdownImage().Replace(text, string.Empty);
        text = MarkdownLink().Replace(text, MapLink);
        text = HtmlTag().Replace(text, string.Empty);

        var lines = text.Split('\n').Select(line => line.TrimEnd());
        return string.Join('\n', lines).Trim();
    }

    private static string MapLink(Match match)
    {
        var label = match.Groups[1].Value;
        var target = match.Groups[2].Value.Trim();

        // Relative targets point into the repository, which is exactly the
        // implementation detail the visible report must not carry.
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return label;
        }

        if (uri.Scheme == Uri.UriSchemeHttps
            && LinkHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)))
        {
            return match.Value;
        }

        return label;
    }

    private static string NormalizeText(string value)
    {
        var text = value
            .Replace('\u2014', '-')
            .Replace('\u2013', '-')
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
        var builder = new StringBuilder(text.Length);
        var lastSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace)
                {
                    builder.Append(' ');
                }

                lastSpace = true;
            }
            else
            {
                builder.Append(c);
                lastSpace = false;
            }
        }

        return builder.ToString();
    }
}
