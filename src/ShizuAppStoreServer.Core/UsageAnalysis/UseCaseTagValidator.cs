using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>
/// A novel use case the classifier proposed but the vocabulary does not cover.
/// Slug is normalized and stable; it is the dedupe key for candidates.
/// </summary>
public sealed record UseCaseProposal(string Slug, string Name, string? Reason);

/// <summary>
/// Validated classifier answer: vocabulary slugs assigned to the app plus
/// proposals for capabilities the vocabulary lacks.
/// </summary>
public sealed record UseCaseTaggingReport(
    IReadOnlyList<string> UseCases,
    IReadOnlyList<UseCaseProposal> Proposals);

/// <summary>
/// Accepts or repairs the tag classifier's answer. Assigned tags must come
/// from the active vocabulary; proposals are normalized, deduplicated and
/// never duplicate a vocabulary slug. An invalid answer returns null so the
/// caller can ask for a correction; when that also fails the previous tags
/// stay untouched.
/// </summary>
public static partial class UseCaseTagValidator
{
    public const int MaxProposalNameChars = 60;
    public const int MaxProposalReasonChars = 200;
    public const int MaxSlugChars = 64;

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugSeparator();

    /// <summary>
    /// Parses and validates the model output. Unknown slugs are rejected
    /// rather than dropped: a wrong slug is a contract violation and the
    /// correction round can fix it with the vocabulary in context.
    /// </summary>
    public static UseCaseTaggingReport? Validate(
        string? content,
        IReadOnlyCollection<string> allowedSlugs,
        int maxTags,
        int maxProposals,
        out string? error)
    {
        error = null;
        var root = UsageReportValidator.ExtractJson(content);
        if (root is null)
        {
            error = "response was not a JSON object with the fields 'use_cases' and 'proposed_use_cases'";
            return null;
        }

        if (root["use_cases"] is not JsonArray tagArray)
        {
            error = "'use_cases' must be an array of vocabulary slugs";
            return null;
        }

        var allowed = new HashSet<string>(allowedSlugs, StringComparer.Ordinal);
        var tags = new List<string>();
        foreach (var item in tagArray)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var raw))
            {
                error = "'use_cases' entries must be strings";
                return null;
            }

            var slug = raw.Trim().ToLowerInvariant();
            if (!allowed.Contains(slug))
            {
                error = $"'{slug}' is not an active use case; use only the provided vocabulary";
                return null;
            }

            if (!tags.Contains(slug, StringComparer.Ordinal))
            {
                tags.Add(slug);
            }
        }

        if (tags.Count > maxTags)
        {
            tags = tags.Take(maxTags).ToList();
        }

        var proposals = new List<UseCaseProposal>();
        if (root["proposed_use_cases"] is JsonArray proposalArray)
        {
            foreach (var item in proposalArray)
            {
                if (item is not JsonObject proposal)
                {
                    continue;
                }

                var rawName = proposal["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name)
                    ? UsageReportValidator.NormalizeText(name)
                    : string.Empty;
                if (rawName.Length == 0)
                {
                    continue;
                }

                if (rawName.Length > MaxProposalNameChars)
                {
                    rawName = rawName[..MaxProposalNameChars].TrimEnd();
                }

                var slug = Slugify(rawName);
                if (slug.Length == 0
                    || allowed.Contains(slug)
                    || proposals.Any(p => string.Equals(p.Slug, slug, StringComparison.Ordinal)))
                {
                    continue;
                }

                var reason = proposal["reason"] is JsonValue reasonValue && reasonValue.TryGetValue<string>(out var rawReason)
                    ? UsageReportValidator.NormalizeText(rawReason)
                    : null;
                if (reason is not null)
                {
                    reason = reason.Length > MaxProposalReasonChars
                        ? reason[..MaxProposalReasonChars].TrimEnd()
                        : reason;
                    if (reason.Length == 0)
                    {
                        reason = null;
                    }
                }

                proposals.Add(new UseCaseProposal(slug, rawName, reason));
                if (proposals.Count >= maxProposals)
                {
                    break;
                }
            }
        }

        return new UseCaseTaggingReport(tags, proposals);
    }

    /// <summary>Lowercase dashed slug; the canonical key for a proposed tag.</summary>
    public static string Slugify(string value)
    {
        var slug = SlugSeparator().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length > MaxSlugChars ? slug[..MaxSlugChars].TrimEnd('-') : slug;
    }
}
