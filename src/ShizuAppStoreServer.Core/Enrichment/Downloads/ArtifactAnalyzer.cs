using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment.Apk;
using ShizuAppStoreServer.Core.Enrichment.Icons;
using ShizuAppStoreServer.Core.Jobs;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment.Downloads;

internal sealed class ArtifactAnalyzer(
    IAapt2Runner aapt2,
    IApkSignerRunner signer,
    HttpClient downloads,
    EnrichmentOptions options,
    ITrackerCatalog? trackers,
    IconPipeline icons,
    ILogger? log)
{
    internal async Task<ArtifactResult> DownloadAndAnalyzeApkAsync(
        string url, string? etag, SourceKind lockSource, DateTimeOffset? releasedAt,
        string? expectedSha256, CancellationToken ct)
    {
        // Metadata-only operator pass: report Unchanged so the row is stamped
        // and the release metadata still lands, but spend no bandwidth or CPU
        // on the APK (see EnrichmentOptions.SkipApkAnalysis).
        if (options.SkipApkAnalysis)
        {
            return new ArtifactResult(null, true, null);
        }

        var tempApk = Path.Combine(Path.GetTempPath(), $"shizu-{Guid.NewGuid():N}.apk");
        try
        {
            await DownloadAsync(url, tempApk, ct);
            return await AnalyzeArtifactAsync(url, null, tempApk, tempApk, etag, lockSource, releasedAt, expectedSha256, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ArtifactResult(null, false, $"Download: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tempApk); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Some releases attach only a zip with the APK inside (e.g.
    /// AppControl-X v3.0.0). Downloads the archive, picks the best
    /// <c>.apk</c> entry and analyzes it like a direct download; the
    /// recorded URL/size/hash stay the archive's and
    /// <see cref="App.ApkArchiveEntry"/> names the APK inside.
    /// </summary>
    internal async Task<ArtifactResult> DownloadAndAnalyzeZipAsync(
        string zipUrl, string? etag, SourceKind lockSource, DateTimeOffset? releasedAt,
        string? expectedSha256, CancellationToken ct)
    {
        // See DownloadAndAnalyzeApkAsync: same metadata-only skip.
        if (options.SkipApkAnalysis)
        {
            return new ArtifactResult(null, true, null);
        }

        var tempZip = Path.Combine(Path.GetTempPath(), $"shizu-{Guid.NewGuid():N}.zip");
        var tempApk = Path.Combine(Path.GetTempPath(), $"shizu-{Guid.NewGuid():N}.apk");
        try
        {
            await DownloadAsync(zipUrl, tempZip, ct);
            using var zip = ZipFile.OpenRead(tempZip);
            var entry = ApkAssetSelector.PickApk(zip.Entries, e => e.FullName, e => e.Length);
            if (entry is null)
            {
                return new ArtifactResult(null, false, null);
            }

            entry.ExtractToFile(tempApk, overwrite: true);
            return await AnalyzeArtifactAsync(zipUrl, entry.FullName, tempZip, tempApk, etag, lockSource, releasedAt, expectedSha256, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: a broken or APK-less candidate zip must not stop
            // the F-Droid fallback (the caller reports no .apk asset if all fail).
            return new ArtifactResult(null, false, ex.Message);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* best effort */ }
            try { File.Delete(tempApk); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Shared analysis: artifact hash/size first (returning <c>Unchanged</c>
    /// when it matches <paramref name="expectedSha256"/>), then badging and
    /// (best-effort) signer certs and launcher icon. <paramref name="apkPath"/>
    /// is the APK that gets analyzed, <paramref name="artifactPath"/> the file
    /// clients download (same file unless the APK came out of an archive). Does
    /// not mutate the app row; the caller routes the analysis to the row that
    /// owns its package.
    /// </summary>
    internal async Task<ArtifactResult> AnalyzeArtifactAsync(
        string artifactUrl, string? archiveEntry, string artifactPath, string apkPath,
        string? etag, SourceKind lockSource, DateTimeOffset? releasedAt,
        string? expectedSha256, CancellationToken ct)
    {
        // Hash first: when the file is byte-identical to what is already
        // recorded, every derived field (badging, signers, icon) is unchanged
        // too, so the expensive tools can be skipped entirely.
        var started = Stopwatch.GetTimestamp();
        var label = Path.GetFileName(apkPath);
        string artifactSha256;
        long artifactSize;
        try
        {
            using var sha = SHA256.Create();
            await using (var stream = File.OpenRead(artifactPath))
            {
                artifactSize = stream.Length;
                artifactSha256 = Convert.ToHexStringLower(await sha.ComputeHashAsync(stream, ct));
            }
        }
        catch (IOException ex)
        {
            return new ArtifactResult(null, false, $"APK unreadable: {ex.Message}");
        }

        if (expectedSha256 is not null && EnrichmentQueries.HashMatches(artifactSha256, expectedSha256))
        {
            JobContext.Current?.Analyze($"analyze {label} unchanged, hashed {artifactSize}B in {EnrichmentQueries.Elapsed(started)}ms");
            return new ArtifactResult(null, true, null);
        }

        var badgingStarted = Stopwatch.GetTimestamp();
        BadgingInfo badging;
        try
        {
            badging = BadgingParser.Parse(await aapt2.DumpBadgingAsync(apkPath, ct));
        }
        catch (Exception ex) when (ex is Aapt2Exception or BadgingParseException)
        {
            return new ArtifactResult(null, false, $"aapt2: {ex.Message}");
        }

        var signerStarted = Stopwatch.GetTimestamp();
        var signers = await TryExtractSignersAsync(apkPath, ct);
        var sigSha256 = CertFingerprint.Join(signers.Signers.Select(s => s.Sha256));
        var sigMd5 = CertFingerprint.Join(signers.Signers.Select(s => s.Md5));
        var signalsStarted = Stopwatch.GetTimestamp();
        var inspection = await InspectApkAsync(apkPath, badging, ct);
        var iconStarted = Stopwatch.GetTimestamp();
        // An icons:false admin pass renders nothing: skip the resolve so no
        // raster decode or Gradle XML render runs and no icon gets adopted.
        var icon = options.SkipIconRenders ? null : await icons.ResolveIconAsync(badging, apkPath, ct);
        JobContext.Current?.Analyze($"analyze {label} badging {EnrichmentQueries.Elapsed(badgingStarted)}ms, "
            + $"signers {EnrichmentQueries.Elapsed(signerStarted)}ms, signals {EnrichmentQueries.Elapsed(signalsStarted)}ms, "
            + $"icon {EnrichmentQueries.Elapsed(iconStarted)}ms, total {EnrichmentQueries.Elapsed(started)}ms");

        return new ArtifactResult(new ArtifactAnalysis(
            artifactUrl, archiveEntry, lockSource, etag, badging, artifactSha256, artifactSize,
            sigSha256, sigMd5, releasedAt, icon, inspection, signers), false, null);
    }

    /// <summary>
    /// Static APK inspection: declared Shizuku/Dhizuku permissions plus
    /// Exodus tracker matches in the DEX code. Both are best-effort garnish;
    /// a failed scan must not fail enrichment, and the DEX read only happens
    /// when a tracker catalog is injected, so test runs skip it.
    /// </summary>
    internal async Task<ApkInspection> InspectApkAsync(string apkPath, BadgingInfo badging, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var signals = ShizukuSignalScanner.Scan(badging.Permissions);
        var dexText = TrackerScanner.ReadDexText(apkPath);

        IReadOnlyList<TrackerHit> hits = [];
        if (trackers is not null)
        {
            try
            {
                var catalog = await trackers.GetAsync(ct);
                hits = TrackerScanner.ScanText(dexText, catalog);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.LogDebug(ex, "Tracker scan failed for {ApkPath}.", apkPath);
            }
        }

        JobContext.Current?.Analyze($"inspect {Path.GetFileName(apkPath)} dhizuku={signals.DhizukuDeclared} "
            + $"shizuku={signals.ShizukuDeclared} trackers={hits.Count} in {EnrichmentQueries.Elapsed(started)}ms");
        return new ApkInspection(signals, hits);
    }

    internal async Task DownloadAsync(string url, string tempApk, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        JobContext.Current?.Download($"download start {url}", new { url });
        using var response = await downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            JobContext.Current?.Download(
                $"download failed HTTP {(int)response.StatusCode} after {EnrichmentQueries.Elapsed(started)}ms",
                new { url, status = (int)response.StatusCode }, JobEventLevel.Warning);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} for {url}.");
        }

        await using var file = File.Create(tempApk);
        await response.Content.CopyToAsync(file, ct);
        JobContext.Current?.Download(
            $"download done {file.Length}B in {EnrichmentQueries.Elapsed(started)}ms", new { url, bytes = file.Length });
    }

    /// <summary>
    /// Download → aapt2 → file hash, with best-effort signer certs. Returns
    /// null when the file can't be fetched or parsed (callers fall back to
    /// index metadata); never throws except on cancellation.
    /// </summary>
    internal async Task<AnalyzedApk?> TryAnalyzeDownloadAsync(string apkUrl, CancellationToken ct)
    {
        // Metadata-only operator pass: callers fall back to index metadata.
        if (options.SkipApkAnalysis)
        {
            return null;
        }

        var tempApk = Path.Combine(Path.GetTempPath(), $"shizu-{Guid.NewGuid():N}.apk");
        AnalyzedApk? result = null;
        try
        {
            await DownloadAsync(apkUrl, tempApk, ct);

            BadgingInfo badging;
            try
            {
                badging = BadgingParser.Parse(await aapt2.DumpBadgingAsync(tempApk, ct));
            }
            catch (Exception ex) when (ex is Aapt2Exception or BadgingParseException)
            {
                return null;
            }

            string fileSha256;
            long fileSize;
            try
            {
                using var sha = SHA256.Create();
                await using (var stream = File.OpenRead(tempApk))
                {
                    fileSize = stream.Length;
                    fileSha256 = Convert.ToHexStringLower(await sha.ComputeHashAsync(stream, ct));
                }
            }
            catch (IOException)
            {
                return null;
            }

            result = new AnalyzedApk(
                tempApk,
                badging,
                fileSha256,
                fileSize,
                await TryExtractSignersAsync(tempApk, ct),
                await InspectApkAsync(tempApk, badging, ct));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            // The success path owns the file via AnalyzedApk disposal.
            if (result is null)
            {
                try { File.Delete(tempApk); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Best-effort signer facts (none when apksigner is missing, Java is
    /// absent, or the APK is unsigned). Never fails enrichment.
    /// </summary>
    internal async Task<ApkSignerInfo> TryExtractSignersAsync(string apkPath, CancellationToken ct)
    {
        try
        {
            return ApkSignerParser.Parse(await signer.PrintCertsAsync(apkPath, ct));
        }
        catch (Exception ex) when (ex is ApkSignerException or ApkSignerParseException)
        {
            return ApkSignerInfo.None;
        }
    }
}
