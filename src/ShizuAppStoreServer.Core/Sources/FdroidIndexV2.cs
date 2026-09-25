using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Everything the server reads from one F-Droid repo <c>index-v2.json</c>:
/// releases, screenshot paths and signing certificates. The v2 file is the
/// only repo index the server uses; it carries every release and metadata
/// field the legacy <c>index.xml</c> provided, plus the authoritative
/// <c>versions.&lt;sha&gt;.manifest.signer.sha256</c>. The deserializer
/// models only the paths the server consumes and skips the rest.
/// </summary>
/// <param name="Packages">
/// Package id to its releases, newest <c>versionCode</c> first (ties broken
/// by APK file name for determinism). A release can ship one APK per
/// architecture, so all siblings are kept and the caller decides which to
/// analyze.
/// </param>
/// <param name="Screenshots">
/// Package id to repo-root-relative image paths like
/// <c>/pkg/en-US/phoneScreenshots/00.png</c>; the caller prefixes the repo
/// base. Phone shots are preferred, falling back to the first form factor
/// present; within a form factor <c>en-US</c> is preferred, else the first
/// locale, both in ordinal key order for determinism.
/// </param>
/// <param name="Signers">
/// Package id to APK file SHA-256 (lowercased) to normalized signing-cert
/// SHA-256 list.
/// </param>
public sealed record FdroidIndexV2Data(
    IReadOnlyDictionary<string, IReadOnlyList<FdroidPackageInfo>> Packages,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Screenshots,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Signers);

/// <summary>Parser for F-Droid repo <c>index-v2.json</c>.</summary>
public static class FdroidIndexV2Parser
{
    public static FdroidIndexV2Data Parse(byte[] json)
    {
        var repo = JsonSerializer.Deserialize<RepoDto>(json);
        var packages = new Dictionary<string, IReadOnlyList<FdroidPackageInfo>>(StringComparer.Ordinal);
        var screenshots = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var signers = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        if (repo?.Packages is null)
        {
            return new FdroidIndexV2Data(packages, screenshots, signers);
        }

        foreach (var (id, package) in repo.Packages)
        {
            var releases = SelectPackages(id, package?.Versions, package?.Metadata);
            if (releases.Count > 0)
            {
                packages[id] = releases;
            }

            var names = SelectScreenshots(package?.Metadata?.Screenshots);
            if (names.Count > 0)
            {
                screenshots[id] = names;
            }

            var certificates = SelectSigners(package?.Versions);
            if (certificates.Count > 0)
            {
                signers[id] = certificates;
            }
        }

        return new FdroidIndexV2Data(packages, screenshots, signers);
    }

    /// <summary>
    /// Releases for one package. The versions map has no order, so entries
    /// are sorted by <c>versionCode</c> descending with the APK file name as
    /// the deterministic tie-breaker; entries without a file name are
    /// skipped, like the v1 parser skipped packages without an
    /// <c>apkname</c>.
    /// </summary>
    private static IReadOnlyList<FdroidPackageInfo> SelectPackages(
        string id, Dictionary<string, VersionDto?>? versions, MetadataDto? metadata)
    {
        if (versions is null || versions.Count == 0) return [];
        var iconFile = NullIfBlank(SelectLocalized(metadata?.Icon)?.Name);
        var description = SelectLocalized(metadata?.Description);
        var sourceUrl = NullIfBlank(metadata?.SourceCode);
        var releases = new List<FdroidPackageInfo>(versions.Count);
        foreach (var (fileSha256, version) in versions)
        {
            var file = version?.File;
            var apkName = NullIfBlank(file?.Name);
            if (apkName is null)
            {
                continue;
            }

            var manifest = version?.Manifest;
            var certificates = NormalizeSigners(manifest?.Signer?.Sha256);
            releases.Add(new FdroidPackageInfo(
                id,
                manifest?.VersionCode ?? 0,
                NullIfBlank(manifest?.VersionName),
                apkName.TrimStart('/'),
                NullIfBlank(file?.Sha256) ?? NullIfBlank(fileSha256),
                file?.Size,
                manifest?.UsesSdk?.MinSdkVersion,
                iconFile,
                sourceUrl,
                manifest?.NativeCode?.FirstOrDefault(),
                description,
                certificates.Count > 0 ? CertFingerprint.Join(certificates) : null));
        }

        return releases
            .OrderByDescending(release => release.VersionCode)
            .ThenBy(release => release.ApkName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// v2 stores name, description and icon per locale; <c>en-US</c> wins,
    /// else the first key in ordinal order so the result is deterministic.
    /// </summary>
    private static T? SelectLocalized<T>(Dictionary<string, T?>? localized) where T : class =>
        localized is null || localized.Count == 0
            ? null
            : localized.TryGetValue("en-US", out var english) && english is not null
                ? english
                : localized.OrderBy(kv => kv.Key, StringComparer.Ordinal).First().Value;

    private static IReadOnlyList<string> SelectScreenshots(
        Dictionary<string, Dictionary<string, List<ShotDto>>>? formFactors)
    {
        if (formFactors is null || formFactors.Count == 0) return [];
        var form = formFactors.TryGetValue("phone", out var phone)
            ? phone
            : formFactors.OrderBy(kv => kv.Key, StringComparer.Ordinal).First().Value;
        if (form is null || form.Count == 0) return [];
        var locale = form.TryGetValue("en-US", out var english)
            ? english
            : form.OrderBy(kv => kv.Key, StringComparer.Ordinal).First().Value;
        return (locale ?? []).Select(shot => shot.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToList();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> SelectSigners(
        Dictionary<string, VersionDto?>? versions)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (versions is null) return result;
        foreach (var (fileSha256, version) in versions)
        {
            if (string.IsNullOrWhiteSpace(fileSha256)) continue;
            var certificates = NormalizeSigners(version?.Manifest?.Signer?.Sha256);
            if (certificates.Count > 0)
            {
                result[fileSha256.ToLowerInvariant()] = certificates;
            }
        }
        return result;
    }

    private static List<string> NormalizeSigners(List<string>? sha256) =>
        (sha256 ?? [])
            .Select(CertFingerprint.Normalize)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class RepoDto { [JsonPropertyName("packages")] public Dictionary<string, PackageDto?>? Packages { get; set; } }
    private sealed class PackageDto
    {
        [JsonPropertyName("metadata")] public MetadataDto? Metadata { get; set; }
        [JsonPropertyName("versions")] public Dictionary<string, VersionDto?>? Versions { get; set; }
    }
    private sealed class MetadataDto
    {
        [JsonPropertyName("screenshots")] public Dictionary<string, Dictionary<string, List<ShotDto>>>? Screenshots { get; set; }
        [JsonPropertyName("description")] public Dictionary<string, string?>? Description { get; set; }
        [JsonPropertyName("icon")] public Dictionary<string, LocalizedFileDto?>? Icon { get; set; }
        [JsonPropertyName("sourceCode")] public string? SourceCode { get; set; }
    }
    private sealed class LocalizedFileDto { [JsonPropertyName("name")] public string? Name { get; set; } }
    private sealed class VersionDto
    {
        [JsonPropertyName("file")] public FileDto? File { get; set; }
        [JsonPropertyName("manifest")] public ManifestDto? Manifest { get; set; }
    }
    private sealed class FileDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
    }
    private sealed class ManifestDto
    {
        [JsonPropertyName("versionCode")] public long? VersionCode { get; set; }
        [JsonPropertyName("versionName")] public string? VersionName { get; set; }
        [JsonPropertyName("nativecode")] public List<string>? NativeCode { get; set; }
        [JsonPropertyName("usesSdk")] public UsesSdkDto? UsesSdk { get; set; }
        [JsonPropertyName("signer")] public SignerDto? Signer { get; set; }
    }
    private sealed class UsesSdkDto { [JsonPropertyName("minSdkVersion")] public int? MinSdkVersion { get; set; } }
    private sealed class SignerDto { [JsonPropertyName("sha256")] public List<string>? Sha256 { get; set; } }
    private sealed class ShotDto { [JsonPropertyName("name")] public string? Name { get; set; } }
}
