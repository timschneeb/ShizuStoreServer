using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Sync;

/// <summary>
/// Drains the AI usage-analysis queue. One lane per configured parallelism;
/// each claim happens in its own DI scope so a retry never inherits a stale
/// DbContext. Idles (and logs once) while the analyzer is disabled.
/// </summary>
public sealed class UsageAnalysisWorker(
    IServiceScopeFactory scopes,
    UsageAnalysisOptions options,
    ILogger<UsageAnalysisWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            .ToArray();
        await Task.WhenAll(lanes);
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
