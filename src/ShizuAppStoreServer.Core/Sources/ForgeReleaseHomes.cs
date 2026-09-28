namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Listed repos whose releases live somewhere else (user calls). Shared by
/// AppEnricher and ReleasePoller so the two cannot drift: when the poll keeps
/// the listed repo it watches a feed that never changes and force-enriches the
/// app on every fast pass.
/// </summary>
public static class ForgeReleaseHomes
{
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

    public static (string Owner, string Repo) Remap(string owner, string repo)
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
}
