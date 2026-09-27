namespace ShizuAppStoreServer.Core.Jobs;

/// <summary>Everything a sink needs to open a run.</summary>
public sealed record JobStart(
    JobKind Kind,
    JobTrigger Trigger,
    DateTimeOffset StartedAt,
    string? Reference = null,
    object? Metadata = null,
    long? UsageAnalysisRunId = null,
    int? ItemCount = null);

/// <summary>Final outcome of a run, written exactly once.</summary>
public sealed record JobFinish(
    JobStatus Status,
    string? Summary = null,
    string? Error = null,
    int ItemsTotal = 0,
    int ItemsOk = 0,
    int ItemsSkipped = 0,
    int ItemsFailed = 0,
    string? Reference = null,
    object? Metadata = null);

/// <summary>One ordered event inside a run.</summary>
public sealed record JobEventRecord(
    int Seq,
    JobEventLevel Level,
    JobEventType Type,
    string Message,
    string? Phase,
    long? AppId,
    string? Slug,
    object? Data,
    int? DurationMs);

/// <summary>
/// Destination for job bookkeeping. Implementations must never throw into a
/// job: a broken sink degrades observability, not the pass.
/// </summary>
public interface IJobSink
{
    IJobSinkSession Begin(JobStart start);

    /// <summary>A pass that found nothing due; still gets a lightweight run row.</summary>
    void Skipped(JobStart start, string reason);

    /// <summary>Marks runs left <c>Running</c> by a previous process as interrupted.</summary>
    Task RecoverInterruptedAsync(CancellationToken ct);
}

/// <summary>Per-run sink state.</summary>
public interface IJobSinkSession : IAsyncDisposable
{
    /// <summary>Null when the sink could not persist the run header.</summary>
    long? RunId { get; }

    /// <summary>Updates the progress denominator once the job selects its work items.</summary>
    void SetItemCount(int count);

    void Write(JobEventRecord record);

    Task FinishAsync(JobFinish finish, CancellationToken ct);
}

/// <summary>Opens and closes job runs. Resolved from DI; null object in tests.</summary>
public interface IJobLog
{
    JobSession? Current { get; }

    JobSession Begin(JobStart start);

    void Skipped(JobStart start, string reason);

    Task RecoverInterruptedAsync(CancellationToken ct);
}

/// <summary>Fans one run out to every configured sink.</summary>
public sealed class JobLog(IReadOnlyList<IJobSink> sinks) : IJobLog
{
    public JobSession? Current => JobContext.Current;

    public JobSession Begin(JobStart start)
    {
        var sessions = new List<IJobSinkSession>(sinks.Count);
        foreach (var sink in sinks)
        {
            sessions.Add(sink.Begin(start));
        }

        return new JobSession(start, [.. sessions]);
    }

    public void Skipped(JobStart start, string reason)
    {
        foreach (var sink in sinks)
        {
            sink.Skipped(start, reason);
        }
    }

    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        foreach (var sink in sinks)
        {
            await sink.RecoverInterruptedAsync(ct);
        }
    }
}

/// <summary>Discards everything; used by tests and when the database sink is absent.</summary>
public sealed class NullJobLog : IJobLog
{
    public static readonly NullJobLog Instance = new();

    private NullJobLog()
    {
    }

    public JobSession? Current => JobContext.Current;

    public JobSession Begin(JobStart start) => new(start, []);

    public void Skipped(JobStart start, string reason)
    {
    }

    public Task RecoverInterruptedAsync(CancellationToken ct) => Task.CompletedTask;
}
