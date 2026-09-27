using ShizuAppStoreServer.Core.Enrichment;

namespace ShizuAppStoreServer.Core.Jobs;

/// <summary>
/// Ambient run identity. Jobs activate one session per async flow, so low level
/// code (enrichers, renderers, pollers) can log without threading a parameter
/// through every call. Tasks spawned inside the flow inherit it.
/// </summary>
public static class JobContext
{
    private static readonly AsyncLocal<JobSession?> Active = new();

    public static JobSession? Current => Active.Value;

    internal static IDisposable Push(JobSession session)
    {
        var prior = Active.Value;
        Active.Value = session;
        return new Pop(prior);
    }

    private sealed class Pop(JobSession? prior) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Active.Value = prior;
            }
        }
    }
}

/// <summary>
/// One run in progress: assigns event sequence numbers, fans events out to the
/// sinks and restores the ambient context when it closes. Finish exactly once;
/// disposing an unfinished session records it as interrupted.
/// </summary>
public sealed class JobSession : IAsyncDisposable
{
    private readonly IJobSinkSession[] _sinks;
    private IDisposable? _activation;
    private int _seq;
    private int _finished;

    internal JobSession(JobStart start, IJobSinkSession[] sinks)
    {
        Start = start;
        _sinks = sinks;
        foreach (var sink in sinks)
        {
            if (sink.RunId is { } id)
            {
                RunId = id;
                break;
            }
        }

        _activation = JobContext.Push(this);
    }

    public JobStart Start { get; }

    /// <summary>First persisted run id across sinks; null when no sink could persist.</summary>
    public long? RunId { get; }

    /// <summary>The job selected its work items; file logs show progress against it.</summary>
    public void SetItemCount(int count)
    {
        foreach (var sink in _sinks)
        {
            sink.SetItemCount(count);
        }
    }

    public void Event(
        JobEventLevel level,
        JobEventType type,
        string message,
        string? phase = null,
        long? appId = null,
        string? slug = null,
        object? data = null,
        int? durationMs = null)
    {
        if (Volatile.Read(ref _finished) != 0)
        {
            return;
        }

        var record = new JobEventRecord(
            Interlocked.Increment(ref _seq), level, type, message, phase, appId, slug, data, durationMs);
        foreach (var sink in _sinks)
        {
            sink.Write(record);
        }
    }

    public async Task FinishAsync(JobFinish finish, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        try
        {
            foreach (var sink in _sinks)
            {
                await sink.FinishAsync(finish, ct);
            }
        }
        finally
        {
            _activation?.Dispose();
            _activation = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _finished) == 0)
        {
            await FinishAsync(
                new JobFinish(JobStatus.Interrupted, Summary: "job did not complete"),
                CancellationToken.None);
        }

        _activation?.Dispose();
        _activation = null;
    }
}

/// <summary>
/// Null-safe logging helpers. Ambient sessions are often absent (tests, CLI
/// paths without a sink), so every helper tolerates a null receiver.
/// </summary>
public static class JobSessionExtensions
{
    /// <summary>Long running action progress or outcome.</summary>
    public static void Detail(
        this JobSession? session,
        string message,
        JobEventType type = JobEventType.Decision,
        object? data = null,
        string? phase = null,
        long? appId = null,
        string? slug = null)
    {
        session?.Event(JobEventLevel.Debug, type, message, phase, appId, slug, data);
    }

    /// <summary>A choice the job made (skip, fallback, exclusion, primary pick).</summary>
    public static void Decision(
        this JobSession? session,
        string message,
        object? data = null,
        JobEventLevel level = JobEventLevel.Debug,
        string? phase = null,
        long? appId = null,
        string? slug = null)
    {
        session?.Event(level, JobEventType.Decision, message, phase, appId, slug, data);
    }

    /// <summary>Start or end of a named phase.</summary>
    public static void Phase(this JobSession? session, string phase, string message, object? data = null)
    {
        session?.Event(JobEventLevel.Info, JobEventType.Phase, message, phase, data: data);
    }

    /// <summary>One scanned app's outcome, streamed as it finishes.</summary>
    public static void App(this JobSession? session, string slug, string? displayName, EnrichResult result, long? appId = null)
    {
        if (session is null)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(displayName) || displayName == slug
            ? slug
            : $"{slug} ({displayName})";
        var status = ResultStatus(result.Outcome);
        var note = result.Outcome == EnrichOutcome.Failed ? result.Error : result.Detail;
        var message = string.IsNullOrEmpty(note) ? $"{name}  {status}" : $"{name}  {status}  {note}";
        var level = result.Outcome switch
        {
            EnrichOutcome.Failed => JobEventLevel.Warning,
            EnrichOutcome.Enriched => JobEventLevel.Info,
            _ => JobEventLevel.Debug,
        };

        session.Event(level, JobEventType.App, message, appId: appId, slug: slug, data: new
        {
            slug,
            displayName,
            outcome = result.Outcome.ToString(),
            error = result.Error,
            detail = result.Detail,
        });
    }

    public static void Download(this JobSession? session, string message, object? data = null, JobEventLevel level = JobEventLevel.Debug)
    {
        session?.Event(level, JobEventType.Download, message, data: data);
    }

    public static void Analyze(this JobSession? session, string message, object? data = null, JobEventLevel level = JobEventLevel.Debug)
    {
        session?.Event(level, JobEventType.Analyze, message, data: data);
    }

    public static void Release(this JobSession? session, string message, object? data = null, JobEventLevel level = JobEventLevel.Debug)
    {
        session?.Event(level, JobEventType.Release, message, data: data);
    }

    public static void Render(this JobSession? session, string message, object? data = null, JobEventLevel level = JobEventLevel.Debug)
    {
        session?.Event(level, JobEventType.Render, message, data: data);
    }

    public static void Poll(this JobSession? session, string message, object? data = null, JobEventLevel level = JobEventLevel.Debug)
    {
        session?.Event(level, JobEventType.Poll, message, data: data);
    }

    private static string ResultStatus(EnrichOutcome outcome) => outcome switch
    {
        EnrichOutcome.Enriched => "OK",
        EnrichOutcome.AvatarFallback => "ok",
        EnrichOutcome.Failed => "FAIL",
        _ => "skip", // UpToDate, SkippedFresh
    };
}
