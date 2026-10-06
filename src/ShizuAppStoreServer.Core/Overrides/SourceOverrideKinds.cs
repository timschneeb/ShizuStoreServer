using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Overrides;

/// <summary>
/// The <c>app_overrides</c> field kinds that steer source behavior instead of
/// writing an App column. The applier routes them to a consume-once recheck
/// and the database seam parses their values; sharing the names and parsers
/// here keeps the two from drifting.
/// </summary>
internal static class SourceOverrideKinds
{
    internal const string ReleaseHome = "source_release_home";
    internal const string GitCodeMirror = "source_gitcode_mirror";
    internal const string ScanAllReleases = "source_scan_all_releases";
    internal const string PreferPrerelease = "source_prefer_prerelease";

    internal static bool IsSource(string field) =>
        field is ReleaseHome or GitCodeMirror or ScanAllReleases or PreferPrerelease;

    /// <summary>Parse error for an invalid value, or null when it is usable.</summary>
    internal static string? Validate(string field, string value) => field switch
    {
        ReleaseHome => TryParseReleaseHome(value, out _) ? null : "expected owner/repo",
        GitCodeMirror => TryParseGitCodeMirror(value, out _)
            ? null
            : "expected targetOwner/targetRepo|readmeOwner/readmeRepo",
        ScanAllReleases or PreferPrerelease => TryParseFlag(value, out _) ? null : "expected true or false",
        _ => "unknown source field",
    };

    internal static bool TryParseReleaseHome(string value, out (string Owner, string Repo) target) =>
        TrySplitRepo(value.Trim(), out target.Owner, out target.Repo);

    internal static bool TryParseGitCodeMirror(string value, out GitCodeMirror mirror)
    {
        mirror = null!;
        var parts = value.Split('|');
        if (parts.Length != 2
            || !TrySplitRepo(parts[0].Trim(), out var owner, out var repo)
            || !TrySplitRepo(parts[1].Trim(), out var readmeOwner, out var readmeRepo))
        {
            return false;
        }

        mirror = new GitCodeMirror(owner, repo, readmeOwner, readmeRepo);
        return true;
    }

    internal static bool TryParseFlag(string value, out bool flag) => bool.TryParse(value.Trim(), out flag);

    private static bool TrySplitRepo(string value, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1 || value.IndexOf('/', slash + 1) >= 0)
        {
            return false;
        }

        owner = value[..slash];
        repo = value[(slash + 1)..];
        return true;
    }
}
