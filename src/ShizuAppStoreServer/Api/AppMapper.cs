using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Api;

/// <summary>Entity → DTO projections shared by the apps and changes controllers.</summary>
public static class AppMapper
{
    /// <summary>Default candidate for clients without an installed-signature match.</summary>
    public static AppDownload? Primary(App a) => a.Downloads.FirstOrDefault(d => d.IsPrimary);

    /// <summary>APK-derived display name when known, else the list name.</summary>
    public static string DisplayName(App a) => a.DisplayName ?? a.Name;

    public static AppSummaryDto ToSummary(App a)
    {
        var primary = Primary(a);
        return new(
            a.Slug,
            DisplayName(a),
            a.Description,
            a.License,
            ApiEnums.ToApiString(a.Listing),
            ApiEnums.ToApiString(a.Type),
            a.IsRecommended,
            a.HasPaid,
            a.HasIap,
            a.HasAds,
            a.TrialDays,
            a.RequiresRoot,
            ApiEnums.ToApiString(a.Availability),
            a.PackageName,
            primary?.VersionCode,
            primary?.VersionName ?? a.VersionName,
            primary?.MinSdk,
            primary?.SizeBytes,
            a.IconHash,
            a.IconAdaptive,
            a.Category?.Slug ?? string.Empty,
            a.UpdatedAt,
            primary?.SigSha256,
            primary?.SigMd5,
            a.Stars,
            a.DownloadTotal,
            a.InstallCount,
            a.VersionUpdatedAt,
            a.ListUpdatedAt,
            a.AuthorKey,
            a.AuthorName,
            SourceName(a),
            primary?.TargetSdk,
            primary?.CompileSdk,
            primary?.Locales.Count,
            primary?.Abis ?? [],
            SummaryLabels(primary?.LocalizedLabels, a.ApkLabel ?? DisplayName(a)),
            primary?.DhizukuDeclared ?? false,
            primary?.Trackers ?? [],
            TagGroups(primary?.TrackerTags));
    }

    /// <summary>
    /// Detail projection. <paramref name="path"/> is the root→leaf category
    /// chain (built by the caller, which already holds the category rows).
    /// </summary>
    public static AppDetailDto ToDetail(App a, IReadOnlyList<CategoryPathDto> path)
    {
        var primary = Primary(a);
        return new(
            a.Slug,
            DisplayName(a),
            a.Description,
            a.License,
            ApiEnums.ToApiString(a.Listing),
            ApiEnums.ToApiString(a.Type),
            a.IsRecommended,
            a.HasPaid,
            a.HasIap,
            a.HasAds,
            a.TrialDays,
            a.RequiresRoot,
            ApiEnums.ToApiString(a.Availability),
            a.PackageName,
            primary?.VersionCode,
            primary?.VersionName ?? a.VersionName,
            primary?.MinSdk,
            a.IconHash,
            a.IconAdaptive,
            a.Category?.Slug ?? string.Empty,
            a.UpdatedAt,
            a.Url,
            a.SourceUrl,
            ApiEnums.ToApiString(a.SourceKind),
            a.Downloads
                .OrderByDescending(d => d.IsPrimary)
                .ThenByDescending(d => d.VersionCode ?? -1)
                .Select(ToDownload)
                .ToList(),
            a.StoreUrl,
            a.ExcludedReason,
            path,
            a.Parent?.Slug,
            a.AddedAt,
            a.LastCheckedAt,
            a.Stars,
            a.DownloadTotal,
            a.InstallCount,
            a.VersionUpdatedAt,
            a.ListUpdatedAt,
            a.AuthorName,
            a.AuthorUrl,
            a.Permissions,
            a.FullDescription,
            a.Changelog,
            a.ChangelogUrl,
            a.Screenshots,
            SourceName(a),
            primary?.TargetSdk,
            primary?.CompileSdk,
            primary?.Locales.Count,
            primary?.Locales ?? [],
            primary?.Abis ?? [],
            a.UsageShort,
            a.UsageMarkdown,
            a.UsageAnalyzedAt,
            primary?.DhizukuDeclared ?? false,
            primary?.Trackers ?? [],
            TagGroups(primary?.TrackerTags));
    }

    /// <summary>
    /// Human-readable origin for the detail subtitle. Play redirects report the
    /// store even when the list entry points at a forge repository.
    /// </summary>
    private static string SourceName(App a) => a.Availability == Availability.PlayRedirect
        ? "Play Store"
        : a.SourceKind.GetDescription();

    /// <summary>
    /// Groups stored <c>tracker:tag</c> pairs by tracker name, preserving the
    /// recorded order. Entries without a name or tag are skipped.
    /// </summary>
    private static IReadOnlyList<TrackerTagDto> TagGroups(IReadOnlyList<string>? pairs)
    {
        if (pairs is null || pairs.Count == 0)
        {
            return [];
        }

        List<string>? names = null;
        Dictionary<string, List<string>>? byName = null;
        foreach (var pair in pairs)
        {
            var separator = pair.LastIndexOf(':');
            if (separator <= 0 || separator == pair.Length - 1)
            {
                continue;
            }

            var name = pair[..separator];
            var tag = pair[(separator + 1)..];
            byName ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
            names ??= [];
            if (!byName.TryGetValue(name, out var tags))
            {
                names.Add(name);
                byName[name] = tags = [];
            }

            tags.Add(tag);
        }

        if (names is null || byName is null)
        {
            return [];
        }

        return names.Select(name => new TrackerTagDto(name, byName[name])).ToList();
    }

    private static DownloadDto ToDownload(AppDownload d) => new(
        ApiEnums.ToApiString(d.Source),
        d.PackageName,
        FdroidRepos.ClientDownloadUrl(d.ApkUrl),
        d.ArchiveEntry,
        d.VersionCode,
        d.VersionName,
        d.SizeBytes,
        d.Sha256,
        d.SigSha256,
        d.SigMd5,
        d.MinSdk,
        d.Abi,
        d.IsPrimary,
        d.TargetSdk,
        d.CompileSdk,
        d.Locales,
        d.Abis,
        LocalizedLabelMap(d.LocalizedLabels),
        d.SignerDn,
        d.SignerScheme,
        d.SignerKeyAlgorithm,
        d.DhizukuDeclared,
        d.Trackers,
        TagGroups(d.TrackerTags));

    /// <summary>
    /// Summary-safe label map: only locales whose label differs from the APK's
    /// default label, the name a device without a matching locale would show.
    /// Variant entries carry a suffixed display name, so comparing against the
    /// display name would leak every unchanged label into list payloads.
    /// </summary>
    private static IReadOnlyDictionary<string, string> SummaryLabels(IReadOnlyList<string>? entries, string defaultLabel)
    {
        var labels = LocalizedLabelMap(entries ?? []);
        var differing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (locale, label) in labels)
        {
            if (label.Length > 0 && !string.Equals(label, defaultLabel, StringComparison.Ordinal))
            {
                differing[locale] = label;
            }
        }

        return differing;
    }

    /// <summary>
    /// Decodes the stored <c>locale=label</c> entries; a malformed entry
    /// (no separator) is skipped rather than exposed as a keyless label.
    /// </summary>
    private static IReadOnlyDictionary<string, string> LocalizedLabelMap(IReadOnlyList<string> entries)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                continue;
            }

            map.TryAdd(entry[..separator], entry[(separator + 1)..]);
        }

        return map;
    }

    /// <summary>Root→leaf <c>(slug, name)</c> chain for a category (max depth 2 in real data).</summary>
    public static IReadOnlyList<CategoryPathDto> CategoryPath(Category? category)
    {
        if (category is null)
        {
            return [];
        }

        var chain = new List<CategoryPathDto>();
        for (var c = category; c is not null; c = c.Parent)
        {
            chain.Add(new CategoryPathDto(c.Slug, c.Name));
        }

        chain.Reverse();
        return chain;
    }

    public static RemovedAppDto ToRemoved(RemovedApp t) => new(t.Slug, t.Name, t.RemovedAt);
}
