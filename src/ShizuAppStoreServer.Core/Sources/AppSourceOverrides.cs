namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Release home for a listed repo whose APK builds live on the GitCode mirror:
/// the mirror owner/repo plus the GitHub repo that carries the README (the
/// mirror publishes none).
/// </summary>
public sealed record GitCodeMirror(
    string Owner, string Repo, string ReadmeOwner, string ReadmeRepo);

/// <summary>
/// Per-entry source behavior that does not follow from the list URL alone,
/// keyed by <c>apps.slug</c>. AppEnricher and ReleasePoller both consult this
/// so the two cannot drift: when the poll keeps the listed repo it watches a
/// feed that never changes and force-enriches the app on every fast pass.
/// Rows live in <c>app_overrides</c> (<see cref="DbAppSourceOverrides"/>), so
/// operators edit them with SQL and the applier's consume-once invalidation
/// gets the behavior applied on the next pass.
/// </summary>
public interface IAppSourceOverrides
{
    /// <summary>Listed repo whose releases live in a different repo.</summary>
    (string Owner, string Repo)? RemapReleaseHome(string appSlug);

    /// <summary>GitCode mirror for a listed GitHub repo, when one exists.</summary>
    GitCodeMirror? GitCodeMirrorFor(string appSlug);

    /// <summary>Every release carries a different app, so latest-only compare misses them.</summary>
    bool ScansAllReleases(string appSlug);

    /// <summary>The repo's stable channel is empty; prefer its newest prerelease.</summary>
    bool PrefersPrerelease(string appSlug);

    /// <summary>Release polling has no cheap signal; the entry stays on the due window.</summary>
    bool SkipReleasePoll(string appSlug);
}

/// <summary>
/// Pass-through implementation for hand-built callers (tests) that do not
/// construct a database-backed seam.
/// </summary>
public sealed class NoAppSourceOverrides : IAppSourceOverrides
{
    public static NoAppSourceOverrides Instance { get; } = new();

    private NoAppSourceOverrides()
    {
    }

    public (string Owner, string Repo)? RemapReleaseHome(string appSlug) => null;

    public GitCodeMirror? GitCodeMirrorFor(string appSlug) => null;

    public bool ScansAllReleases(string appSlug) => false;

    public bool PrefersPrerelease(string appSlug) => false;

    public bool SkipReleasePoll(string appSlug) => false;
}
