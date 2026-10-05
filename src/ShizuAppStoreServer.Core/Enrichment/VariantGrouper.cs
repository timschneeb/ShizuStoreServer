using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Parsing;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment;

internal sealed class VariantGrouper(
    ShizuDbContext db,
    DownloadStore downloadStore,
    IconPipeline icons,
    ILogger? log)
{
    /// <summary>
    /// Drops operator-excluded packages (table <c>app_download_exclusions</c>)
    /// from the release before it is grouped, and deletes the stale candidates
    /// already stored for them. A root whose stored identity is excluded is
    /// cleared so the surviving group becomes the entry instead of a variant.
    /// When everything would be excluded the release is kept unchanged: an
    /// operator typo must never empty a row.
    /// </summary>
    internal async Task<IReadOnlyList<ArtifactAnalysis>> ApplyDownloadExclusionsAsync(
        App root, IReadOnlyList<ArtifactAnalysis> analyses, CancellationToken ct)
    {
        var excluded = await LoadExcludedPackagesAsync(root.Slug, ct);
        if (excluded.Count == 0)
        {
            return analyses;
        }

        var kept = analyses.Where(a => !IsExcluded(excluded, a.Badging.PackageName)).ToList();
        if (kept.Count == 0)
        {
            // A partial pass can re-scan only the excluded artifact while its
            // surviving sibling stays recorded and Complete, so re-applying the
            // excluded analysis would silently undo the operator's exclusion.
            // Keep the stored state in that case. Only when no non-excluded
            // candidate is recorded is the release genuinely all-excluded
            // (operator typo), and then it stays applied unchanged so the row
            // never goes empty.
            var stored = await downloadStore.LoadGroupDownloadsAsync(root, ct);
            return stored.Any(d => !IsExcluded(excluded, d.PackageName)) ? [] : analyses;
        }

        foreach (var download in await downloadStore.LoadGroupDownloadsAsync(root, ct))
        {
            if (IsExcluded(excluded, download.PackageName))
            {
                db.Downloads.Remove(download);
            }
        }

        // The stored package is the root-group selector: pointing at an
        // excluded package would keep the removed group alive, so clear it and
        // let SelectRootGroup fall back to the group that survives.
        if (IsExcluded(excluded, root.PackageName))
        {
            root.PackageName = null;
            root.ApkLabel = null;
        }

        // A variant whose only package was excluded must not linger as an
        // empty entry; drop and tombstone it like a vanished variant.
        foreach (var variant in await EnrichmentQueries.LoadVariantGroupAsync(db, root, ct))
        {
            if (!IsExcluded(excluded, variant.PackageName)
                || (await downloadStore.LoadDownloadsAsync(variant, ct)).Count > 0)
            {
                continue;
            }

            var icon = variant.IconHash;
            variant.IconHash = null;
            if (icon is not null)
            {
                await icons.DeleteIconIfOrphanedAsync(variant, icon, ct);
            }

            await WriteRemovedTombstoneAsync(variant, ct);
            db.Apps.Remove(variant);
        }

        return kept;
    }

    private async Task<HashSet<string>> LoadExcludedPackagesAsync(string slug, CancellationToken ct)
    {
        var packages = await db.AppDownloadExclusions.AsNoTracking()
            .Where(x => x.AppSlug == slug)
            .Select(x => x.PackageName)
            .ToListAsync(ct);
        return new HashSet<string>(packages, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExcluded(HashSet<string> excluded, string? packageName) =>
        !string.IsNullOrEmpty(packageName) && excluded.Contains(packageName);

    /// <summary>
    /// One app name's artifacts within a release. Same label means the same
    /// user-facing app; a blank label cannot group and stays one group per
    /// package.
    /// </summary>
    internal sealed record LabelGroup(string? Label, List<ArtifactAnalysis> Items);

    internal static List<LabelGroup> GroupByLabel(IReadOnlyList<ArtifactAnalysis> analyses)
    {
        var groups = new List<LabelGroup>();
        foreach (var analysis in analyses)
        {
            var label = NormalizeLabel(analysis.Badging.ApplicationLabel);
            var group = label is null
                ? null
                : groups.FirstOrDefault(g => string.Equals(g.Label, label, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                group = new LabelGroup(label, []);
                groups.Add(group);
            }

            group.Items.Add(analysis);
        }

        return groups;
    }

    private static string? NormalizeLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    internal static List<string> GroupPackages(LabelGroup group) =>
        group.Items.Select(a => a.Badging.PackageName)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The group that owns the list entry: the stored APK label, else the group
    /// holding the list URL's package, else the root package, else the group
    /// carrying the broadest (base) package. The list entry is normally the
    /// base app; flavors hang off it.
    /// </summary>
    internal static LabelGroup? SelectRootGroup(App root, List<LabelGroup> groups, string? listPackage)
    {
        if (groups.Count == 0)
        {
            return null;
        }

        var current = NormalizeLabel(root.ApkLabel);
        var match = current is null
            ? null
            : groups.FirstOrDefault(g => string.Equals(g.Label, current, StringComparison.OrdinalIgnoreCase));
        match ??= listPackage is null ? null : GroupWithPackage(groups, listPackage);

        // A stored package is the root's identity: when the release no longer
        // carries it, every group is a different app, so the root stays
        // unclaimed until HealRootPresentationAsync refills it from its primary.
        if (!string.IsNullOrEmpty(root.PackageName))
        {
            return match ?? GroupWithPackage(groups, root.PackageName);
        }

        // First enrich: nothing stored yet, so the broadest group is the list app.
        var basePackage = FindBasePackage(groups.SelectMany(g => GroupPackages(g)).ToList());
        match ??= basePackage is null ? null : GroupWithPackage(groups, basePackage);
        return match ?? groups[0];
    }

    private static LabelGroup? GroupWithPackage(List<LabelGroup> groups, string packageName) =>
        groups.FirstOrDefault(g => g.Items.Any(a =>
            string.Equals(a.Badging.PackageName, packageName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The package that presents a group: the list URL's package when present,
    /// else the dot-prefix base (the flavor parent), else the package whose id
    /// names the app (label/repo token, e.g. <c>com.dergoogler.mmrl</c> over an
    /// obfuscated spoof build), else the first.
    /// </summary>
    internal static string? ResolveCanonicalPackage(
        App root, string? label, IReadOnlyList<string> packages, string? listPackage)
    {
        if (packages.Count <= 1)
        {
            return packages.FirstOrDefault();
        }

        if (listPackage is not null)
        {
            var listed = packages.FirstOrDefault(p => string.Equals(p, listPackage, StringComparison.OrdinalIgnoreCase));
            if (listed is not null)
            {
                return listed;
            }
        }

        var basePackage = FindBasePackage(packages);
        if (basePackage is not null)
        {
            return basePackage;
        }

        foreach (var token in IdentityTokens(root, label))
        {
            var tokenMatch = packages.FirstOrDefault(p => p.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (tokenMatch is not null)
            {
                return tokenMatch;
            }
        }

        return packages[0];
    }

    /// <summary>
    /// The shortest package that prefixes every other with a dot (the flavor
    /// parent: <c>app.mihon</c> for <c>app.mihon.foss</c>). Candidates must be
    /// members, so an unrelated short id cannot win.
    /// </summary>
    private static string? FindBasePackage(IReadOnlyList<string> packages) =>
        packages
            .Where(p => packages.All(q => string.Equals(p, q, StringComparison.OrdinalIgnoreCase)
                || q.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Length)
            .ThenBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();

    private static IEnumerable<string> IdentityTokens(App root, string? label)
    {
        foreach (var raw in new[] { label, RepoName(root.Url), RepoName(root.SourceUrl) })
        {
            var token = NormalizeToken(raw);
            if (token is { Length: >= 3 })
            {
                yield return token;
            }
        }
    }

    private static string? NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? RepoName(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()?.Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase)
            : null;

    /// <summary>
    /// Package id a list entry's own URL targets (F-Droid or Play), the
    /// strongest signal for which package the entry means.
    /// </summary>
    internal static string? ListEndpointPackage(App root)
    {
        if (SourceClassifier.TryParseFdroidPackage(root.Url, out var fdroid)
            || SourceClassifier.TryParseFdroidPackage(root.SourceUrl, out fdroid))
        {
            return fdroid;
        }

        if (SourceClassifier.TryParsePlayPackage(root.Url, out var play)
            || SourceClassifier.TryParsePlayPackage(root.SourceUrl, out play))
        {
            return play;
        }

        return null;
    }

    /// <summary>
    /// Folds pre-flavor-grouping variant rows back into the root. A variant
    /// whose stored APK label matches the root's is a flavor of the same app:
    /// its candidates move onto the root (each keeping its package), the row is
    /// deleted and tombstoned so cached clients drop it. Root <c>UpdatedAt</c>
    /// bumps so those clients also refetch the merged entry.
    /// </summary>
    internal async Task MergeSameLabelVariantsAsync(
        App root, string? rootLabel, string? listPackage, DateTimeOffset now, CancellationToken ct)
    {
        var label = rootLabel ?? NormalizeLabel(root.ApkLabel);
        if (label is null)
        {
            return;
        }

        var variants = await EnrichmentQueries.LoadVariantGroupAsync(db, root, ct);
        if (variants.Count == 0)
        {
            return;
        }

        var packages = new List<string>();
        foreach (var variant in variants)
        {
            if (!string.IsNullOrEmpty(variant.PackageName))
            {
                packages.Add(variant.PackageName);
            }
        }

        foreach (var download in await downloadStore.LoadDownloadsAsync(root, ct))
        {
            if (!string.IsNullOrEmpty(download.PackageName))
            {
                packages.Add(download.PackageName);
            }
        }

        if (!string.IsNullOrEmpty(root.PackageName))
        {
            packages.Add(root.PackageName);
        }

        var canonical = ResolveCanonicalPackage(root, label, packages.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), listPackage);

        var folded = false;
        foreach (var variant in variants)
        {
            if (!string.Equals(NormalizeLabel(variant.ApkLabel), label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var download in await downloadStore.LoadDownloadsAsync(variant, ct))
            {
                download.App = root;
                download.AppId = root.Id;
                download.PackageName ??= variant.PackageName;
            }

            var icon = variant.IconHash;
            variant.IconHash = null;
            if (icon is not null)
            {
                await icons.DeleteIconIfOrphanedAsync(variant, icon, ct);
            }

            await WriteRemovedTombstoneAsync(variant, ct);
            db.Apps.Remove(variant);
            folded = true;
        }

        if (!folded)
        {
            return;
        }

        if (canonical is not null && !string.Equals(root.PackageName, canonical, StringComparison.OrdinalIgnoreCase))
        {
            root.PackageName = canonical;
        }

        // Folding changes the entry's candidates; refresh cached clients.
        root.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task WriteRemovedTombstoneAsync(App app, CancellationToken ct)
    {
        var existing = db.RemovedApps.Local.FirstOrDefault(t => t.Slug == app.Slug)
            ?? await db.RemovedApps.FirstOrDefaultAsync(t => t.Slug == app.Slug, ct);
        if (existing is null)
        {
            existing = new RemovedApp { Slug = app.Slug };
            db.RemovedApps.Add(existing);
        }

        existing.Name = app.Name;
        existing.Listing = app.Listing;
        // Commit time, not the pass start: a client that synced mid-pass must
        // still see the removal, the same reason the Shizuku gate stamps so.
        existing.RemovedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Drops TV and Wear OS artifacts when the same package also ships a phone
    /// build. A package that only ships a TV or watch build is kept: then that
    /// form factor is the app. Grouped by package so a multi-app repo's phone
    /// variant never suppresses another package's watch-only variant.
    /// </summary>
    internal static List<ArtifactAnalysis> PreferPhoneAnalyses(IReadOnlyList<ArtifactAnalysis> analyses)
    {
        var kept = new List<ArtifactAnalysis>(analyses.Count);
        foreach (var group in analyses.GroupBy(a => a.Badging.PackageName ?? string.Empty, StringComparer.Ordinal))
        {
            var hasPhone = group.Any(a => !a.Badging.IsTvFormFactor && !a.Badging.IsWearFormFactor);
            foreach (var analysis in group)
            {
                if (hasPhone && (analysis.Badging.IsTvFormFactor || analysis.Badging.IsWearFormFactor))
                {
                    continue;
                }

                kept.Add(analysis);
            }
        }

        return kept;
    }

    internal static string BuildDisplayName(App member, App root, bool multi)
    {
        var label = string.IsNullOrWhiteSpace(member.ApkLabel) ? member.Name : member.ApkLabel;
        // The parenthetical only disambiguates; drop it when the APK label is
        // already the list name (naming a row "DroidOS (DroidOS)" helps nobody).
        return multi && !string.Equals(label, root.Name, StringComparison.OrdinalIgnoreCase)
            ? $"{label} ({root.Name})"
            : label;
    }

    /// <summary>
    /// The row for a non-root app name (one repo, several distinct apps). A
    /// new name becomes a variant row that mirrors the list entry's metadata
    /// and points back at it; its package identifies it across passes.
    /// </summary>
    internal async Task<App?> EnsureVariantAsync(
        App root, SourceKind kind, string? packageName,
        IReadOnlyDictionary<string, SiblingClaim> siblingClaims, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(packageName))
        {
            return root;
        }

        var variants = await EnrichmentQueries.LoadVariantGroupAsync(db, root, ct);
        var existing = variants.FirstOrDefault(v =>
            string.Equals(v.PackageName, packageName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        if (siblingClaims.TryGetValue(packageName, out var owner))
        {
            // A live entry sharing this source repo already serves the
            // package; a second listing would overwrite it on install.
            log?.LogInformation(
                "Skipping variant {Package} for {Root}: already served by {Owner}.",
                packageName, root.Slug, owner.Owner.Slug);
            return null;
        }

        var variant = new App
        {
            Slug = await UniqueVariantSlugAsync(root, packageName, ct),
            Name = root.Name,
            DisplayName = root.Name,
            Url = root.Url,
            Description = root.Description,
            License = root.License,
            Listing = root.Listing,
            Type = root.Type,
            SourceUrl = root.SourceUrl,
            SourceKind = kind,
            CategoryId = root.CategoryId,
            ParentId = root.ParentId,
            AuthorName = root.AuthorName,
            AuthorUrl = root.AuthorUrl,
            AuthorKey = root.AuthorKey,
            Stars = root.Stars,
            DownloadTotal = root.DownloadTotal,
            FullDescription = root.FullDescription,
            ReadmeUrl = root.ReadmeUrl,
            Changelog = root.Changelog,
            ChangelogUrl = root.ChangelogUrl,
            AddedAt = now,
            UpdatedAt = now,
            Root = root,
            PackageName = packageName,
        };

        // Add before wiring downloads: EF assigns a distinct temporary key so
        // each new variant's downloads stay separated in the change tracker.
        db.Apps.Add(variant);
        return variant;
    }

    internal sealed record SiblingClaim(App Owner, bool RootPackage);

    /// <summary>
    /// Packages already served by live list entries that share this root's
    /// source repo, mapped to the owning entry. A sibling's own row always
    /// owns its package; its variants count too, because the sibling pass
    /// would otherwise mirror the package back.
    /// </summary>
    internal async Task<Dictionary<string, SiblingClaim>> LoadSiblingPackageClaimsAsync(
        App root, CancellationToken ct)
    {
        var claims = new Dictionary<string, SiblingClaim>(StringComparer.OrdinalIgnoreCase);
        foreach (var sibling in await LoadSiblingRootsAsync(root, ct))
        {
            if (!string.IsNullOrEmpty(sibling.PackageName))
            {
                claims.TryAdd(sibling.PackageName, new SiblingClaim(sibling, RootPackage: true));
            }

            foreach (var variant in await EnrichmentQueries.LoadVariantGroupAsync(db, sibling, ct))
            {
                if (!string.IsNullOrEmpty(variant.PackageName))
                {
                    claims.TryAdd(variant.PackageName, new SiblingClaim(sibling, RootPackage: false));
                }
            }
        }

        return claims;
    }

    /// <summary>
    /// Live (non-excluded) roots whose list entry targets the same source repo
    /// as this one, tracked or just added in the current pass. One repo can
    /// ship several apps, so sibling entries are legitimate; they only must
    /// not mirror each other's packages.
    /// </summary>
    private async Task<List<App>> LoadSiblingRootsAsync(App root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root.SourceUrl))
        {
            return [];
        }

        var source = NormalizeSourceUrl(root.SourceUrl);
        var siblings = new List<App>();
        var rows = await db.Apps.AsNoTracking()
            .Where(a => a.Id != root.Id && a.RootAppId == null && a.SourceUrl != null)
            .ToListAsync(ct);
        foreach (var app in rows)
        {
            if (app.Availability != Availability.Excluded
                && string.Equals(NormalizeSourceUrl(app.SourceUrl!), source, StringComparison.OrdinalIgnoreCase))
            {
                siblings.Add(app);
            }
        }

        foreach (var local in db.Apps.Local)
        {
            if (local.Id != root.Id
                && local.RootAppId == null
                && local.SourceUrl is not null
                && local.Availability != Availability.Excluded
                && db.Entry(local).State != EntityState.Deleted
                && string.Equals(NormalizeSourceUrl(local.SourceUrl), source, StringComparison.OrdinalIgnoreCase)
                && !siblings.Contains(local))
            {
                siblings.Add(local);
            }
        }

        return siblings;
    }

    private static string NormalizeSourceUrl(string sourceUrl) => sourceUrl.TrimEnd('/');

    /// <summary>
    /// Drops variants whose package a sibling entry sharing this source repo
    /// already serves. A list entry always keeps its own package; when two
    /// siblings both mirrored one unlisted package, the lower root id wins so
    /// both passes agree on the owner.
    /// </summary>
    internal async Task PruneSiblingOwnedVariantsAsync(
        App root, IReadOnlyDictionary<string, SiblingClaim> siblingClaims, CancellationToken ct)
    {
        foreach (var variant in await EnrichmentQueries.LoadVariantGroupAsync(db, root, ct))
        {
            if (string.IsNullOrEmpty(variant.PackageName)
                || !siblingClaims.TryGetValue(variant.PackageName, out var claim)
                || (!claim.RootPackage && claim.Owner.Id > root.Id))
            {
                continue;
            }

            log?.LogInformation(
                "Removing variant {Slug}: package {Package} is already served by {Owner}.",
                variant.Slug, variant.PackageName, claim.Owner.Slug);
            var icon = variant.IconHash;
            variant.IconHash = null;
            if (icon is not null)
            {
                await icons.DeleteIconIfOrphanedAsync(variant, icon, ct);
            }

            await WriteRemovedTombstoneAsync(variant, ct);
            db.Apps.Remove(variant);
        }
    }

    private async Task<string> UniqueVariantSlugAsync(App root, string packageName, CancellationToken ct)
    {
        var baseSlug = Slug.Slugify(packageName);
        var used = new HashSet<string>(await db.Apps.Select(a => a.Slug).ToListAsync(ct), StringComparer.Ordinal);
        foreach (var local in db.Apps.Local)
        {
            used.Add(local.Slug);
        }

        var candidate = baseSlug;
        var i = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{baseSlug}-{i++}";
        }

        return candidate.Length > 200 ? candidate[..200] : candidate;
    }

    internal async Task RemoveVanishedVariantsAsync(
        App root, IReadOnlyList<App> variants, IReadOnlyList<SourceAsset> scannedAssets, CancellationToken ct)
    {
        var scannedUrls = scannedAssets
            .Where(DownloadStore.IsApkAsset)
            .Select(a => a.Url)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            var downloads = await downloadStore.LoadDownloadsAsync(variant, ct);
            if (downloads.Count == 0 || downloads.Any(d => scannedUrls.Contains(d.ApkUrl)))
            {
                continue;
            }

            log?.LogInformation(
                "Removing variant {Slug}: package {Package} is no longer published by {Root}.",
                variant.Slug, variant.PackageName, root.Slug);
            var icon = variant.IconHash;
            variant.IconHash = null;
            if (icon is not null)
            {
                await icons.DeleteIconIfOrphanedAsync(variant, icon, ct);
            }

            await WriteRemovedTombstoneAsync(variant, ct);
            db.Apps.Remove(variant);
        }
    }
}
