using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Jobs;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>
/// Recording <see cref="IJobSink"/> for hermetic tests. It persists real
/// <c>job_runs</c> rows (the <c>sync_issues</c> foreign key needs one) into
/// the test context and captures every session, event, skip and finish so
/// tests can assert on what a job logged. All writes are serialized because
/// enrichment pushes app events from worker tasks.
/// </summary>
internal sealed class RecordingJobSink(ShizuDbContext db) : IJobSink
{
    private readonly ShizuDbContext _db = db;
    private readonly object _gate = new();

    public IJobLog Log => new JobLog([this]);

    public List<JobStart> Begins { get; } = [];
    public List<(JobStart Start, string Reason, long RunId)> Skips { get; } = [];
    public List<JobEventRecord> Events { get; } = [];
    public List<(JobFinish Finish, long RunId)> Finishes { get; } = [];

    public IJobSinkSession Begin(JobStart start)
    {
        lock (_gate)
        {
            var run = Insert(start, JobStatus.Running);
            _db.SaveChanges();
            Begins.Add(start);
            return new Session(this, run.Id);
        }
    }

    public void Skipped(JobStart start, string reason)
    {
        lock (_gate)
        {
            var run = Insert(start, JobStatus.Skipped);
            run.FinishedAt = start.StartedAt;
            run.Summary = reason;
            _db.SaveChanges();
            Skips.Add((start, reason, run.Id));
        }
    }

    public Task RecoverInterruptedAsync(CancellationToken ct = default) =>
        Task.CompletedTask;

    private JobRun Insert(JobStart start, JobStatus status)
    {
        var run = new JobRun
        {
            Kind = start.Kind,
            Trigger = start.Trigger,
            Status = status,
            StartedAt = start.StartedAt,
            StartedDay = DateOnly.FromDateTime(start.StartedAt.UtcDateTime),
            Reference = start.Reference,
            ItemsTotal = start.ItemCount ?? 0,
            UsageAnalysisRunId = start.UsageAnalysisRunId,
        };
        _db.JobRuns.Add(run);
        return run;
    }

    private sealed class Session(RecordingJobSink sink, long runId) : IJobSinkSession
    {
        private int _finished;

        public long? RunId { get; } = runId;

        public void SetItemCount(int count)
        {
            lock (sink._gate)
            {
                var run = sink._db.JobRuns.Find(runId);
                if (run is not null)
                {
                    run.ItemsTotal = count;
                }
            }
        }

        public void Write(JobEventRecord record)
        {
            lock (sink._gate)
            {
                sink.Events.Add(record);
            }
        }

        public Task FinishAsync(JobFinish finish, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _finished, 1) == 1)
            {
                return Task.CompletedTask;
            }

            lock (sink._gate)
            {
                var run = sink._db.JobRuns.Find(runId);
                if (run is not null)
                {
                    run.Status = finish.Status;
                    run.FinishedAt = DateTimeOffset.UtcNow;
                    run.ItemsTotal = finish.ItemsTotal;
                    run.ItemsOk = finish.ItemsOk;
                    run.ItemsSkipped = finish.ItemsSkipped;
                    run.ItemsFailed = finish.ItemsFailed;
                    run.Summary = finish.Summary;
                    run.Error = finish.Error;
                    run.Reference = finish.Reference ?? run.Reference;
                    sink._db.SaveChanges();
                }

                sink.Finishes.Add((finish, runId));
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
