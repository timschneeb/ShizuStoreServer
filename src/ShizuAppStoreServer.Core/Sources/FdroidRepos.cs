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
    /// Icon URL from the repo-root-relative <c>metadata.icon.name</c>, e.g.
    /// <c>/pkg/en-US/icon.png</c>. Unlike the legacy <c>index.xml</c> layout
    /// there is no shared icon directory to fall back to.
    /// </summary>
    public static IReadOnlyList<string> IconUrls(string repoBase, string iconFile) =>
    [
        $"{repoBase.TrimEnd('/')}/{iconFile.TrimStart('/')}",
    ];
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
    string? SigSha256 = null);
