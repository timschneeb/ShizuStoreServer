namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Release home for a listed repo whose APK builds live on the GitCode mirror:
/// the mirror owner/repo plus the GitHub repo that carries the README (the
/// mirror publishes none).
/// </summary>
public sealed record GitCodeMirror(
    string Owner, string Repo, string ReadmeOwner, string ReadmeRepo);

/// <summary>
/// Per-entry source behavior that does not follow from the list URL alone.
/// AppEnricher and ReleasePoller both consult this so the two cannot drift:
/// when the poll keeps the listed repo it watches a feed that never changes
/// and force-enriches the app on every fast pass. A database-backed
/// implementation can replace <see cref="StaticAppSourceOverrides"/> when
/// operator overrides land.
/// </summary>
public interface IAppSourceOverrides
{
    /// <summary>Listed repo whose releases live in a different repo.</summary>
    (string Owner, string Repo) RemapReleaseHome(string owner, string repo);

    /// <summary>GitCode mirror for a listed GitHub repo, when one exists.</summary>
    GitCodeMirror? GitCodeMirrorFor(string owner, string repo);

    /// <summary>Every release carries a different app, so latest-only compare misses them.</summary>
    bool ScansAllReleases(string owner, string repo);

    /// <summary>Release polling has no cheap signal; the entry stays on the due window.</summary>
    bool SkipReleasePoll(string owner, string repo);
}

/// <summary>
/// The in-code special cases. Remap answers the release home, the mirror and
/// all-releases answers pick the fetch shape before dispatch, and the poll
/// skip folds both into the fast-path decision.
/// </summary>
public sealed class StaticAppSourceOverrides : IAppSourceOverrides
{
    public static StaticAppSourceOverrides Instance { get; } = new();

    private StaticAppSourceOverrides()
    {
    }

    // The list links instafel's source monorepo; the updater APK ships from a
    // separate release repo.
    private const string InstafelListOwner = "mamiiblt";
    private const string InstafelListRepo = "instafel";
    private const string InstafelUpdaterOwner = "instafel";
    private const string InstafelUpdaterRepo = "u-rel";

    // LinkSheet stopped publishing releases from its source repo; the nightly
    // repo carries them. The source repo stays the analysis target.
    private const string LinkSheetListOwner = "LinkSheet";
    private const string LinkSheetListRepo = "LinkSheet";
    private const string LinkSheetNightlyRepo = "nightly";

    // hlbmerge_flutter rebuilds only on the GitCode mirror; its README comes
    // from the listed GitHub repo.
    private const string HlbmergeGitHubOwner = "molihuan";
    private const string HlbmergeGitHubRepo = "hlbmerge_flutter";
    private const string HlbmergeGitCodeOwner = "bigmolihuan";
    private const string HlbmergeGitCodeRepo = "hlbmerge_flutter";

    // One repo, several distinct apps, each in its own GitHub release.
    private const string SmartspacerOwner = "KieronQuinn";
    private const string SmartspacerRepo = "SmartspacerPlugins";

    public (string Owner, string Repo) RemapReleaseHome(string owner, string repo)
    {
        if (owner == InstafelListOwner && repo == InstafelListRepo)
        {
            return (InstafelUpdaterOwner, InstafelUpdaterRepo);
        }

        if (owner == LinkSheetListOwner && repo == LinkSheetListRepo)
        {
            return (owner, LinkSheetNightlyRepo);
        }

        return (owner, repo);
    }

    public GitCodeMirror? GitCodeMirrorFor(string owner, string repo) =>
        owner == HlbmergeGitHubOwner && repo == HlbmergeGitHubRepo
            ? new GitCodeMirror(
                HlbmergeGitCodeOwner, HlbmergeGitCodeRepo,
                HlbmergeGitHubOwner, HlbmergeGitHubRepo)
            : null;

    public bool ScansAllReleases(string owner, string repo) =>
        owner == SmartspacerOwner && repo == SmartspacerRepo;

    public bool SkipReleasePoll(string owner, string repo) =>
        GitCodeMirrorFor(owner, repo) is not null || ScansAllReleases(owner, repo);
}
