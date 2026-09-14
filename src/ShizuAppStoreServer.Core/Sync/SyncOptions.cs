namespace ShizuAppStoreServer.Core.Sync;

/// <summary>
/// Sync-loop settings. Binds to the <c>Sync</c> config section; on the server
/// <c>ListPath</c> comes from <c>appsettings.Production.json</c>
/// (<c>/opt/shizuappstore/list</c>), for local runs point it at the
/// <c>awesome-shizuku</c> checkout.
/// </summary>
public sealed class SyncOptions
{
    /// <summary>Local awesome-shizuku git clone (fast loop runs <c>git fetch</c> here).</summary>
    public string ListPath { get; set; } = "/opt/shizuappstore/list";

    /// <summary>Fast-loop period in minutes.</summary>
    public int FastLoopMinutes { get; set; } = 15;

    /// <summary>Nightly full re-check time, <c>HH:mm</c> UTC.</summary>
    public string NightlyTimeUtc { get; set; } = "03:00";

    /// <summary>
    /// Run one pass at startup. True in production (first boot = initial
    /// backfill); integration tests turn it off.
    /// </summary>
    public bool RunOnStartup { get; set; } = true;
}
