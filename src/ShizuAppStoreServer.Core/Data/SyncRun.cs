namespace ShizuAppStoreServer.Core.Data;

/// <summary>One list-sync pass (table <c>sync_runs</c>).</summary>
public sealed class SyncRun
{
    public long Id { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>What triggered the run: <c>scheduled</c>, <c>webhook</c>, <c>manual</c>, <c>backfill</c>.</summary>
    public required string Trigger { get; set; }

    public string? HeadCommit { get; set; }

    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int Failed { get; set; }
    public int IssueCount { get; set; }
    public string? Error { get; set; }
}
