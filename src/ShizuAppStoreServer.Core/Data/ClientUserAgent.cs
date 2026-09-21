namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Anonymous client usage stats (table <c>client_user_agents</c>): one row per
/// distinct User-Agent string seen on a tracked request. Written only by the
/// in-process tracking worker; no endpoint reads or writes it.
/// </summary>
public sealed class ClientUserAgent
{
    /// <summary>Header and column cap; longer agents are truncated, not split.</summary>
    public const int MaxUserAgentLength = 512;

    public long Id { get; set; }

    public string UserAgent { get; set; } = "";
    public long RequestCount { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string? LastPath { get; set; }
}
