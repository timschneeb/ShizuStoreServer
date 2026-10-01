using System.Globalization;
using System.Text.Json;
using Sentry;
using Sentry.Extensibility;
using ShizuAppStoreServer.Core.Jobs;

namespace ShizuAppStoreServer.Jobs;

/// <summary>
/// Sentry sink for job runs: the lifecycle (start, skip, finish) and failure
/// events become breadcrumbs, error events become Sentry issues. The full
/// event stream stays in the DB job log, so per-app chatter is not forwarded.
/// A failure here degrades reporting only; it never throws into a job.
/// </summary>
public sealed class SentryJobSink(ILogger<SentryJobSink> log) : IJobSink
{
    public IJobSinkSession Begin(JobStart start)
    {
        Guard(log, () => Breadcrumb(start, "started"));
        return new Session(log, start);
    }

    public void Skipped(JobStart start, string reason) =>
        Guard(log, () => Breadcrumb(
            start, $"skipped: {reason}", JobStatus.Skipped, BreadcrumbLevel.Warning));

    public Task RecoverInterruptedAsync(CancellationToken ct) => Task.CompletedTask;

    private static void Breadcrumb(
        JobStart start, string message, JobStatus? status = null, BreadcrumbLevel level = BreadcrumbLevel.Info)
    {
        var data = new Dictionary<string, string>
        {
            ["kind"] = SentryJobTags.Value(start.Kind),
            ["trigger"] = SentryJobTags.Value(start.Trigger),
        };
        if (status is { } value)
        {
            data["status"] = SentryJobTags.Value(value);
        }

        SentrySdk.AddBreadcrumb(
            $"job {SentryJobTags.Value(start.Kind)} {message}", "job", data: data, level: level);
    }

    private static void Guard(ILogger log, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Sentry job telemetry failed.");
        }
    }

    private sealed class Session(ILogger log, JobStart start) : IJobSinkSession
    {
        private int _finished;
        private int _errorCaptured;

        public long? RunId => null;

        public void SetItemCount(int count)
        {
        }

        public void Write(JobEventRecord record)
        {
            // Info and below are routine per-app chatter; the DB job log has
            // the full stream and Sentry only needs the failures.
            if (record.Level != JobEventLevel.Error)
            {
                return;
            }

            Interlocked.Exchange(ref _errorCaptured, 1);
            Guard(log, () => CaptureError(
                $"job {SentryJobTags.Value(start.Kind)} error: {record.Message}",
                start, record.Slug, record.Phase));
        }

        public Task FinishAsync(JobFinish finish, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0)
            {
                return Task.CompletedTask;
            }

            Guard(log, () =>
            {
                var status = SentryJobTags.Value(finish.Status);
                Breadcrumb(
                    start, status, finish.Status,
                    finish.Status == JobStatus.Failed ? BreadcrumbLevel.Error : BreadcrumbLevel.Info);

                // Failed and interrupted runs need an issue even when no
                // individual job event carried the error, for example a run
                // disposed mid-flight by a host shutdown.
                if (finish.Status is JobStatus.Failed or JobStatus.Interrupted
                    && Volatile.Read(ref _errorCaptured) == 0)
                {
                    var detail = finish.Error ?? finish.Summary ?? "(no detail)";
                    CaptureError(
                        $"job {SentryJobTags.Value(start.Kind)} {status}: {detail}", start, null, null);
                }
            });

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static void CaptureError(string message, JobStart start, string? slug, string? phase)
        {
            var evt = new SentryEvent
            {
                Message = message,
                Level = SentryLevel.Error,
                // One issue per job kind instead of one per message variant.
                Fingerprint = ["job", SentryJobTags.Value(start.Kind)],
            };
            if (slug is not null)
            {
                evt.SetTag("app.slug", slug);
            }

            if (phase is not null)
            {
                evt.SetTag("job.phase", phase);
            }

            SentrySdk.CaptureEvent(evt);
        }
    }
}

/// <summary>
/// Tags every event raised while a job is active with the ambient job
/// identity (kind, trigger, run id) so background failures group and
/// filter by job.
/// </summary>
public sealed class JobContextEventProcessor : ISentryEventProcessor
{
    public SentryEvent Process(SentryEvent @event)
    {
        if (JobContext.Current is { } job)
        {
            @event.SetTag("job.kind", SentryJobTags.Value(job.Start.Kind));
            @event.SetTag("job.trigger", SentryJobTags.Value(job.Start.Trigger));
            if (job.RunId is { } runId)
            {
                @event.SetTag("job.run_id", runId.ToString(CultureInfo.InvariantCulture));
            }
        }

        return @event;
    }
}

internal static class SentryJobTags
{
    public static string Value(Enum value) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
