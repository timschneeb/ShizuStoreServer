using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Sync;

/// <summary>
/// Drains the AI usage-analysis queue. One lane per configured parallelism;
/// each claim happens in its own DI scope so a retry never inherits a stale
/// DbContext. Idles (and logs once) while the analyzer is disabled. Stored
/// transcripts past the retention window are pruned at startup and on a timer.
/// </summary>
public sealed class UsageAnalysisWorker(
    IServiceScopeFactory scopes,
    UsageAnalysisOptions options,
    ILogger<UsageAnalysisWorker> log) : BackgroundService
{
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prune even when the analyzer is off: the transcripts are only useful
        // for a short window and nothing else deletes them.
        TryPruneLogs();

        if (!options.IsConfigured)
        {
            log.LogInformation("Usage analysis is disabled (set UsageAnalysis:Enabled, BaseUrl and Model).");
            return;
        }

        log.LogInformation("Usage analysis worker starting with {Lanes} lane(s), model {Model}.",
            Math.Max(1, options.MaxParallelism), options.Model);
        using (var scope = scopes.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<IUsageAnalysisRunner>();
            await runner.RecoverInterruptedAsync(stoppingToken);
        }

        var lanes = Enumerable.Range(0, Math.Max(1, options.MaxParallelism))
            .Select(_ => RunLaneAsync(stoppingToken))
            .ToList();
        lanes.Add(PruneLoopAsync(stoppingToken));
        await Task.WhenAll(lanes);
    }

    private bool TryGetPruneTarget(out string path, out TimeSpan retention)
    {
        path = options.LogPath ?? string.Empty;
        retention = options.LogRetention ?? TimeSpan.Zero;
        return path.Length > 0 && retention > TimeSpan.Zero;
    }

    private void TryPruneLogs()
    {
        if (!TryGetPruneTarget(out var path, out var retention))
        {
            return;
        }

        try
        {
            var deleted = UsageAnalysisLogWriter.PruneDirectory(path, retention, log);
            if (deleted > 0)
            {
                log.LogInformation("Deleted {Count} usage log(s) older than {Retention} from {Path}.",
                    deleted, retention, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not prune usage logs in {Path}.", path);
        }
    }

    private async Task PruneLoopAsync(CancellationToken ct)
    {
        if (!TryGetPruneTarget(out _, out _))
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PruneInterval, ct);
                TryPruneLogs();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Usage log pruning failed; retrying on the next sweep.");
            }
        }
    }

    private async Task RunLaneAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<IUsageAnalysisRunner>();
                if (!await runner.RunNextAsync(ct))
                {
                    await Task.Delay(options.PollInterval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Usage analysis lane failed; retrying shortly.");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
