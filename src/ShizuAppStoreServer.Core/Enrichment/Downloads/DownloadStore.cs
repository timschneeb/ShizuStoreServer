using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment.Apk;
using ShizuAppStoreServer.Core.Sources;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Enrichment.Downloads;

internal sealed class DownloadStore(
    ShizuDbContext db,
    IUsageAnalysisQueue? usageQueue,
    ITrackerCatalog? trackers,
    IzzyStatsProvider? izzyStats)
{
    internal async Task<List<AppDownload>> LoadGroupDownloadsAsync(App root, CancellationToken ct)
    {
        var rows = await LoadDownloadsAsync(root, ct);
        foreach (var variant in await EnrichmentQueries.LoadVariantGroupAsync(db, root, ct))
        {
            rows.AddRange(await LoadDownloadsAsync(variant, ct));
        }

        return rows;
    }

    internal static bool IsApkAsset(SourceAsset asset) =>
        asset.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
        || asset.Url.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records an index-only (not downloaded) candidate, used for the
    /// per-architecture siblings of an F-Droid package.
    /// </summary>
    internal async Task UpsertIndexAssetAsync(
        App app, SourceKind kind, SourceAsset asset, string? packageName, DateTimeOffset now, CancellationToken ct)
    {
        await UpsertDownloadAsync(app, new DownloadCandidate(
            kind,
            asset.Name,
            asset.Url,
            ArchiveEntry: null,
            VersionCode: asset.VersionCode,
            VersionName: asset.VersionName,
            SizeBytes: asset.Size,
            Sha256: asset.Sha256,
            SigSha256: null,
            SigMd5: asset.SigMd5,
            MinSdk: null,
            Abi: asset.Abi,
            PackageName: packageName), now, ct);
    }

    /// <summary>
    /// Pre-versioning rows recorded a fully analyzed build but predate one or
    /// more current extraction fields (full ABI set, localized labels, signer
    /// details); the same-asset short-circuits would keep them empty forever,
    /// so re-analyze once. Index-only rows never ran an analysis and stay
    /// untouched.
    /// </summary>
    internal static bool NeedsAnalysisHeal(AppDownload? primary) =>
        primary is not null
        && primary.Analyzed
        && primary.AnalysisVersion < AppEnricher.CurrentAnalysisVersion;

    /// <summary>
    /// Pre-fix rows recorded a fully analyzed build but never persisted its
    /// permissions (the F-Droid path never wrote them); the same-asset
    /// short-circuits would keep them blank forever, so re-analyze once. Only
    /// analyzed rows heal: index-only rows never went through a badging pass,
    /// so their empty permission list is not an error. A genuinely
    /// permission-less analyzed build re-verifies each pass; such builds are
    /// all but nonexistent.
    /// </summary>
    internal static bool NeedsPermissionHeal(App app, AppDownload? primary) =>
        primary is not null
        && primary.Analyzed
        && app.Permissions is not { Count: > 0 };

    /// <summary>
    /// True when an index-only row predates the index-v2 signer map and the
    /// fresh index entry now carries a certificate SHA-256 to backfill it.
    /// </summary>
    internal static bool NeedsSignerBackfill(AppDownload? primary, FdroidPackageInfo package) =>
        primary is not null
        && !primary.Analyzed
        && primary.SigSha256 is null
        && package.SigSha256 is not null;

    /// <summary>
    /// Pre-signals rows recorded a fully analyzed build but never scanned it
    /// for analysis signals (Dhizuku declaration, Exodus tracker code
    /// signatures); the same-asset short-circuits would keep them empty
    /// forever, so re-analyze once. The inspected flag is set by the first
    /// signal scan and never downgraded; index-only rows never ran a badging
    /// pass and stay untouched.
    /// </summary>
    internal static bool NeedsSignalHeal(AppDownload? primary) =>
        primary is not null
        && primary.Analyzed
        && !primary.Inspected;

    /// <summary>
    /// Any heal a 304 or unchanged-index short-circuit would suppress forever:
    /// a fully analyzed row missing fields the current extraction version (or
    /// an older one) never persisted. Callers use this to force one refetch so
    /// the normal analysis path can backfill them.
    /// </summary>
    internal static bool NeedsRefetch(App app, AppDownload? primary) =>
        NeedsPermissionHeal(app, primary)
        || NeedsSignalHeal(primary)
        || NeedsAnalysisHeal(primary);

    /// <summary>
    /// Legacy index-only rows have no analysis to heal but still need one
    /// forced refetch so the index-v2 signer map can give the row its file
    /// identity.
    /// </summary>
    internal static bool NeedsIndexIdentityBackfill(AppDownload? primary) =>
        primary is not null
        && !primary.Analyzed
        && primary.SigSha256 is null;

    /// <summary>
    /// Signing identity of a candidate: the first SHA-256 token, else the
    /// first MD5 token, else a URL-derived fallback for fingerprintless rows.
    /// The downloads snapshot keeps one row per identity (newest build only).
    /// </summary>
    internal static string ComputeSigKey(string? sigSha256, string? sigMd5, string apkUrl)
    {
        var key = FirstFingerprint(sigSha256) ?? FirstFingerprint(sigMd5);
        return key ?? "url:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apkUrl)));
    }

    private static string? FirstFingerprint(string? value) =>
        value?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();

    /// <summary>
    /// Encodes localized labels as <c>locale=label</c> entries for the
    /// newline-joined column, preserving dictionary order.
    /// </summary>
    internal static List<string> LocalizedLabelEntries(IReadOnlyDictionary<string, string> labels) =>
        labels.Select(kv => $"{kv.Key}={kv.Value}").ToList();

    /// <summary>
    /// Records an analyzed artifact on the row so flavor twins of the same
    /// version are skipped without another download or AI analysis. Entries are
    /// <c>sha256 url</c>; a repeated URL replaces its checksum.
    /// </summary>
    internal static void RememberArtifact(AppDownload row, DownloadCandidate candidate) =>
        RememberArtifact(row, candidate.Sha256, candidate.ApkUrl);

    internal static void RememberArtifact(AppDownload row, string? sha, string url)
    {
        if (sha is not { Length: > 0 })
        {
            return;
        }

        row.AnalyzedArtifacts.RemoveAll(entry =>
            RecordedArtifacts.Parse(entry) is { } parsed && string.Equals(parsed.Url, url, StringComparison.Ordinal));
        row.AnalyzedArtifacts.Add($"{sha} {url}");
    }

    /// <summary>
    /// Rank for same-version flavor twins: stable release builds beat debug,
    /// test, beta and terminal builds; equal rank keeps the incumbent. Matches
    /// whole filename tokens so a name like <c>app-latest.apk</c> does not
    /// count as a test build.
    /// </summary>
    private static int ArtifactRank(string url)
    {
        var name = url[(url.LastIndexOf('/') + 1)..];
        var tokens = name.Split(
            ['-', '_', '.', ' ', '+', '(', ')', '[', ']'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var rank = 0;
        if (tokens.Any(t => t.Equals("release", StringComparison.OrdinalIgnoreCase)))
        {
            rank += 2;
        }

        if (tokens.Any(t => t.Equals("debug", StringComparison.OrdinalIgnoreCase)
            || t.Equals("test", StringComparison.OrdinalIgnoreCase)
            || t.Equals("beta", StringComparison.OrdinalIgnoreCase)
            || t.Equals("terminal", StringComparison.OrdinalIgnoreCase)))
        {
            rank -= 2;
        }

        return rank;
    }

    /// <summary>Ranks by the file the client installs, the archive entry when
    /// the APK is served inside a zip.</summary>
    private static int ArtifactRank(AppDownload row) =>
        ArtifactRank(row.ArchiveEntry is { Length: > 0 } entry ? entry : row.ApkUrl);

    /// <summary>
    /// A release newer than the one already recorded means a version bump may
    /// still be pending even when the artifact bytes match, so the checksum
    /// short-circuits must not fire.
    /// </summary>
    internal static bool ReleasedNewerThan(DateTimeOffset? releasedAt, App row) =>
        releasedAt is not null && (row.VersionUpdatedAt is null || releasedAt > row.VersionUpdatedAt);

    // IzzyOnDroid hosts the developers' own upstream builds (same signing key
    // as forge releases); only f-droid.org rebuilds use a different key.
    private static bool IsForgeSource(SourceKind kind) => kind != SourceKind.FDroid;

    private static int SourceOrder(SourceKind kind) => kind switch
    {
        SourceKind.GitHub => 0,
        SourceKind.GitLab => 1,
        SourceKind.Izzy => 2,
        SourceKind.Codeberg => 3,
        SourceKind.Other => 4,
        SourceKind.FDroid => 5,
        _ => 6,
    };

    internal async Task<List<AppDownload>> LoadDownloadsAsync(App app, CancellationToken ct)
    {
        // A variant created in this pass has no database key yet, so its
        // pending rows are matched by navigation rather than by AppId.
        var rows = app.Id == 0
            ? []
            : await db.Downloads.Where(d => d.AppId == app.Id).ToListAsync(ct);
        foreach (var local in db.Downloads.Local)
        {
            if (!rows.Contains(local) && IsForApp(local, app))
            {
                rows.Add(local);
            }
        }

        // A row marked for removal must not count as a candidate: the primary
        // recompute would otherwise pick it and persist no live primary.
        return rows.Where(r => db.Entry(r).State != EntityState.Deleted).ToList();
    }

    private static bool IsForApp(AppDownload row, App app) =>
        app.Id != 0 ? row.AppId == app.Id : ReferenceEquals(row.App, app);

    internal async Task<AppDownload?> PrimaryDownloadAsync(App app, CancellationToken ct) =>
        (await LoadDownloadsAsync(app, ct)).FirstOrDefault(d => d.IsPrimary);

    internal async Task<bool> HasDownloadsAsync(App app, CancellationToken ct) =>
        await db.Downloads.AnyAsync(d => d.AppId == app.Id, ct);

    /// <summary>
    /// Upserts the row for the candidate's signing identity. Newer versions
    /// replace older ones; on a version tie the forge source's URL wins. A
    /// row is matched by signing key, then by MD5, then by the APK's SHA-256,
    /// so an index-only twin without a SHA-256 identity merges with its
    /// analyzed counterpart instead of duplicating it.
    /// </summary>
    internal async Task UpsertDownloadAsync(App app, DownloadCandidate candidate, DateTimeOffset now, CancellationToken ct)
    {
        var sigKey = ComputeSigKey(candidate.SigSha256, candidate.SigMd5, candidate.ApkUrl);
        var md5 = FirstFingerprint(candidate.SigMd5);
        var rows = await LoadDownloadsAsync(app, ct);
        // Package, signing identity and ABI are the key: one release can ship
        // several per-arch APKs that share a signing identity, and one app row
        // can carry several flavor packages (FOSS vs Play, debug vs release).
        // An unknown package (index-only asset) matches any; a null-package row
        // is legacy data that adopts the package instead of gaining a twin.
        bool SamePackage(string? value) =>
            candidate.PackageName is null || string.Equals(value, candidate.PackageName, StringComparison.OrdinalIgnoreCase);
        var row = rows.FirstOrDefault(d => SamePackage(d.PackageName) && d.SigKey == sigKey && d.Abi == candidate.Abi)
            ?? (md5 is null ? null : rows.FirstOrDefault(d => SamePackage(d.PackageName) && FirstFingerprint(d.SigMd5) == md5 && d.Abi == candidate.Abi))
            ?? (candidate.Sha256 is null ? null : rows.FirstOrDefault(d => SamePackage(d.PackageName) && d.Abi == candidate.Abi && d.Sha256 is not null && EnrichmentQueries.HashMatches(d.Sha256, candidate.Sha256)));
        if (row is null)
        {
            row = new AppDownload { App = app, SigKey = sigKey, ApkUrl = candidate.ApkUrl, Abi = candidate.Abi };
            db.Downloads.Add(row);
        }
        else
        {
            row.SigKey = sigKey;
            var newer = row.VersionCode is null || candidate.VersionCode is null || candidate.VersionCode > row.VersionCode;
            var forgeWinsTie = candidate.VersionCode == row.VersionCode
                && IsForgeSource(candidate.Source) && !IsForgeSource(row.Source);
            if (!newer && !forgeWinsTie && candidate.Source != row.Source)
            {
                return;
            }

            // Same source and version: flavor twins (debug vs release, terminal
            // vs full, fdroid vs plain) share the identity row. Keep the row
            // holding the higher-ranked artifact and only remember the twin, so
            // later passes skip it without re-downloading or re-queueing AI.
            if (candidate.Source == row.Source
                && candidate.VersionCode is not null
                && candidate.VersionCode == row.VersionCode
                && !string.Equals(candidate.ApkUrl, row.ApkUrl, StringComparison.Ordinal)
                && ArtifactRank(candidate.ApkUrl) <= ArtifactRank(row.ApkUrl))
            {
                RememberArtifact(row, candidate);
                return;
            }
        }

        // A lower offered version means the app moved from a prerelease track
        // to an older stable one. History rows above the newly served code came
        // from that track, so flag them and keep the version anomaly check
        // focused on skipped releases and failed analyses.
        if (row.VersionCode is long incumbent && candidate.VersionCode is long incoming && incoming < incumbent)
        {
            await FlagSupersededPrereleaseVersionsAsync(app, incoming, ct);
        }

        // A different version invalidates the twin memory of the old release.
        if (candidate.VersionCode is not null && row.VersionCode is not null
            && candidate.VersionCode != row.VersionCode)
        {
            row.AnalyzedArtifacts.Clear();
        }

        row.Source = candidate.Source;
        row.SourceRef = candidate.SourceRef;
        // The release tag is forge metadata; a path that does not know it
        // (index-only updates, local analyses) must not clear a recorded one.
        row.ReleaseTag = candidate.ReleaseTag ?? row.ReleaseTag;
        if (candidate.PackageName is not null)
        {
            row.PackageName = candidate.PackageName;
        }

        // Keep the incumbent artifact in the memory so a heal (debug replaced
        // by release) does not make the old twin look unanalyzed next pass.
        RememberArtifact(row, row.Sha256, row.ApkUrl);
        row.ApkUrl = candidate.ApkUrl;
        row.ArchiveEntry = candidate.ArchiveEntry;
        row.VersionCode = candidate.VersionCode;
        row.VersionName = candidate.VersionName;
        row.SizeBytes = candidate.SizeBytes;
        // A new artifact means a new release; the first checksum ever recorded
        // for a row means this build was never viewed by the AI analyzer. Both
        // queue exactly one source analysis (see the queue call below).
        var apkChanged = candidate.Sha256 is not null
            && row.Sha256 is not null
            && !EnrichmentQueries.HashMatches(row.Sha256, candidate.Sha256);
        var firstAnalysis = candidate.Sha256 is not null && row.Sha256 is null;
        row.Sha256 = candidate.Sha256;
        // Index-only candidates carry no fingerprints; never clear what a
        // previous analysis established.
        row.SigSha256 = candidate.SigSha256 ?? row.SigSha256;
        row.SigMd5 = candidate.SigMd5 ?? row.SigMd5;
        // Once a build has been inspected, a later index-only update must not
        // make the row forget that its data came from a real analysis.
        row.Analyzed = row.Analyzed || candidate.Analyzed;
        row.MinSdk = candidate.MinSdk;
        // SDK levels and locales only come from a real analysis; index-only
        // updates must not clear what a previous analysis established.
        row.TargetSdk = candidate.TargetSdk ?? row.TargetSdk;
        row.CompileSdk = candidate.CompileSdk ?? row.CompileSdk;
        if (candidate.Locales is { Count: > 0 })
        {
            row.Locales = candidate.Locales.ToList();
        }

        if (candidate.Abis is { Count: > 0 })
        {
            row.Abis = candidate.Abis.ToList();
        }

        if (candidate.LocalizedLabels is { Count: > 0 })
        {
            row.LocalizedLabels = candidate.LocalizedLabels.ToList();
        }

        // Signer detail and the extraction generation only come from a real
        // analysis; index-only updates keep the last recorded values.
        row.SignerDn = candidate.SignerDn ?? row.SignerDn;
        row.SignerScheme = candidate.SignerScheme ?? row.SignerScheme;
        row.SignerKeyAlgorithm = candidate.SignerKeyAlgorithm ?? row.SignerKeyAlgorithm;
        row.AnalysisVersion = Math.Max(row.AnalysisVersion, candidate.AnalysisVersion);

        // Signals come from a real inspection only; an index-only update
        // keeps whatever the last analysis recorded. The inspected flag marks
        // that a signal scan ran and is never downgraded.
        if (candidate.Inspection is not null)
        {
            row.Inspected = true;
            row.DhizukuDeclared = candidate.Inspection.Signals.DhizukuDeclared;
            row.ShizukuDeclared = row.ShizukuDeclared || candidate.Inspection.Signals.ShizukuDeclared;
            row.Trackers = candidate.Inspection.Trackers
                .Select(t => t.Name).Distinct(StringComparer.Ordinal).ToList();
            row.TrackerSignatures = candidate.Inspection.Trackers
                .Select(t => t.Signature).Distinct(StringComparer.Ordinal).ToList();
            row.TrackerTags = candidate.Inspection.Trackers
                .SelectMany(t => t.Tags.Select(tag => $"{t.Name}:{tag}"))
                .Distinct(StringComparer.Ordinal).ToList();
        }

        // An index-only update can carry the signal parsed from the F-Droid
        // index; an analyzed row keeps it true once any recorded build
        // declared Shizuku (the gate accepts the app on a single witness).
        row.ShizukuDeclared = row.ShizukuDeclared || candidate.ShizukuDeclared;

        row.Abi = candidate.Abi;
        row.ResolvedAt = now;
        RememberArtifact(row, candidate);

        if (usageQueue is not null && (apkChanged || (firstAnalysis && app.UsageAnalyzedAt is null)))
        {
            await usageQueue.EnqueueForAnalyzedArtifactAsync(
                app, apkChanged, firstAnalysis,
                candidate.ReleaseTag ?? ReleaseTagParser.FromArtifactUrl(candidate.ApkUrl), ct);
        }
    }

    /// <summary>
    /// Flags history rows above the newly served version as pre-releases. An
    /// intentional downgrade only happens when a stable release replaces the
    /// prerelease that was served before, so those rows are prerelease
    /// history, not the skipped release the anomaly check looks for.
    /// </summary>
    private async Task FlagSupersededPrereleaseVersionsAsync(App app, long ceiling, CancellationToken ct)
    {
        foreach (var version in app.Versions)
        {
            if (version.VersionCode > ceiling)
            {
                version.IsPrerelease = true;
            }
        }

        if (app.Id == 0)
        {
            return;
        }

        var superseded = await db.AppVersions
            .Where(v => v.AppId == app.Id && v.VersionCode > ceiling && !v.IsPrerelease)
            .ToListAsync(ct);
        foreach (var version in superseded)
        {
            version.IsPrerelease = true;
        }
    }

    /// <summary>
    /// Default candidate for fresh installs: forge sources beat F-Droid/Izzy,
    /// then the newest version, then a fixed source order; when the forge no
    /// longer ships binaries (see <see cref="App.ForgeAssetsStale"/>) the
    /// alternatives compete on version code alone. Exactly one row is primary
    /// per app (invariant enforced here, not by a DB constraint).
    /// </summary>
    internal async Task RecomputePrimaryAsync(
        App app, CancellationToken ct, string? preferredPackage = null, string? preferredReleaseTag = null)
    {
        var rows = await LoadDownloadsAsync(app, ct);
        // Flavor packages share one row; the default offer must stay the
        // canonical package (the list URL's package), so a flavor can never
        // become the fresh install. Unknown packages keep the old ordering.
        var candidates = preferredPackage is null
            ? rows
            : rows.Where(r => r.PackageName is null || string.Equals(r.PackageName, preferredPackage, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            candidates = rows;
        }

        AppDownload? best = null;
        foreach (var row in candidates)
        {
            best = best is null ? row : PreferDownload(row, best, preferredReleaseTag, preferForge: !app.ForgeAssetsStale);
        }

        // The partial unique index on (app_id) where is_primary is not
        // deferrable, and a single SaveChanges may send the promotion before
        // the demotion. Persist the demotion first (raw, so pending inserts
        // stay untouched), then let the caller's save promote the winner.
        if (rows.Any(r => r.IsPrimary))
        {
            await db.Downloads
                .Where(d => d.AppId == app.Id && d.IsPrimary)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsPrimary, false), ct);

            // The raw update bypasses the tracker, so the tracked rows still
            // carry their old flag as the original value; re-promoting the
            // same row would then look like a no-op and never be saved.
            foreach (var row in rows)
            {
                var entry = db.Entry(row);
                if (entry.State != EntityState.Detached)
                {
                    entry.Property(x => x.IsPrimary).OriginalValue = false;
                }
            }
        }

        foreach (var row in rows)
        {
            row.IsPrimary = ReferenceEquals(row, best);
        }
    }

    /// <summary>
    /// Rebuilds the primary flag for an app whose stored rows lost it (a
    /// pre-fix recompute bug). Cheap: one existence check, and a recompute
    /// over rows already in the database, never a download.
    /// </summary>
    internal async Task HealMissingPrimaryAsync(App app, CancellationToken ct)
    {
        if (app.Id == 0)
        {
            return;
        }

        if (await db.Downloads.AnyAsync(d => d.AppId == app.Id && d.IsPrimary, ct))
        {
            return;
        }

        var rows = await LoadDownloadsAsync(app, ct);
        if (rows.Count == 0)
        {
            return;
        }

        await RecomputePrimaryAsync(app, ct, app.PackageName);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Rows analyzed before the category tags were stored have tracker names
    /// but no tags. The tags are derivable from the stored names via the
    /// cached Exodus catalog, so backfill them without re-downloading; rows
    /// whose trackers all lack categories retry harmlessly through the cached
    /// catalog. Cheap: two stored-row checks plus an in-memory lookup.
    /// </summary>
    internal async Task HealTrackerTagsAsync(App app, CancellationToken ct)
    {
        if (trackers is null)
        {
            return;
        }

        var primary = await PrimaryDownloadAsync(app, ct);
        if (primary is null || primary.Trackers.Count == 0 || primary.TrackerTags.Count > 0)
        {
            return;
        }

        var catalog = await trackers.GetAsync(ct);
        if (catalog.Count == 0)
        {
            return;
        }

        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var tracker in catalog)
        {
            if (!byName.TryGetValue(tracker.Name, out var categories))
            {
                byName[tracker.Name] = categories = [];
            }

            categories.AddRange(tracker.Categories);
        }

        var tags = primary.Trackers
            .SelectMany(name => byName.TryGetValue(name, out var categories)
                ? categories.Select(tag => $"{name}:{tag}")
                : [])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (tags.Count == 0)
        {
            return;
        }

        primary.TrackerTags = tags;
        await db.SaveChangesAsync(ct);
    }

    private static AppDownload PreferDownload(
        AppDownload a, AppDownload b, string? preferredReleaseTag = null, bool preferForge = true)
    {
        // When the forge stopped shipping binaries, its rows compete on
        // version code only: the alternative source's newer build leads.
        if (preferForge)
        {
            var aForge = IsForgeSource(a.Source);
            var bForge = IsForgeSource(b.Source);
            if (aForge != bForge)
            {
                return aForge ? a : b;
            }

            // The release just scanned owns its tag's rows: when a repo switches
            // from prereleases to stable, the freshly recorded stable build must
            // outrank older prerelease rows kept for ABI or signature history.
            if (preferredReleaseTag is not null)
            {
                var aTag = string.Equals(a.ReleaseTag, preferredReleaseTag, StringComparison.Ordinal);
                var bTag = string.Equals(b.ReleaseTag, preferredReleaseTag, StringComparison.Ordinal);
                if (aTag != bTag)
                {
                    return aTag ? a : b;
                }
            }
        }

        // A release-named artifact beats debug/test/beta/terminal builds even
        // when the latter carry a higher version code: a debug APK must never
        // become the fresh-install default.
        var aRank = ArtifactRank(a);
        var bRank = ArtifactRank(b);
        if (aRank != bRank)
        {
            return aRank > bRank ? a : b;
        }

        var av = a.VersionCode ?? -1;
        var bv = b.VersionCode ?? -1;
        if (av != bv)
        {
            return av > bv ? a : b;
        }

        var aAbi = AbiOrder(a.Abi);
        var bAbi = AbiOrder(b.Abi);
        if (aAbi != bAbi)
        {
            return aAbi < bAbi ? a : b;
        }

        // An analyzed row carries the real SHA-256 identity; prefer it over a
        // twin that only has index metadata when everything else ties.
        if ((a.SigSha256 is null) != (b.SigSha256 is null))
        {
            return a.SigSha256 is not null ? a : b;
        }

        return SourceOrder(a.Source) <= SourceOrder(b.Source) ? a : b;
    }

    // Universal builds run anywhere, so they lead the fresh-install default;
    // arm64-v8a covers most devices when the release ships only splits.
    private static int AbiOrder(string? abi) => abi?.ToLowerInvariant() switch
    {
        null => 0,
        "arm64-v8a" => 1,
        "armeabi-v7a" => 2,
        "x86_64" => 3,
        "x86" => 4,
        _ => 5,
    };

    internal async Task RemoveDownloadAsync(App app, SourceKind source, CancellationToken ct)
    {
        var rows = await LoadDownloadsAsync(app, ct);
        foreach (var row in rows.Where(r => r.Source == source))
        {
            db.Downloads.Remove(row);
        }
    }

    /// <summary>
    /// F-Droid index siblings of the primary package: same version name, a
    /// distinct non-null ABI and not a newer version code, so an index-only
    /// row can never outrank the analyzed primary.
    /// </summary>
    internal static List<FdroidPackageInfo> FdroidSiblings(
        IReadOnlyList<FdroidPackageInfo> packages, FdroidPackageInfo primary)
    {
        var siblings = new List<FdroidPackageInfo>();
        if (primary.VersionName is null)
        {
            return siblings;
        }

        foreach (var candidate in packages)
        {
            if (ReferenceEquals(candidate, primary)
                || candidate.Abi is null
                || string.Equals(candidate.Abi, primary.Abi, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate.VersionName, primary.VersionName, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate.VersionCode is { } code
                && primary.VersionCode is { } primaryCode
                && code > primaryCode)
            {
                continue;
            }

            siblings.Add(candidate);
        }

        return siblings;
    }

    /// <summary>
    /// Records every per-architecture sibling of an F-Droid primary as an
    /// index-only download row and drops arch rows that vanished from the
    /// index for that package.
    /// </summary>
    internal async Task UpsertFdroidSiblingsAsync(
        App app, SourceKind kind, string repoBase, string packageId,
        IReadOnlyList<FdroidPackageInfo> packages, FdroidPackageInfo primary,
        DateTimeOffset now, CancellationToken ct)
    {
        var siblings = FdroidSiblings(packages, primary);
        foreach (var sibling in siblings)
        {
            await UpsertDownloadAsync(app, new DownloadCandidate(
                kind,
                packageId,
                $"{repoBase.TrimEnd('/')}/{sibling.ApkName}",
                null,
                sibling.VersionCode,
                sibling.VersionName,
                sibling.Size,
                sibling.Sha256,
                sibling.SigSha256,
                null,
                sibling.MinSdk,
                sibling.Abi,
                PackageName: packageId,
                ShizukuDeclared: ShizukuPermission.IsDeclared(sibling.Permissions ?? [])), now, ct);
        }

        var keep = siblings.Select(s => s.ApkName).Append(primary.ApkName).ToHashSet(StringComparer.Ordinal);
        foreach (var row in await LoadDownloadsAsync(app, ct))
        {
            if (row.Source == kind
                && row.SourceRef == packageId
                && row.Abi is not null
                && row.ApkUrl.LastIndexOf('/') is var slash
                && slash >= 0
                && !keep.Contains(row.ApkUrl[(slash + 1)..]))
            {
                db.Downloads.Remove(row);
            }
        }
    }

    /// <summary>
    /// Best-effort IzzyOnDroid rolling download count for an Izzy-primary app
    /// (the only F-Droid-compatible repo that publishes counts). Never fails
    /// an enrich: stats trouble leaves the previous value untouched.
    /// </summary>
    internal async Task ApplyIzzyDownloadsAsync(App app, string packageName, CancellationToken ct)
    {
        if (izzyStats is null)
        {
            return;
        }

        try
        {
            if (await izzyStats.GetDownloadsAsync(packageName, ct) is { } downloads)
            {
                app.DownloadTotal = downloads;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Popularity is best-effort; the enrich outcome must not depend on it.
        }
    }

    /// <summary>
    /// Stamps the served build's publish time from the repo index. Forge
    /// releases carry the date themselves, but F-Droid-compatible repos
    /// publish it only as the version entry's <c>added</c>; without this
    /// those apps never sort under "recently updated". Newer wins, so a
    /// forge date already ahead of the index build is kept.
    /// </summary>
    internal static void StampFdroidDate(App app, FdroidPackageInfo package, DateTimeOffset now)
    {
        if (package.Added is not { } added
            || (app.VersionUpdatedAt is not null && app.VersionUpdatedAt >= added))
        {
            return;
        }

        app.VersionUpdatedAt = added;
        // The changes feed keys "updated" on updated_at, so a backfilled date
        // must move it or incremental clients never refetch the detail.
        app.UpdatedAt = now;
    }

    internal async Task AddVersionRowAsync(
        App app, long? versionCode, string? versionName, string apkUrl, bool isPrerelease,
        DateTimeOffset now, CancellationToken ct)
    {
        // Identity is (app, code), mirroring IX_app_versions_app_id_version_code:
        // upstreams re-tag the same build (vFlow's v1.5.3-pr1 reuses 1.5.2's
        // code), and matching on the name too inserted a duplicate row whose
        // unique violation rolled back the whole enrich (stars, permissions).
        // Pending rows are invisible to AnyAsync, so a multi-ABI release (same
        // versionCode per ABI, same signing key) would insert the row twice.
        var exists = app.Versions.Any(v => v.VersionCode == versionCode)
            || db.AppVersions.Local.Any(v =>
                (ReferenceEquals(v.App, app) || (app.Id != 0 && v.AppId == app.Id))
                && v.VersionCode == versionCode)
            || await db.AppVersions.AnyAsync(v =>
                v.AppId == app.Id && v.VersionCode == versionCode, ct);
        if (exists)
        {
            return;
        }

        app.Versions.Add(new AppVersion
        {
            VersionCode = versionCode,
            VersionName = versionName,
            ApkUrl = apkUrl,
            IsPrerelease = isPrerelease,
            DetectedAt = now,
        });
    }
}
