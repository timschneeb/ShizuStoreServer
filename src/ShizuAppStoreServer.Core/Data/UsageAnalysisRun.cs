namespace ShizuAppStoreServer.Core.Data;

/// <summary>Lifecycle of one queued AI source analysis.</summary>
public enum UsageAnalysisStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// One AI source-analysis attempt for an app. The table doubles as the work
/// queue: <c>Pending</c> rows with a due <c>NextAttemptAt</c> are claimed by
/// the worker, and the partial unique index keeps at most one active row per
/// app. Every attempt is kept so token usage and cost stay auditable.
/// </summary>
public sealed class UsageAnalysisRun
{
    public long Id { get; set; }

    public long AppId { get; set; }
    public App? App { get; set; }

    public UsageAnalysisStatus Status { get; set; }

    /// <summary>Attempts so far, including the current one.</summary>
    public int Attempts { get; set; }

    public string? RepoForge { get; set; }

    /// <summary>Commit the source was checked out at; pins the evidence to a revision.</summary>
    public string? RepoCommit { get; set; }

    /// <summary>Release tag the commit came from, when one was resolved.</summary>
    public string? RepoRef { get; set; }

    public string? Model { get; set; }
    public int PromptVersion { get; set; }

    /// <summary>Last failure, truncated; null while the run is healthy.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// File name of the rendered HTML conversation log for this attempt,
    /// relative to <c>UsageAnalysis:LogPath</c>; null when not written.
    /// </summary>
    public string? LogFile { get; set; }

    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Computed from the configured per-million prices.</summary>
    public decimal CostUsd { get; set; }

    /// <summary>Tool invocations the model made across all turns.</summary>
    public int ToolCalls { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Earliest time the worker may claim this row.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }
}
