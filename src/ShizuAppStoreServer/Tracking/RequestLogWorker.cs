using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Tracking;

/// <summary>
/// Drains <see cref="RequestLogTracker"/> into <c>request_logs</c> every
/// <see cref="RequestLogOptions.FlushInterval"/>. A flush failure is logged
/// and the batch is dropped: the log must never take the host down, and the
/// next interval starts clean. Shutdown drains what is buffered.
/// </summary>
public sealed class RequestLogWorker(
    RequestLogTracker tracker,
    IServiceScopeFactory scopeFactory,
    RequestLogOptions options,
    ILogger<RequestLogWorker> logger) : BackgroundService
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
        var hits = new List<RequestLogHit>();
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
            await RequestLogRecorder.FlushAsync(db, hits, ct);
            if (dropped > 0)
            {
                logger.LogWarning("Request log dropped {Dropped} entries: buffer full.", dropped);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Request log flush failed; {Count} entries dropped.", hits.Count);
        }
    }
}
