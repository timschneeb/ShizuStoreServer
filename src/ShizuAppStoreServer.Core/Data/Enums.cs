namespace ShizuAppStoreServer.Core.Data;

/// <summary>Which awesome-list file an entry comes from.</summary>
public enum Listing
{
    /// <summary>Main <c>README.md</c> (open-source / source-available).</summary>
    Main,
    /// <summary><c>pages/CLOSED_SOURCE.md</c>.</summary>
    ClosedSource,
}

/// <summary>What kind of thing a list entry is.</summary>
public enum AppType
{
    /// <summary>Regular app (default).</summary>
    App,
    /// <summary>Entry under a "Development libraries" section.</summary>
    Library,
    /// <summary>Automate flow (category "Flows for Automate").</summary>
    Flow,
}

/// <summary>Top-level grouping of a category, derived from the <c>##</c> section.</summary>
public enum CategorySection
{
    Apps,
    Libraries,
    Misc,
}

/// <summary>Hosting forge detected from the entry/source URL. Set by the resolvers (M4).</summary>
public enum SourceKind
{
    GitHub,
    GitLab,
    Codeberg,
    FDroid,
    Izzy,
    Play,
    Other,
}

/// <summary>What the client can do with an entry. Set by the resolvers (M4).</summary>
public enum Availability
{
    /// <summary>Direct APK download available via <c>ApkUrl</c>.</summary>
    DirectApk,
    /// <summary>No APK source; client opens the Play listing instead.</summary>
    PlayRedirect,
    /// <summary>No APK source; client opens the source page in a Custom Tab.</summary>
    LinkOnly,
    /// <summary>Play-sole-source entry; never sent to clients.</summary>
    Excluded,
}
