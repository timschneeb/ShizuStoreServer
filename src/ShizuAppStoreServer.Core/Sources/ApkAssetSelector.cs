namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Picks the APK to enrich from a stable release's assets: prefer an asset
/// whose name contains <c>release</c> (universal APKs are usually tagged
/// that way), otherwise take the largest <c>.apk</c> (arch-specific splits
/// lose to the fatter universal build). Pure static for testability.
/// </summary>
public static class ApkAssetSelector
{
    public static GitHubAsset? PickApk(IReadOnlyList<GitHubAsset> assets) =>
        PickApk(assets, a => a.Name, a => a.Size);

    /// <summary>
    /// Generic pick over any asset shape (GitLab links carry no size, so
    /// ties keep API order). Prefer a name containing <c>release</c>,
    /// otherwise take the largest <c>.apk</c>.
    /// </summary>
    public static T? PickApk<T>(IReadOnlyList<T> assets, Func<T, string> name, Func<T, long> size) =>
        PickApk(assets, name, size, url: null);

    /// <summary>
    /// Variant that also matches the URL: GitLab release links often carry a
    /// generic label ("APK", "Package") while only the URL ends with the
    /// actual filename (e.g. narektor/batt links to Batt-1.3.apk).
    /// </summary>
    public static T? PickApk<T>(IReadOnlyList<T> assets, Func<T, string> name, Func<T, long> size, Func<T, string>? url)
    {
        var apks = assets
            .Where(a => IsApk(name(a)) || (url is not null && IsApk(url(a))))
            .ToList();
        if (apks.Count == 0)
        {
            return default;
        }

        var release = apks.Where(a => name(a).Contains("release", StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = release.Count > 0 ? release : apks;
        // Largest first; OrderByDescending is stable, so input order breaks ties.
        return pool.OrderByDescending(size).First();
    }

    private static bool IsApk(string value) =>
        value.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Picks a release archive to fish an APK out of when no direct
    /// <c>.apk</c> asset exists (some projects only attach a zip, e.g.
    /// AppControl-X v3.0.0). Same preference order as <see cref="PickApk{T}"/>:
    /// a name containing <c>release</c> wins, otherwise the largest.
    /// </summary>
    public static T? PickZip<T>(IReadOnlyList<T> assets, Func<T, string> name, Func<T, long> size)
    {
        var zips = assets
            .Where(a => name(a).EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (zips.Count == 0)
        {
            return default;
        }

        var release = zips.Where(a => name(a).Contains("release", StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = release.Count > 0 ? release : zips;
        return pool.OrderByDescending(size).First();
    }
}
