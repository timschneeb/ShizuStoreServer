using ShizuAppStoreServer.Core.Sync;

namespace ShizuAppStoreServer.Sync;

/// <summary>
/// Fast loop: every <c>Sync:FastLoopMinutes</c> (15) drain
/// <c>sync_requests</c>, re-parse the list when HEAD moved, upsert, and
/// enrich due apps. Optionally runs one pass at startup (first boot =
/// initial backfill). A webhook request wakes the loop immediately
/// (<see cref="SyncSignal"/>); a request that lands while a pass is running
/// turns into an immediate follow-up pass instead of waiting for the tick.
/// </summary>
public sealed class SyncWorker(
    ISyncPassRunner runner, SyncSignal signal, SyncOptions options, ILogger<SyncWorker> logger)
    : BackgroundService
{
    /// <summary>Retry delay when the gate is held by the nightly pass.</summary>
    internal static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.RunOnStartup)
        {
            await RunWithRetryAsync(stoppingToken);
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.FastLoopMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            var signaled = await signal.WaitAsync(interval, stoppingToken);
            if (signaled)
            {
                // Consume the request that woke us; anything arriving later
                // stays pending and becomes a follow-up run below.
                signal.ConsumePending();
                logger.LogInformation("Sync worker woken by a webhook request.");
            }

            await RunWithRetryAsync(stoppingToken);

            while (signal.ConsumePending() && !stoppingToken.IsCancellationRequested)
            {
                await RunWithRetryAsync(stoppingToken);
            }
        }
    }

    private async Task RunWithRetryAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && !await runner.RunOnceAsync("scheduled", false, stoppingToken))
        {
            try
            {
                await Task.Delay(BusyRetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
