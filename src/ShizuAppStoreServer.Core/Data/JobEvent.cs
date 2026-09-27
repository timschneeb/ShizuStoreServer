using ShizuAppStoreServer.Core.Jobs;

namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// One ordered event inside a run (table <c>job_events</c>). Sequence numbers
/// are assigned by the emitting session, so the stream stays chronological even
/// when enrichment runs apps in parallel.
/// </summary>
public sealed class JobEvent
{
    public long Id { get; set; }

    public long JobRunId { get; set; }
    public JobRun? JobRun { get; set; }

    public int Seq { get; set; }

    public DateTimeOffset At { get; set; }

    public JobEventLevel Level { get; set; }
    public JobEventType Type { get; set; }

    /// <summary>Phase the event belongs to (list, enrich, render, ...); null when generic.</summary>
    public string? Phase { get; set; }

    public long? AppId { get; set; }
    public App? App { get; set; }

    public string? Slug { get; set; }

    public required string Message { get; set; }

    /// <summary>Event specific JSON payload (jsonb); null when the message says it all.</summary>
    public string? Data { get; set; }

    public int? DurationMs { get; set; }
}
