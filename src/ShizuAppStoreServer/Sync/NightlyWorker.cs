using ShizuAppStoreServer.Core.Sync;

namespace ShizuAppStoreServer.Sync;

/// <summary>
/// Nightly full re-check: every app (except excluded) is
/// re-resolved with <c>force</c>, release feeds answer 304 when nothing
/// changed, version bumps append to <c>app_versions</c>.
/// </summary>
public sealed class NightlyWorker(
    ISyncPassRunner runner, SyncOptions options, ILogger<NightlyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!TimeOnly.TryParse(options.NightlyTimeUtc, out var nightly))
        {
            logger.LogError(
                "Invalid Sync:NightlyTimeUtc '{Value}'; nightly re-check disabled.", options.NightlyTimeUtc);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeUntilNextNightly(DateTimeOffset.UtcNow, nightly), stoppingToken);
            await runner.RunOnceAsync("nightly", fullRecheck: true, stoppingToken);
        }
    }

    /// <summary>Delay until the next <paramref name="nightly"/> o'clock UTC, strictly in the future. Public for tests.</summary>
    public static TimeSpan TimeUntilNextNightly(DateTimeOffset nowUtc, TimeOnly nightly)
    {
        var candidate = DateOnly.FromDateTime(nowUtc.UtcDateTime).ToDateTime(nightly, DateTimeKind.Utc);
        if (candidate <= nowUtc.UtcDateTime)
        {
            candidate = candidate.AddDays(1);
        }

        return candidate - nowUtc.UtcDateTime;
    }
}
