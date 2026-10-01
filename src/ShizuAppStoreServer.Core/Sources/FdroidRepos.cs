using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>Known F-Droid-compatible repos and their <c>index-v2.json</c> layout.</summary>
public static class FdroidRepos
{
    public const string DefaultFDroidBase = "https://f-droid.org/repo/";

    public const string DefaultIzzyBase = "https://apt.izzysoft.de/fdroid/repo/";

    /// <summary>
    /// Upstream repo base, overridable through <c>Enrichment:FdroidRepoBase</c>.
    /// f-droid.org throttles datacenter IPs to a few hundred KB/s, which starves
    /// the 60MB index, so production points at a mirror. Caches key by base, so
    /// switching mid-run is safe.
    /// </summary>
    public static string FDroidBase { get; set; } = DefaultFDroidBase;

    /// <summary>
    /// IzzyOnDroid base, overridable through <c>Enrichment:IzzyRepoBase</c>.
    /// The official host refuses datacenter IPs outright, so production uses a
    /// mirror here as well.
    /// </summary>
    public static string IzzyBase { get; set; } = DefaultIzzyBase;

    /// <summary>Optional second Izzy mirror, used when the primary base fails.</summary>
    public static string? IzzyBaseFallback { get; set; }

    /// <summary>Fallback base for a repo base, null when none is configured.</summary>
    public static string? FallbackFor(string repoBase) =>
        string.Equals(repoBase.TrimEnd('/'), IzzyBase.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            ? IzzyBaseFallback
            : null;

    public static string BaseFor(SourceKind kind) =>
        kind == SourceKind.Izzy ? IzzyBase : FDroidBase;

    /// <summary>
    /// Applies configured repo base overrides. Every host that publishes
    /// downloads (API and storefront) must call this with the same values,
    /// otherwise <see cref="ClientDownloadUrl(string)"/> cannot map stored
    /// mirror URLs back to canonical upstream.
    /// </summary>
    public static void Configure(string? fdroidBase, string? izzyBase, string? izzyFallback)
    {
        if (!string.IsNullOrWhiteSpace(fdroidBase))
        {
            FDroidBase = fdroidBase.TrimEnd('/') + "/";
        }

        if (!string.IsNullOrWhiteSpace(izzyBase))
        {
            IzzyBase = izzyBase.TrimEnd('/') + "/";
        }

        if (!string.IsNullOrWhiteSpace(izzyFallback))
        {
            IzzyBaseFallback = izzyFallback.TrimEnd('/') + "/";
        }
    }

    /// <summary>
    /// Icon URL from the repo-root-relative <c>metadata.icon.name</c>, e.g.
    /// <c>/pkg/en-US/icon.png</c>. Unlike the legacy <c>index.xml</c> layout
    /// there is no shared icon directory to fall back to.
    /// </summary>
    public static IReadOnlyList<string> IconUrls(string repoBase, string iconFile) =>
    [
        $"{repoBase.TrimEnd('/')}/{iconFile.TrimStart('/')}",
    ];

    /// <summary>Canonical upstream URL for a download stored against the configured repo bases.</summary>
    public static string ClientDownloadUrl(string apkUrl) =>
        ClientDownloadUrl(apkUrl, FDroidBase, IzzyBase, IzzyBaseFallback);

    /// <summary>
    /// Rewrites an <c>apkUrl</c> on a configured repo base back to the
    /// canonical upstream host. Mirrors exist for server-side fetches only
    /// (f-droid.org throttles datacenter IPs) and some refuse .apk requests
    /// that do not look like the F-Droid client, so client-facing output and
    /// browsers always use upstream. Bases are parameters so tests can pin
    /// them without touching process-wide configuration.
    /// </summary>
    public static string ClientDownloadUrl(
        string apkUrl, string? fdroidBase, string? izzyBase, string? izzyFallback) =>
        RewriteBase(apkUrl, fdroidBase, DefaultFDroidBase)
        ?? RewriteBase(apkUrl, izzyBase, DefaultIzzyBase)
        ?? RewriteBase(apkUrl, izzyFallback, DefaultIzzyBase)
        ?? apkUrl;

    private static string? RewriteBase(string apkUrl, string? mirrorBase, string upstreamBase)
    {
        if (string.IsNullOrEmpty(mirrorBase))
        {
            return null;
        }

        var prefix = mirrorBase.TrimEnd('/') + "/";
        return apkUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? upstreamBase.TrimEnd('/') + "/" + apkUrl[prefix.Length..]
            : null;
    }
}

/// <summary>One release of an app in a repo <c>index-v2.json</c>.</summary>
public sealed record FdroidPackageInfo(
    string PackageName,
    long VersionCode,
    string? VersionName,
    string ApkName,
    string? Sha256,
    long? Size,
    int? MinSdk,
    /// <summary>Repo-root-relative icon path from <c>metadata.icon</c>.</summary>
    string? IconFile,
    /// <summary>Upstream source repo URL from <c>metadata.sourceCode</c>.</summary>
    string? SourceUrl = null,
    /// <summary>Native ABI from <c>manifest.nativecode</c>; null for fat/universal builds.</summary>
    string? Abi = null,
    /// <summary>Long description from <c>metadata.description</c> (HTML).</summary>
    string? LongDescription = null,
    /// <summary>
    /// Signing-cert SHA-256 from <c>manifest.signer.sha256</c>, the
    /// authoritative fingerprint; null when the index did not carry it.
    /// </summary>
    string? SigSha256 = null,
    /// <summary>Declared permission names from <c>manifest.usesPermission</c>.</summary>
    IReadOnlyList<string>? Permissions = null,
    /// <summary>
    /// Publish time from the version entry's <c>added</c> field (Unix ms).
    /// The only release date these repos publish; null when absent or out of
    /// range.
    /// </summary>
    DateTimeOffset? Added = null);
