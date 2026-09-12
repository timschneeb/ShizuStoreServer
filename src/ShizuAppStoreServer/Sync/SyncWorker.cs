using ShizuAppStoreServer.Core.Sync;

namespace ShizuAppStoreServer.Sync;

/// <summary>
/// Fast loop (PLAN §7): every <c>Sync:FastLoopMinutes</c> (15) drain
/// <c>sync_requests</c>, re-parse the list when HEAD moved, upsert, and
/// enrich due apps. Optionally runs one pass at startup (first boot =
/// initial backfill).
/// </summary>
public sealed class SyncWorker(SyncPassRunner runner, SyncOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.RunOnStartup)
        {
            await runner.RunOnceAsync("scheduled", fullRecheck: false, stoppingToken);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.FastLoopMinutes)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await runner.RunOnceAsync("scheduled", fullRecheck: false, stoppingToken);
        }
    }
}
