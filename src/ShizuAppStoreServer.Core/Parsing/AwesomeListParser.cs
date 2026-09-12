using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ShizuAppStoreServer.Core.Parsing;

/// <summary>
/// Parses the awesome-shizuku list format:
/// <c>* [Name](url) [tags] - Description `License` [(Source code)](url)</c>
/// with <c>##</c> sections, <c>###</c> categories, <c>####</c> subcategories
/// and indented nested entries. Mirrors the grammar enforced by scripts/lint.py.
/// </summary>
public sealed partial class AwesomeListParser
{
    // Same grammar as lint.py's app_pattern (bullet marker already stripped by Markdig).
    [GeneratedRegex(@"^\[(?<name>[^\]]+)\]\((?<url>[^)]+)\)(?<pretags>.*?)\s+-\s+(?<rest>.*)$", RegexOptions.Singleline)]
    private static partial Regex ItemPattern();

    // Same grammar as lint.py's end_pattern: description, `License`, optional [(Source code)](url).
    [GeneratedRegex(@"^(?<desc>.*?)\s+`(?<license>[^`]+)`(?:\s+\[\(Source code\)\]\((?<source>[^)]+)\))?\s*$", RegexOptions.Singleline)]
    private static partial Regex EndPattern();

    [GeneratedRegex(@"`(?<days>\d+)-(?<unit>[a-zA-Z]+) trial`")]
    private static partial Regex TrialPattern();

    // Every recognized pre-tag token plus the decorative money emoji.
    [GeneratedRegex(@"✨|💰|`Paid`|`IAP`|`Ads`|`Root`|`\d+-[a-zA-Z]+ trial`")]
    private static partial Regex KnownPreTags();

    private static readonly HashSet<string> IgnoredSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "table of contents",
        "languages",
        "license",
        "annotations",
    };

    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder().Build();

    public ParsedDocument Parse(string markdown, string listingName)
    {
        var document = new ParsedDocument { ListingName = listingName };
        var ast = Markdown.Parse(markdown, _pipeline);

        string currentSection = string.Empty;
        var inContent = false;
        string? currentCategoryName = null;
        string? currentSubcategoryName = null;
        var usedSlugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in ast)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var title = GetHeadingText(heading);
                    if (heading.Level == 2)
                    {
                        currentSection = title;
                        inContent = !IgnoredSections.Contains(title);
                        currentCategoryName = null;
                        currentSubcategoryName = null;
                    }
                    else if (heading.Level == 3 && inContent)
                    {
                        currentCategoryName = title;
                        currentSubcategoryName = null;
                    }
                    else if (heading.Level == 4 && inContent)
                    {
                        if (currentCategoryName is null)
                        {
                            document.Warnings.Add(new ParseWarning(title, "Subcategory without a parent category; skipped."));
                        }
                        else
                        {
                            currentSubcategoryName = title;
                        }
                    }
                    break;

                case ListBlock list when inContent:
                    if (currentCategoryName is null)
                    {
                        document.Warnings.Add(new ParseWarning(currentSection, "List entries outside of any category; skipped."));
                    }
                    else
                    {
                        var category = EnsureCategory(document, usedSlugs, currentSection, currentCategoryName, currentSubcategoryName);
                        ParseList(document, usedSlugs, markdown, list, category, depth: 0, parent: null);
                    }
                    break;
            }
        }

        return document;
    }

    private void ParseList(
        ParsedDocument document,
        HashSet<string> usedSlugs,
        string markdown,
        ListBlock list,
        ParsedCategory category,
        int depth,
        ParsedEntry? parent)
    {
        foreach (var listItem in list.OfType<ListItemBlock>())
        {
            var paragraph = listItem.OfType<ParagraphBlock>().FirstOrDefault();
            if (paragraph is null || paragraph.Span.IsEmpty)
            {
                continue;
            }

            // Slice the raw item text out of the original source via the block span.
            // (StringLineGroup does not expose the text reliably.)
            var span = paragraph.Span;
            var raw = markdown.Substring(span.Start, span.End - span.Start + 1).Trim();
            var entry = ParseEntry(document, usedSlugs, raw);

            if (entry is not null)
            {
                if (depth == 0 || parent is null)
                {
                    category.Entries.Add(entry);
                }
                else
                {
                    parent.Children.Add(entry);
                }
            }

            foreach (var nested in listItem.OfType<ListBlock>())
            {
                if (entry is null)
                {
                    document.Warnings.Add(new ParseWarning(category.Name, "Nested list without a parent entry; skipped."));
                    continue;
                }

                ParseList(document, usedSlugs, markdown, nested, category, depth + 1, entry);
            }
        }
    }

    private ParsedEntry? ParseEntry(ParsedDocument document, HashSet<string> usedSlugs, string raw)
    {
        var item = ItemPattern().Match(raw);
        if (!item.Success)
        {
            document.Warnings.Add(new ParseWarning(raw[..Math.Min(raw.Length, 60)], "Entry does not match '* [Name](URL) [Tags] - Description'."));
            return null;
        }

        var name = item.Groups["name"].Value.Trim();
        var url = item.Groups["url"].Value.Trim();
        var preTags = item.Groups["pretags"].Value;
        var rest = item.Groups["rest"].Value;

        var trialDays = ParseTrialDays(document, name, preTags);
        var leftover = KnownPreTags().Replace(preTags, string.Empty).Trim();
        if (leftover.Length > 0)
        {
            document.Warnings.Add(new ParseWarning(name, $"Unrecognized pre-tag content: '{leftover}'."));
        }

        string description = rest.Trim();
        string? license = null;
        string? sourceUrl = null;

        var end = EndPattern().Match(rest);
        if (end.Success)
        {
            description = end.Groups["desc"].Value.Trim();
            license = NormalizeLicense(end.Groups["license"].Value.Trim());
            if (end.Groups["source"].Success)
            {
                sourceUrl = end.Groups["source"].Value.Trim();
            }
        }
        else
        {
            document.Warnings.Add(new ParseWarning(name, "Entry does not end with a `License` tag."));
        }

        return new ParsedEntry
        {
            Name = name,
            Slug = UniqueSlug(usedSlugs, Slug.Slugify(name)),
            Url = url,
            Description = description,
            License = license,
            SourceUrl = sourceUrl,
            IsRecommended = preTags.Contains("✨", StringComparison.Ordinal),
            HasPaid = preTags.Contains("`Paid`", StringComparison.Ordinal),
            HasIap = preTags.Contains("`IAP`", StringComparison.Ordinal),
            HasAds = preTags.Contains("`Ads`", StringComparison.Ordinal),
            TrialDays = trialDays,
            RequiresRoot = preTags.Contains("`Root`", StringComparison.Ordinal),
        };
    }

    private static int? ParseTrialDays(ParsedDocument document, string entryName, string preTags)
    {
        var trial = TrialPattern().Match(preTags);
        if (!trial.Success)
        {
            return null;
        }

        if (!trial.Groups["unit"].Value.StartsWith("day", StringComparison.OrdinalIgnoreCase))
        {
            document.Warnings.Add(new ParseWarning(entryName, $"Unsupported trial unit '{trial.Groups["unit"].Value}'; only day-based trials are tracked."));
            return null;
        }

        return int.TryParse(trial.Groups["days"].Value, out var days) ? days : null;
    }

    private static string NormalizeLicense(string license) =>
        license.Equals("Propietary", StringComparison.Ordinal) ? "Proprietary" : license;

    private static ParsedCategory EnsureCategory(
        ParsedDocument document,
        HashSet<string> usedSlugs,
        string section,
        string name,
        string? subcategory)
    {
        var existing = document.Categories.FirstOrDefault(c =>
            c.Section == section && c.Name == name && c.Subcategory == subcategory);
        if (existing is not null)
        {
            return existing;
        }

        var slugBase = subcategory is null ? name : $"{name} {subcategory}";
        var category = new ParsedCategory
        {
            Section = section,
            Name = name,
            Subcategory = subcategory,
            Slug = UniqueSlug(usedSlugs, Slug.Slugify(slugBase)),
        };
        document.Categories.Add(category);
        return category;
    }

    private static string UniqueSlug(HashSet<string> usedSlugs, string candidate)
    {
        if (usedSlugs.Add(candidate))
        {
            return candidate;
        }

        var i = 2;
        while (!usedSlugs.Add($"{candidate}-{i}"))
        {
            i++;
        }

        return $"{candidate}-{i}";
    }

    private static string GetHeadingText(HeadingBlock heading)
    {
        if (heading.Inline is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var literal in heading.Inline.Descendants().OfType<LiteralInline>())
        {
            sb.Append(literal.Content.ToString());
        }

        return sb.ToString().Trim();
    }
}
