namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// Version names reach the catalog from APK badging or forge release tags.
/// Broken release tooling can bake shell output into the APK's
/// <c>versionName</c> (a tag-less <c>git describe</c> writes
/// "fatal: No names found, cannot describe anything."), so implausible values
/// count as unknown and the release tag stands in.
/// </summary>
public static class VersionNames
{
    /// <summary>No real version name is this long; command output is.</summary>
    private const int MaxLength = 48;

    private static readonly string[] ErrorPrefixes = ["fatal:", "error:", "warning:"];

    /// <summary>Trimmed name when it looks like a version, otherwise null.</summary>
    public static string? Sanitize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxLength)
        {
            return null;
        }

        if (trimmed.Any(char.IsControl))
        {
            return null;
        }

        if (ErrorPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return trimmed.Contains("No names found", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("cannot describe", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    /// <summary>Badging name when plausible, otherwise the sanitized release tag.</summary>
    public static string? Resolve(string? badgingName, string? releaseTag) =>
        Sanitize(badgingName) ?? Sanitize(releaseTag);
}
