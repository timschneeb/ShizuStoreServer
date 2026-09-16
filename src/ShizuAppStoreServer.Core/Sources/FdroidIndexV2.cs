using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Reads screenshot references from an F-Droid repo <c>index-v2.json</c>.
/// Only <c>index-v2.json</c> carries screenshots (the legacy <c>index.xml</c>
/// has none), and the file is large (tens of MB), so the deserializer models
/// only the <c>packages.*.metadata.screenshots</c> path and skips the rest.
/// Each package maps to repo-root-relative image paths like
/// <c>/pkg/en-US/phoneScreenshots/00.png</c>; the caller prefixes the repo
/// base. Phone shots are preferred, falling back to the first form factor
/// present; within a form factor <c>en-US</c> is preferred, else the first
/// locale, both in ordinal key order for determinism.
/// </summary>
public static class FdroidIndexV2Parser
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseScreenshots(byte[] json)
    {
        var repo = JsonSerializer.Deserialize<RepoDto>(json);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (repo?.Packages is null)
        {
            return result;
        }

        foreach (var (id, package) in repo.Packages)
        {
            var names = SelectScreenshots(package?.Metadata?.Screenshots);
            if (names.Count > 0)
            {
                result[id] = names;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> SelectScreenshots(
        Dictionary<string, Dictionary<string, List<ShotDto>>>? formFactors)
    {
        if (formFactors is null || formFactors.Count == 0)
        {
            return [];
        }

        var form = formFactors.TryGetValue("phone", out var phone)
            ? phone
            : formFactors.OrderBy(kv => kv.Key, StringComparer.Ordinal).First().Value;
        if (form is null || form.Count == 0)
        {
            return [];
        }

        var locale = form.TryGetValue("en-US", out var english)
            ? english
            : form.OrderBy(kv => kv.Key, StringComparer.Ordinal).First().Value;

        return (locale ?? [])
            .Select(shot => shot.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
    }

    private sealed class RepoDto
    {
        [JsonPropertyName("packages")]
        public Dictionary<string, PackageDto?>? Packages { get; set; }
    }

    private sealed class PackageDto
    {
        [JsonPropertyName("metadata")]
        public MetadataDto? Metadata { get; set; }
    }

    private sealed class MetadataDto
    {
        [JsonPropertyName("screenshots")]
        public Dictionary<string, Dictionary<string, List<ShotDto>>>? Screenshots { get; set; }
    }

    private sealed class ShotDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
