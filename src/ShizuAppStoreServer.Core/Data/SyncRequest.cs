namespace ShizuAppStoreServer.Core.Data;

/// <summary>Webhook/manual trigger queue (table <c>sync_requests</c>), drained by the fast loop.</summary>
public sealed class SyncRequest
{
    public long Id { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// Upgrades the drained pass to a full-catalog re-check (like the
    /// nightly) so operators can backfill without a restart.
    /// </summary>
    public bool Full { get; set; }

    public bool Processed { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
