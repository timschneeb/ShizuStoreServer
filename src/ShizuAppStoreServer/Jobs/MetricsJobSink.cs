using System.Text.Json;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Jobs;

namespace ShizuAppStoreServer.Jobs;

/// <summary>
/// Metrics sink for completed job runs. A failure here degrades observability
/// only: it never fails the job it is recording.
/// </summary>
public sealed class MetricsJobSink(ShizuMetrics metrics, ILogger<MetricsJobSink> log) : IJobSink
{
    public IJobSinkSession Begin(JobStart start) => new Session(metrics, log, start);

    public void Skipped(JobStart start, string reason) =>
        Record(metrics, log, start, JobStatus.Skipped, DateTimeOffset.UtcNow - start.StartedAt);

    public Task RecoverInterruptedAsync(CancellationToken ct) => Task.CompletedTask;

    private static void Record(
        ShizuMetrics metrics, ILogger log, JobStart start, JobStatus status, TimeSpan duration)
    {
        try
        {
            metrics.JobFinished(
                Snake(start.Kind),
                Snake(start.Trigger),
                Snake(status),
                duration.TotalSeconds);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not record job metrics for {Kind}.", start.Kind);
        }
    }

    // Prometheus label values read better in snake_case; the API wire format
    // uses the same convention.
    private static string Snake(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private sealed class Session(ShizuMetrics metrics, ILogger log, JobStart start) : IJobSinkSession
    {
        private int _finished;

        public long? RunId => null;

        public void SetItemCount(int count)
        {
        }

        public void Write(JobEventRecord record)
        {
        }

        public Task FinishAsync(JobFinish finish, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0)
            {
                Record(metrics, log, start, finish.Status, DateTimeOffset.UtcNow - start.StartedAt);
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
