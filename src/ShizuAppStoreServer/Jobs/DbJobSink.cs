using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Jobs;

namespace ShizuAppStoreServer.Jobs;

/// <summary>
/// Database sink for job runs and their event streams. The run row is written
/// synchronously at open so events can never reference a missing row; events
/// flow through a bounded channel and a background flush so a chatty pass
/// does not pay one round trip per event. A failure here degrades
/// observability only: it never fails the job it is recording.
/// </summary>
public sealed class DbJobSink(IServiceScopeFactory scopes, JobLogOptions options, ILogger<DbJobSink> log) : IJobSink
{
    private const int MaxBatchSize = 1_000;

    private readonly JobLogOptions _options = options;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Wait mode (not DropWrite) so TryWrite stays non-blocking AND reports
    // false when full: that is what makes the dropped counter accurate.
    private readonly Channel<FlushItem> _channel = Channel.CreateBounded<FlushItem>(
        new BoundedChannelOptions(Math.Max(1, options.ChannelCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    public IJobSinkSession Begin(JobStart start)
    {
        long? runId = null;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            var run = new JobRun
            {
                Kind = start.Kind,
                Trigger = start.Trigger,
                Status = JobStatus.Running,
                StartedAt = start.StartedAt,
                StartedDay = DateOnly.FromDateTime(start.StartedAt.UtcDateTime),
                Reference = start.Reference,
                Metadata = SerializeMetadata(start.Metadata),
                UsageAnalysisRunId = start.UsageAnalysisRunId,
                ItemsTotal = start.ItemCount ?? 0,
            };
            db.JobRuns.Add(run);
            db.SaveChanges();
            runId = run.Id;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not open a job run row; this run will not be persisted.");
        }

        return new Session(this, start, runId);
    }

    public void Skipped(JobStart start, string reason)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            var finishedAt = DateTimeOffset.UtcNow;
            db.JobRuns.Add(new JobRun
            {
                Kind = start.Kind,
                Trigger = start.Trigger,
                Status = JobStatus.Skipped,
                StartedAt = start.StartedAt,
                StartedDay = DateOnly.FromDateTime(start.StartedAt.UtcDateTime),
                FinishedAt = finishedAt,
                DurationMs = (int)(finishedAt - start.StartedAt).TotalMilliseconds,
                Reference = start.Reference,
                Metadata = SerializeMetadata(start.Metadata),
                UsageAnalysisRunId = start.UsageAnalysisRunId,
                ItemsTotal = start.ItemCount ?? 0,
                Summary = reason,
            });
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not record a skipped job run; continuing.");
        }
    }

    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            var now = DateTimeOffset.UtcNow;
            var affected = await db.JobRuns
                .Where(r => r.Status == JobStatus.Running)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(r => r.Status, JobStatus.Interrupted)
                        .SetProperty(r => r.FinishedAt, now)
                        .SetProperty(r => r.Summary, "interrupted by server restart"),
                    ct);
            if (affected > 0)
            {
                log.LogWarning("Marked {Count} interrupted job runs at startup.", affected);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not mark interrupted job runs at startup.");
        }
    }

    /// <summary>Flush loop; driven by <see cref="JobLogWorker"/>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await _channel.Reader.WaitToReadAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!more)
                {
                    break;
                }

                // Let a burst land so one pass writes in a handful of batches.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.FlushIntervalSeconds)), ct);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down: fall through and write what is queued.
                }

                await FlushQueuedAsync();
            }
        }
        finally
        {
            await FlushQueuedAsync();
        }
    }

    private async Task FlushQueuedAsync()
    {
        while (true)
        {
            var batch = new List<FlushItem>(MaxBatchSize);
            while (batch.Count < MaxBatchSize && _channel.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count == 0)
            {
                return;
            }

            await ProcessBatchAsync(batch);
        }
    }

    private async Task ProcessBatchAsync(IReadOnlyList<FlushItem> batch)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            var pending = 0;
            foreach (var item in batch)
            {
                switch (item)
                {
                    case EventItem e:
                        db.JobEvents.Add(e.Event);
                        pending++;
                        break;
                    case RunUpdateItem u:
                        // Events queued before the finish marker must be durable
                        // before the run row closes, so the detail page never
                        // shows a finished run with a truncated stream.
                        if (pending > 0)
                        {
                            await db.SaveChangesAsync(CancellationToken.None);
                            pending = 0;
                        }

                        var run = await db.JobRuns.FirstOrDefaultAsync(r => r.Id == u.RunId, CancellationToken.None);
                        if (run is not null)
                        {
                            u.Update.Apply(run, MergeMetadata(run.Metadata, u.Update.Metadata));
                            await db.SaveChangesAsync(CancellationToken.None);
                        }

                        u.Done.TrySetResult(true);
                        break;
                }
            }

            if (pending > 0)
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Job log flush failed; {Count} buffered items were dropped.", batch.Count);
            foreach (var item in batch.OfType<RunUpdateItem>())
            {
                item.Done.TrySetResult(false);
            }
        }
    }

    /// <summary>
    /// Last-resort row write when the channel is full or the worker is stalled.
    /// Idempotent with the worker's own apply, so a late duplicate is harmless.
    /// </summary>
    private async Task ApplyUpdateAsync(long runId, JobRunUpdate update)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            var run = await db.JobRuns.FirstOrDefaultAsync(r => r.Id == runId, CancellationToken.None);
            if (run is not null)
            {
                update.Apply(run, MergeMetadata(run.Metadata, update.Metadata));
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not close job run {RunId}; the flush worker may recover it later.", runId);
        }
    }

    private static string SerializeMetadata(object? value) =>
        value is null ? "{}" : JsonSerializer.Serialize(value, Json);

    private static string MergeMetadata(string? baseJson, object? overrides)
    {
        var merged = JsonNode.Parse(string.IsNullOrWhiteSpace(baseJson) ? "{}" : baseJson) as JsonObject ?? [];
        if (overrides is not null
            && JsonSerializer.SerializeToNode(overrides, Json) is JsonObject extra)
        {
            foreach (var (key, value) in extra)
            {
                merged[key] = value?.DeepClone();
            }
        }

        return merged.ToJsonString();
    }

    private abstract record FlushItem;

    private sealed record EventItem(JobEvent Event) : FlushItem;

    private sealed record RunUpdateItem(long RunId, JobRunUpdate Update, TaskCompletionSource<bool> Done) : FlushItem;

    private sealed record JobRunUpdate(
        JobStatus Status,
        DateTimeOffset FinishedAt,
        long DurationMs,
        int ItemsTotal,
        int ItemsOk,
        int ItemsSkipped,
        int ItemsFailed,
        string? Summary,
        string? Error,
        string? Reference,
        object? Metadata,
        int EventsCount,
        int EventsDropped)
    {
        public void Apply(JobRun run, string metadata)
        {
            run.Status = Status;
            run.FinishedAt = FinishedAt;
            run.DurationMs = (int)DurationMs;
            run.ItemsTotal = ItemsTotal;
            run.ItemsOk = ItemsOk;
            run.ItemsSkipped = ItemsSkipped;
            run.ItemsFailed = ItemsFailed;
            run.Summary = Summary;
            run.Error = Error;
            run.Reference = Reference ?? run.Reference;
            run.Metadata = metadata;
            run.EventsCount = EventsCount;
            run.EventsDropped = EventsDropped;
        }
    }

    private sealed class Session(DbJobSink sink, JobStart start, long? runId) : IJobSinkSession
    {
        private int _written;
        private int _dropped;
        private int _finished;

        public long? RunId { get; } = runId;

        public void SetItemCount(int count)
        {
            // The run row carries the count from JobStart; kept on the sink
            // session for interface parity with sinks that show progress.
        }

        public void Write(JobEventRecord record)
        {
            if (RunId is not { } jobRunId || record.Level < sink._options.MinLevel)
            {
                return;
            }

            var evt = new JobEvent
            {
                JobRunId = jobRunId,
                Seq = record.Seq,
                At = DateTimeOffset.UtcNow,
                Level = record.Level,
                Type = record.Type,
                Phase = record.Phase,
                AppId = record.AppId,
                Slug = record.Slug,
                Message = record.Message,
                Data = record.Data is null ? "{}" : JsonSerializer.Serialize(record.Data, Json),
                DurationMs = record.DurationMs,
            };

            if (sink._channel.Writer.TryWrite(new EventItem(evt)))
            {
                Interlocked.Increment(ref _written);
            }
            else
            {
                Interlocked.Increment(ref _dropped);
            }
        }

        public async Task FinishAsync(JobFinish finish, CancellationToken ct = default)
        {
            if (RunId is not { } jobRunId || Interlocked.Exchange(ref _finished, 1) == 1)
            {
                return;
            }

            var finishedAt = DateTimeOffset.UtcNow;
            var update = new JobRunUpdate(
                finish.Status,
                finishedAt,
                (long)(finishedAt - start.StartedAt).TotalMilliseconds,
                finish.ItemsTotal,
                finish.ItemsOk,
                finish.ItemsSkipped,
                finish.ItemsFailed,
                finish.Summary,
                finish.Error,
                finish.Reference,
                finish.Metadata,
                Volatile.Read(ref _written),
                Volatile.Read(ref _dropped));

            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (sink._channel.Writer.TryWrite(new RunUpdateItem(jobRunId, update, done)))
            {
                var winner = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                if (winner == done.Task && await done.Task)
                {
                    return;
                }
            }

            await sink.ApplyUpdateAsync(jobRunId, update);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
