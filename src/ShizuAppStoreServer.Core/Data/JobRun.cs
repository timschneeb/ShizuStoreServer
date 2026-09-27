using ShizuAppStoreServer.Core.Jobs;

namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// One run of any background job (table <c>job_runs</c>). The row is the run
/// header; per-run detail lives in <see cref="JobEvent"/> rows. Rows are kept
/// indefinitely so the history stays auditable.
/// </summary>
public sealed class JobRun
{
    public long Id { get; set; }

    public JobKind Kind { get; set; }

    public JobTrigger Trigger { get; set; }

    public JobStatus Status { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>UTC day of <see cref="StartedAt"/>, the SQL-side stats filter.</summary>
    public DateOnly StartedDay { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
    public int? DurationMs { get; set; }

    /// <summary>
    /// Run specific pointer: the list head commit for syncs, the analysed
    /// commit for usage analysis.
    /// </summary>
    public string? Reference { get; set; }

    public int ItemsTotal { get; set; }
    public int ItemsOk { get; set; }
    public int ItemsSkipped { get; set; }
    public int ItemsFailed { get; set; }

    public int EventsCount { get; set; }

    /// <summary>Events the sink had to drop because its buffer was full.</summary>
    public int EventsDropped { get; set; }

    public string? Summary { get; set; }
    public string? Error { get; set; }

    /// <summary>Run specific JSON metadata (jsonb); shape depends on <see cref="Kind"/>.</summary>
    public string? Metadata { get; set; }

    public long? UsageAnalysisRunId { get; set; }
    public UsageAnalysisRun? UsageAnalysisRun { get; set; }
}
