using ShizuAppStoreServer.Core.Jobs;

namespace ShizuAppStoreServer.Jobs;

/// <summary>
/// Drains the job log channel and reconciles runs left open by a previous
/// process. A worker crash must never take the API down with it: the host is
/// configured with <c>BackgroundServiceExceptionBehavior.Ignore</c>.
/// </summary>
public sealed class JobLogWorker(DbJobSink sink, IJobLog jobLog, ILogger<JobLogWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await jobLog.RecoverInterruptedAsync(stoppingToken);
            await sink.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Job log worker failed; job events stop persisting.");
        }
    }
}
