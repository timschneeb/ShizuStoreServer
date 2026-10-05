using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment;

internal sealed class FdroidIndexEnricher(
    IEnrichmentPipeline pipeline,
    FdroidIndexProvider fdroid,
    ILauncherIconService launcherIcons,
    IconPipeline icons,
    ArtifactAnalyzer artifacts,
    DownloadStore downloadStore,
    IGitHubReleaseClient github,
    IGitLabReleaseClient gitlab,
    ILogger<AppEnricher>? log = null)
{
    /// <summary>
    /// Rescue path for apps whose forge published no APK: package id from an
    /// F-Droid/Izzy URL, else an F-Droid index lookup by the repo's forge
    /// URL. A hit becomes the app's primary download (the only candidate),
    /// recorded in the signature-keyed downloads list like any other source.
    /// Returns null when the app is not published on F-Droid (the forge
    /// failure then stands). Best-effort: index trouble never replaces the
    /// forge error.
    /// </summary>
    internal async Task<EnrichResult?> TryFdroidFallbackAsync(App app, DateTimeOffset now, CancellationToken ct)
    {
        if (pipeline.TryParseFdroid(app.Url, app.SourceUrl, out var repoBase, out var packageId, out _))
        {
            return await EnrichFromFdroidAsync(app, repoBase, packageId, now, ct);
        }

        var repoKey = SourceClassifier.RepoKey(app.Url) ?? SourceClassifier.RepoKey(app.SourceUrl);
        if (repoKey is null)
        {
            return null;
        }

        FdroidPackageInfo? package;
        try
        {
            package = await fdroid.FindPackageBySourceAsync(FdroidRepos.FDroidBase, repoKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return package is null
            ? null
            : await EnrichFromFdroidAsync(app, FdroidRepos.FDroidBase, package.PackageName, now, ct);
    }

    internal async Task<EnrichResult> EnrichFromFdroidAsync(
        App app, string repoBase, string packageId, DateTimeOffset now, CancellationToken ct)
    {
        (IReadOnlyList<FdroidPackageInfo> Packages, string? IndexEtag)? fetched;
        try
        {
            fetched = await fdroid.GetPackagesAsync(repoBase, packageId, pipeline.ChangelogEtag(app), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
        {
            return pipeline.Fail(app, now, $"F-Droid: {ex.Message}");
        }

        if (fetched is null)
        {
            // A 304 would keep pre-fix rows permission-less forever (see
            // NeedsPermissionHeal), and legacy index-only rows would stay
            // without a SHA-256 identity; refetch the index once and
            // re-analyze below. The provider memoizes the forced fetch, so a
            // rerun cannot loop on it. A failing refetch is not an upstream
            // change, so stay up-to-date instead of failing the pass.
            var primary = await downloadStore.PrimaryDownloadAsync(app, ct);
            if (DownloadStore.NeedsRefetch(app, primary)
                || DownloadStore.NeedsIndexIdentityBackfill(primary))
            {
                try
                {
                    fetched = await fdroid.GetPackagesAsync(repoBase, packageId, null, ct, force: true);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
                {
                    fetched = null;
                }
            }

            if (fetched is null)
            {
                app.LastCheckedAt = now;
                return new EnrichResult(EnrichOutcome.UpToDate, null) { Detail = "index not modified" };
            }
        }

        var (packages, indexEtag) = fetched.Value;
        var package = packages.FirstOrDefault();
        if (package is null)
        {
            return pipeline.Fail(app, now, $"F-Droid: package {packageId} not in the {repoBase} index.");
        }

        // The index <desc> is the only changelog text these repos publish.
        app.Changelog = pipeline.NormalizeChangelog(package.LongDescription);

        var kind = repoBase == FdroidRepos.IzzyBase ? SourceKind.Izzy : SourceKind.FDroid;
        app.SourceKind = kind;

        // f-droid.org publishes no download counts; only the Izzy repo does.
        if (kind == SourceKind.Izzy)
        {
            await downloadStore.ApplyIzzyDownloadsAsync(app, package.PackageName, ct);
        }

        var apkUrl = $"{repoBase.TrimEnd('/')}/{package.ApkName}";
        var siblings = DownloadStore.FdroidSiblings(packages, package);
        var recorded = await downloadStore.LoadDownloadsAsync(app, ct);
        var siblingsRecorded = siblings.All(s => recorded.Any(d =>
            d.Source == kind
            && d.SourceRef == packageId
            && d.Abi == s.Abi
            && d.ApkUrl.EndsWith('/' + s.ApkName, StringComparison.Ordinal)));
        var current = await downloadStore.PrimaryDownloadAsync(app, ct);
        if (current is not null
            && current.Source == kind
            && current.ApkUrl == apkUrl
            && current.VersionCode == package.VersionCode
            && current.Sha256 is not null
            && (package.Sha256 is null || EnrichmentQueries.HashMatches(current.Sha256, package.Sha256))
            && app.PackageName is not null
            && app.IconHash is not null && icons.IconFileExists(app.IconHash)
            && !DownloadStore.NeedsRefetch(app, current)
            && !DownloadStore.NeedsSignerBackfill(current, package)
            && siblingsRecorded)
        {
            // Heals the release date on rows enriched before the index date
            // was parsed; everything else about the row is already current.
            DownloadStore.StampFdroidDate(app, package, now);
            app.EnrichEtag = indexEtag;
            app.LastCheckedAt = now;
            return new EnrichResult(EnrichOutcome.UpToDate, null) { Detail = "index unchanged" };
        }

        // Changed version: analyze the actual APK (same quality bar as forge
        // builds). Anything wrong with the file falls back to the index-only
        // record, except a package-name mismatch, which means the index row
        // and the file disagree, so the old values are kept.
        var oldIcon = app.IconHash;
        using var analyzed = await artifacts.TryAnalyzeDownloadAsync(apkUrl, ct);
        if (analyzed is not null && analyzed.Badging.PackageName != package.PackageName)
        {
            return pipeline.Fail(app, now, $"F-Droid: {apkUrl} contains {analyzed.Badging.PackageName}, expected {package.PackageName}.");
        }

        var icon = analyzed is not null
            && await launcherIcons.ResolveAsync(analyzed.ApkPath, analyzed.Badging, ct) is { } apkIcon
            ? apkIcon
            : await icons.MirrorIconAsync(package.IconFile, repoBase, app.Name, ct);
        await icons.WriteIconFileAsync(icon, ct);

        var versionCode = analyzed?.Badging.VersionCode ?? package.VersionCode;
        var versionName = analyzed?.Badging.VersionName ?? package.VersionName;
        var minSdk = analyzed?.Badging.MinSdk ?? package.MinSdk;
        // Index signers come from index-v2 manifest.signer.sha256; an
        // analyzed APK wins because it reflects the actual file.
        var sigSha256 = analyzed is null
            ? package.SigSha256
            : CertFingerprint.Join(analyzed.Signers.Signers.Select(s => s.Sha256));
        var sigMd5 = analyzed is null
            ? null
            : CertFingerprint.Join(analyzed.Signers.Signers.Select(s => s.Md5));

        await downloadStore.UpsertDownloadAsync(app, new DownloadCandidate(
            kind,
            packageId,
            apkUrl,
            null,
            versionCode,
            versionName,
            analyzed?.FileSize ?? package.Size,
            analyzed?.FileSha256 ?? package.Sha256,
            sigSha256,
            sigMd5,
            minSdk,
            analyzed?.Badging.Abi ?? package.Abi,
            TargetSdk: analyzed?.Badging.TargetSdk,
            CompileSdk: analyzed?.Badging.CompileSdk,
            Locales: analyzed?.Badging.Locales,
            Abis: analyzed?.Badging.Abis,
            LocalizedLabels: analyzed is null ? null : DownloadStore.LocalizedLabelEntries(analyzed.Badging.LocalizedLabels),
            SignerDn: analyzed?.Signers.Dn,
            SignerScheme: analyzed?.Signers.Scheme,
            SignerKeyAlgorithm: analyzed?.Signers.KeyAlgorithm,
            AnalysisVersion: analyzed is null ? 0 : pipeline.CurrentAnalysisVersion,
            Inspection: analyzed?.Inspection,
            PackageName: package.PackageName, Analyzed: analyzed is not null,
            ShizukuDeclared: ShizukuPermission.IsDeclared(package.Permissions ?? [])), now, ct);
        await downloadStore.RecomputePrimaryAsync(app, ct, package.PackageName);

        app.PackageName = package.PackageName;
        if (analyzed is not null)
        {
            // The analyzed APK's permission list belongs to the served
            // build regardless of source; index-only rows keep prior values.
            app.Permissions = analyzed.Badging.Permissions.ToList();
        }

        app.IconHash = icon.Sha256;
        app.IconAdaptive = icon.Adaptive;
        app.Availability = Availability.DirectApk;
        app.EnrichEtag = indexEtag;
        app.ExcludedReason = null;
        app.LastCheckedAt = now;
        app.LastError = null;

        // The index lists one package per architecture; record the siblings as
        // index-only rows so the client can pick the device's ABI.
        await downloadStore.UpsertFdroidSiblingsAsync(app, kind, repoBase, packageId, packages, package, now, ct);
        await downloadStore.RecomputePrimaryAsync(app, ct, package.PackageName);

        await downloadStore.AddVersionRowAsync(app, versionCode, versionName, apkUrl, false, now, ct);

        DownloadStore.StampFdroidDate(app, package, now);

        await icons.DeleteIconIfOrphanedAsync(app, oldIcon, ct);
        await ResolveForgeCandidateFromSourceAsync(app, package.SourceUrl, now, ct);
        if (kind == SourceKind.Izzy)
        {
            // Izzy builds come from the developers, F-Droid rebuilds are a
            // distinct source; record the f-droid.org candidate as well.
            await ResolveFdroidCandidateAsync(app, now, ct);
        }

        return new EnrichResult(EnrichOutcome.Enriched, null);
    }

    /// <summary>
    /// F-Droid candidate for forge-primary apps: when the same package is
    /// published on F-Droid, record its build next to the primary forge
    /// build. Only runs after a fresh primary enrich (never on
    /// <c>SkippedFresh</c>); candidate problems are best-effort and never
    /// change the primary outcome.
    /// </summary>
    internal async Task ResolveFdroidCandidateAsync(App app, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.PackageName))
        {
            return;
        }

        FdroidPackageInfo? package;
        IReadOnlyList<FdroidPackageInfo> packages = [];
        try
        {
            var fetched = await fdroid.GetPackagesAsync(FdroidRepos.FDroidBase, app.PackageName, seedEtag: null, ct);
            if (fetched is null)
            {
                return; // 304 with nothing cached: no new information.
            }

            packages = fetched.Value.Packages;
            package = packages.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return; // Best-effort candidate: index trouble never fails the primary.
        }

        if (package is null)
        {
            await downloadStore.RemoveDownloadAsync(app, SourceKind.FDroid, ct);
            await RecomputePrimaryAfterCandidateAsync(app, ct);
            return;
        }

        var apkUrl = $"{FdroidRepos.FDroidBase.TrimEnd('/')}/{package.ApkName}";
        await downloadStore.UpsertFdroidSiblingsAsync(
            app, SourceKind.FDroid, FdroidRepos.FDroidBase, package.PackageName, packages, package, now, ct);
        var existing = (await downloadStore.LoadDownloadsAsync(app, ct))
            .FirstOrDefault(d => d.Source == SourceKind.FDroid
                && d.ApkUrl == apkUrl
                && d.VersionCode == package.VersionCode
                && d.Sha256 is not null);
        if (existing is not null)
        {
            // Backfill the SHA-256 identity from the index when the first
            // discovery predates the signer map.
            if (existing.SigSha256 is null && package.SigSha256 is not null)
            {
                existing.SigSha256 = package.SigSha256;
                existing.ResolvedAt = now;
            }

            // The index knows the declared permissions even for builds the
            // analyzer never downloaded; use it as a second witness.
            if (!existing.ShizukuDeclared && ShizukuPermission.IsDeclared(package.Permissions ?? []))
            {
                existing.ShizukuDeclared = true;
                existing.ResolvedAt = now;
            }

            await RecomputePrimaryAfterCandidateAsync(app, ct);
            return;
        }

        using var analyzed = await artifacts.TryAnalyzeDownloadAsync(apkUrl, ct);
        var candidate = analyzed is not null && analyzed.Badging.PackageName == package.PackageName
            ? new DownloadCandidate(
                SourceKind.FDroid,
                package.PackageName,
                apkUrl,
                null,
                analyzed.Badging.VersionCode,
                analyzed.Badging.VersionName,
                analyzed.FileSize,
                analyzed.FileSha256,
                CertFingerprint.Join(analyzed.Signers.Signers.Select(s => s.Sha256)),
                CertFingerprint.Join(analyzed.Signers.Signers.Select(s => s.Md5)),
                analyzed.Badging.MinSdk,
                analyzed.Badging.Abi,
                TargetSdk: analyzed.Badging.TargetSdk,
                CompileSdk: analyzed.Badging.CompileSdk,
                Locales: analyzed.Badging.Locales,
                Abis: analyzed.Badging.Abis,
                LocalizedLabels: DownloadStore.LocalizedLabelEntries(analyzed.Badging.LocalizedLabels),
                SignerDn: analyzed.Signers.Dn,
                SignerScheme: analyzed.Signers.Scheme,
                SignerKeyAlgorithm: analyzed.Signers.KeyAlgorithm,
                AnalysisVersion: pipeline.CurrentAnalysisVersion,
                Inspection: analyzed.Inspection,
                PackageName: package.PackageName, Analyzed: true,
                ShizukuDeclared: ShizukuPermission.IsDeclared(package.Permissions ?? []))
            : new DownloadCandidate(
                SourceKind.FDroid,
                package.PackageName,
                apkUrl,
                null,
                package.VersionCode,
                package.VersionName,
                package.Size,
                package.Sha256,
                package.SigSha256,
                null,
                package.MinSdk,
                package.Abi,
                PackageName: package.PackageName,
                ShizukuDeclared: ShizukuPermission.IsDeclared(package.Permissions ?? []));
        await downloadStore.UpsertDownloadAsync(app, candidate, now, ct);
        await RecomputePrimaryAfterCandidateAsync(app, ct);
    }

    /// <summary>
    /// Recomputes the primary after an F-Droid candidate changed the row set.
    /// The forge pass just settled the primary with release-tag precedence, so
    /// the candidate must not undo it: without the tag a higher-coded
    /// prerelease row would outrank the freshly served stable build.
    /// </summary>
    private async Task RecomputePrimaryAfterCandidateAsync(App app, CancellationToken ct)
    {
        var primary = await downloadStore.PrimaryDownloadAsync(app, ct);
        await downloadStore.RecomputePrimaryAsync(app, ct, app.PackageName, primary?.ReleaseTag);
    }

    /// <summary>
    /// Exposes the forge build alongside an F-Droid-primary app when the index
    /// names a forge repo (<c>&lt;application&gt;&lt;source&gt;</c>). Candidate-only:
    /// presentation stays with the F-Droid build and every failure is swallowed.
    /// </summary>
    private async Task ResolveForgeCandidateFromSourceAsync(App app, string? sourceUrl, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return;
        }

        try
        {
            if (SourceClassifier.TryParseGitHubRepo(sourceUrl, out var owner, out var repo))
            {
                var target = new SourceTarget(SourceKind.GitHub, $"{owner}/{repo}");
                var release = await github.GetLatestReleaseAsync(target, null, ct);
                if (release is null)
                {
                    return;
                }

                var asset = ApkAssetSelector.PickApk(release.Assets);
                if (asset is not null)
                {
                    await pipeline.ResolveCandidateApkAsync(app, asset.Url, release.Etag, SourceKind.GitHub, now, ct);
                }
                else if (ApkAssetSelector.PickZip(release.Assets) is { } zip)
                {
                    await pipeline.ResolveCandidateZipAsync(app, zip.Url, release.Etag, SourceKind.GitHub, now, ct);
                }

                return;
            }

            if (SourceClassifier.TryParseGitLabRepo(sourceUrl, out var projectPath))
            {
                var release = await gitlab.GetLatestReleaseAsync(
                    new SourceTarget(SourceKind.GitLab, projectPath), null, ct);
                if (release is null)
                {
                    return;
                }

                var link = ApkAssetSelector.PickApk(release.Assets);
                if (link is not null)
                {
                    await pipeline.ResolveCandidateApkAsync(app, link.Url, release.Etag, SourceKind.GitLab, now, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Forge candidate resolution failed for {Slug}.", app.Slug);
        }
    }
}
