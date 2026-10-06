using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment.Apk;
using ShizuAppStoreServer.Core.Overrides;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment.Icons;

internal sealed class IconPipeline(
    ILauncherIconService launcherIcons,
    HttpClient downloads,
    EnrichmentOptions options,
    IPlayStoreClient? play,
    ILogger? log,
    ShizuDbContext db)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ProcessedIcon?>>> _iconByPackage =
        new(StringComparer.Ordinal);

    internal void BeginPass() => _iconByPackage.Clear();

    /// <summary>
    /// One launcher-icon resolve per package per app pass (see
    /// <c>_iconByPackage</c>). A failed resolve is evicted so the next
    /// artifact can try again instead of replaying the fault.
    /// </summary>
    internal async Task<ProcessedIcon?> ResolveIconAsync(
        BadgingInfo badging, string apkPath, CancellationToken ct)
    {
        var key = badging.PackageName is { Length: > 0 } packageName ? packageName : apkPath;
        var entry = _iconByPackage.GetOrAdd(
            key,
            _ => new Lazy<Task<ProcessedIcon?>>(() => launcherIcons.ResolveAsync(
                apkPath, badging, ct, allowXmlRender: !options.DeferXmlIconRenders)));
        try
        {
            return await entry.Value;
        }
        catch
        {
            _iconByPackage.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>
    /// Best-effort icon source for apps that stay external-only (no APK
    /// anywhere): scrapes the linked Play Store listing. Never touches
    /// apk_url; returns true only when an icon file was adopted.
    /// </summary>
    internal async Task<bool> TryPlayIconAsync(App app, DateTimeOffset now, CancellationToken ct)
    {
        if (play is null)
        {
            return false;
        }

        var packageId = string.Empty;
        if (!SourceClassifier.TryParsePlayPackage(app.Url, out packageId)
            && !SourceClassifier.TryParsePlayPackage(app.SourceUrl, out packageId)
            && !SourceClassifier.TryParsePlayPackage(app.StoreUrl, out packageId))
        {
            return false;
        }

        try
        {
            var iconUrl = await play.GetIconUrlAsync(packageId, ct);
            return iconUrl is not null && await TryAdoptPlayIconAsync(app, iconUrl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Play icon fetch failed for {Slug}.", app.Slug);
            return false;
        }
    }

    /// <summary>
    /// Downloads and adopts an already resolved Play icon URL; shared by the
    /// plain icon lookup and the richer details scrape so the page is fetched
    /// only once per enrich pass.
    /// </summary>
    internal async Task<bool> TryAdoptPlayIconAsync(App app, string iconUrl, CancellationToken ct)
    {
        try
        {
            using var response = await downloads.GetAsync(iconUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var icon = IconProcessor.ProcessRawImage(bytes);
            if (icon is null)
            {
                return false;
            }

            await WriteIconFileAsync(icon, ct);
            var oldIcon = app.IconHash;
            app.IconHash = icon.Sha256;
            app.IconAdaptive = false;
            await DeleteIconIfOrphanedAsync(app, oldIcon, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Play icon fetch failed for {Slug}.", app.Slug);
            return false;
        }
    }

    internal async Task<ProcessedIcon> MirrorIconAsync(
        string? iconFile, string repoBase, string appName, CancellationToken ct)
    {
        if (iconFile is not null)
        {
            foreach (var iconUrl in FdroidRepos.IconUrls(repoBase, iconFile))
            {
                try
                {
                    using var response = await downloads.GetAsync(iconUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        break;
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    if (IconProcessor.ProcessRawImage(bytes) is { } icon)
                    {
                        return icon;
                    }

                    break; // Got bytes but undecodable; the legacy size won't decode either.
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    break;
                }
            }
        }

        return LetterAvatarGenerator.Generate(appName);
    }

    internal async Task WriteIconFileAsync(ProcessedIcon icon, CancellationToken ct)
    {
        var path = Path.Combine(options.IconStorePath, $"{icon.Sha256}.png");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(options.IconStorePath);
            await File.WriteAllBytesAsync(path, icon.Png, ct);
        }
    }

    internal bool IconFileExists(string sha256) =>
        File.Exists(Path.Combine(options.IconStorePath, $"{sha256}.png"));

    internal async Task DeleteIconIfOrphanedAsync(App app, string? oldIcon, CancellationToken ct)
    {
        if (oldIcon is null || oldIcon == app.IconHash)
        {
            return;
        }

        // Local first: fast loop enriches a batch before saving, so
        // other apps may reference the hash only in the change tracker.
        var stillUsedLocal = db.Apps.Local.Any(a =>
            a.Id != app.Id && a.IconHash == oldIcon && db.Entry(a).State != EntityState.Deleted);
        var stillUsed = stillUsedLocal
            || await db.Apps.AnyAsync(a => a.Id != app.Id && a.IconHash == oldIcon, ct)
            // A pending or applied icon_hash override is an operator-placed
            // file; the applier restores it after this pass, so keep it.
            || await db.AppOverrides.AnyAsync(
                o => o.DeletedAt == null && o.Field == AppOverrideFields.IconHashName && o.Value == oldIcon, ct);
        if (!stillUsed)
        {
            // Warning on purpose: mass disappearance of icon files once went
            // unnoticed because deletes are silent; the log is the audit trail.
            log?.LogWarning("Deleting orphaned icon file {IconHash}", oldIcon);
            try { File.Delete(Path.Combine(options.IconStorePath, $"{oldIcon}.png")); }
            catch { /* stale file is harmless; next deploy wipes nothing, icons are content-addressed */ }
        }
    }

    /// <summary>
    /// Adopts a resolved raster icon, shared by the immediate path of a
    /// prepared refresh and the batch commit: provenance always syncs, the
    /// file is written before the bytes are compared so a deleted file heals
    /// even on equal bytes, and equal bytes still count when the file was
    /// restored or the caller forces the refresh. Returns true when the
    /// caller should report the refresh as Enriched.
    /// </summary>
    internal async Task<bool> AdoptAsync(App app, ProcessedIcon icon, bool force, CancellationToken ct)
    {
        // Sync even on equal bytes to heal stale flags; the flag is the
        // caller's true-adaptive signal (SyncService forwards the staged
        // root kind), not a blanket XML marker.
        app.IconAdaptive = icon.Adaptive;

        // Write first: a missing file must heal even when the bytes still
        // match the recorded icon (write is a no-op otherwise).
        var healed = !IconFileExists(icon.Sha256);
        await WriteIconFileAsync(icon, ct);
        if (icon.Sha256 == app.IconHash)
        {
            // Enriched (not UpToDate) when a file was restored, so the pass
            // tally proves the healing happened; force counts every rewrite
            // (swap detection needs fresh bytes).
            return force || healed;
        }

        var oldIcon = app.IconHash;
        app.IconHash = icon.Sha256;
        await DeleteIconIfOrphanedAsync(app, oldIcon, ct);
        return true;
    }

    /// <summary>
    /// Batched icon refresh (phase C): normalizes
    /// one batch-rendered PNG and adopts it when it differs. Failures keep
    /// the current icon without touching the row.
    /// </summary>
    internal async Task<EnrichResult> CommitAsync(
        App app, byte[]? png, CancellationToken ct = default, bool force = false, bool isAdaptive = false)
    {
        try
        {
            var icon = png is null ? null : LauncherIconService.NormalizeRender(png, isAdaptive);
            if (icon is null)
            {
                return new EnrichResult(EnrichOutcome.Failed, "Icon refresh: batch render produced no usable icon; kept.");
            }

            return await AdoptAsync(app, icon, force, ct)
                ? new EnrichResult(EnrichOutcome.Enriched, null)
                : new EnrichResult(EnrichOutcome.UpToDate, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EnrichResult(EnrichOutcome.Failed, $"Icon refresh: {ex.Message}");
        }
    }
}
