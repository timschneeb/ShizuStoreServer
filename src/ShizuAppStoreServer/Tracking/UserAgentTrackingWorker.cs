using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Tracking;

/// <summary>
/// Drains <see cref="UserAgentTracker"/> into the stats tables every
/// <see cref="UserAgentTrackingOptions.FlushInterval"/>. A flush failure is
/// logged and the batch is dropped: usage stats must never take the host
/// down, and the next interval starts clean. Shutdown drains what is buffered.
/// </summary>
public sealed class UserAgentTrackingWorker(
    UserAgentTracker tracker,
    IServiceScopeFactory scopeFactory,
    UserAgentTrackingOptions options,
    ILogger<UserAgentTrackingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        var interval = options.FlushInterval > TimeSpan.Zero
            ? options.FlushInterval
            : TimeSpan.FromSeconds(10);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, stoppingToken);
                await FlushAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await FlushAsync(CancellationToken.None);
    }

    /// <summary>
    /// Drains the buffer and writes the batch. Called on every interval, on
    /// shutdown, and directly by tests that need a deterministic flush.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var hits = new List<UserAgentHit>();
        while (tracker.Reader.TryRead(out var hit))
        {
            hits.Add(hit);
        }

        var dropped = tracker.ConsumeDropped();
        if (hits.Count == 0 && dropped == 0)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
            await UserAgentStatsRecorder.FlushAsync(db, hits, ct);
            if (dropped > 0)
            {
                logger.LogWarning("User-agent tracking dropped {Dropped} hits: buffer full.", dropped);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "User-agent tracking flush failed; {Count} hits dropped.", hits.Count);
        }
    }
}
