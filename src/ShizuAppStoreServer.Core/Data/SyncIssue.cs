namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// One entry of the current catalog health snapshot (table <c>sync_issues</c>).
/// The table holds only the most recent completed run: every successful pass
/// replaces all rows, skipped and failed passes leave it untouched.
/// </summary>
public sealed class SyncIssue
{
    public long Id { get; set; }

    public long SyncRunId { get; set; }
    public SyncRun? SyncRun { get; set; }

    public IssueKind Kind { get; set; }

    /// <summary>Machine readable rule name (e.g. <c>missing_license</c>).</summary>
    public required string Rule { get; set; }

    public long? AppId { get; set; }
    public App? App { get; set; }

    public string? Slug { get; set; }

    public required string Message { get; set; }

    /// <summary>Parser location (entry or category name); null for enrich/quality rows.</summary>
    public string? Location { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
