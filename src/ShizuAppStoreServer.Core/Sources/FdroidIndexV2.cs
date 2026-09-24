using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Data extracted from one F-Droid repo <c>index-v2.json</c>: screenshot
/// paths and signing certificates. Only <c>index-v2.json</c> carries
/// screenshots (the legacy <c>index.xml</c> has none) and its
/// <c>versions.&lt;sha&gt;.manifest.signer.sha256</c> is the authoritative
/// signing-cert SHA-256, so both come from the same deserialization of the
/// large (tens of MB) file. The deserializer models only those paths and
/// skips the rest.
/// </summary>
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
    IReadOnlyDictionary<string, IReadOnlyList<string>> Screenshots,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Signers);

/// <summary>Parser for F-Droid repo <c>index-v2.json</c>.</summary>
public static class FdroidIndexV2Parser
{
    public static FdroidIndexV2Data Parse(byte[] json)
    {
        var repo = JsonSerializer.Deserialize<RepoDto>(json);
        var screenshots = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var signers = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        if (repo?.Packages is null)
        {
            return new FdroidIndexV2Data(screenshots, signers);
        }

        foreach (var (id, package) in repo.Packages)
        {
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

        return new FdroidIndexV2Data(screenshots, signers);
    }

    /// <summary>Package id to screenshot paths; for callers that need only screenshots.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseScreenshots(byte[] json) =>
        Parse(json).Screenshots;

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
            var certificates = (version?.Manifest?.Signer?.Sha256 ?? [])
                .Select(CertFingerprint.Normalize)
                .Where(value => value is not null)
                .Select(value => value!)
                .ToList();
            if (certificates.Count > 0)
            {
                result[fileSha256.ToLowerInvariant()] = certificates;
            }
        }
        return result;
    }

    private sealed class RepoDto { [JsonPropertyName("packages")] public Dictionary<string, PackageDto?>? Packages { get; set; } }
    private sealed class PackageDto
    {
        [JsonPropertyName("metadata")] public MetadataDto? Metadata { get; set; }
        [JsonPropertyName("versions")] public Dictionary<string, VersionDto?>? Versions { get; set; }
    }
    private sealed class MetadataDto { [JsonPropertyName("screenshots")] public Dictionary<string, Dictionary<string, List<ShotDto>>>? Screenshots { get; set; } }
    private sealed class VersionDto { [JsonPropertyName("manifest")] public ManifestDto? Manifest { get; set; } }
    private sealed class ManifestDto { [JsonPropertyName("signer")] public SignerDto? Signer { get; set; } }
    private sealed class SignerDto { [JsonPropertyName("sha256")] public List<string>? Sha256 { get; set; } }
    private sealed class ShotDto { [JsonPropertyName("name")] public string? Name { get; set; } }
}
