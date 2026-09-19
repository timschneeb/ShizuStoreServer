using ShizuAppStoreServer.Core.Sync;

namespace ShizuAppStoreServer.Sync;

/// <summary>Snapshot of the operator-triggered screenshots refresh.</summary>
public sealed record ScreenshotRefreshStatus(
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int Checked,
    int Updated,
    int Current,
    int Failed,
    IReadOnlyList<string> Errors,
    string? Error)
{
    public static readonly ScreenshotRefreshStatus Idle =
        new("idle", null, null, 0, 0, 0, 0, [], null);
}

/// <summary>
/// Runs <see cref="SyncService.RefreshScreenshotsAsync"/> inside the live
/// server. Takes the shared <see cref="SyncGate"/> so it excludes the fast and
/// nightly passes (the API keeps serving reads) and reuses the same repo
/// fallback machinery as normal enrichment, with the per-app recheck window
/// bypassed. One run at a time.
/// </summary>
public sealed class ScreenshotRefreshCoordinator(
    IServiceScopeFactory scopes,
    SyncGate gate,
    IHostApplicationLifetime lifetime,
    ILogger<ScreenshotRefreshCoordinator> logger)
{
    private const string Idle = "idle";
    private const string Running = "running";
    private const string Completed = "completed";
    private const string Failed = "failed";

    private readonly object _lock = new();
    private ScreenshotRefreshStatus _status = ScreenshotRefreshStatus.Idle;
    private CancellationTokenSource? _runCts;

    public ScreenshotRefreshStatus Snapshot()
    {
        lock (_lock)
        {
            return _status;
        }
    }

    /// <summary>
    /// Starts a pass unless one is running or a sync pass holds the gate.
    /// <paramref name="reason"/> explains a refusal.
    /// </summary>
    public (bool Started, string? Reason, ScreenshotRefreshStatus Status) TryStart()
    {
        lock (_lock)
        {
            if (_status.State == Running)
            {
                return (false, "A screenshots refresh is already running.", _status);
            }

            // Non-blocking: never queue behind (or starve) a sync pass.
            if (!gate.Wait(0))
            {
                return (false, "A sync pass is running; retry when it finishes.", _status);
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            _runCts = cts;
            _status = new ScreenshotRefreshStatus(Running, DateTimeOffset.UtcNow, null, 0, 0, 0, 0, [], null);
            var started = _status;
            _ = Task.Run(() => RunAsync(started.StartedAt!.Value, cts));
            return (true, null, started);
        }
    }

    /// <summary>Cancels the running pass; false when nothing is running.</summary>
    public bool TryCancel()
    {
        lock (_lock)
        {
            if (_status.State != Running || _runCts is null)
            {
                return false;
            }

            _runCts.Cancel();
            return true;
        }
    }

    private async Task RunAsync(DateTimeOffset startedAt, CancellationTokenSource cts)
    {
        ScreenshotRefreshResult? result = null;
        string? error = null;
        try
        {
            using var scope = scopes.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<SyncService>();
            result = await service.RefreshScreenshotsAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            error = "Screenshots refresh was cancelled.";
        }
        catch (Exception ex)
        {
            error = $"Screenshots refresh crashed: {ex.Message}";
            logger.LogError(ex, "Screenshots refresh crashed.");
        }
        finally
        {
            gate.Release();
            lock (_lock)
            {
                _runCts = null;
            }

            cts.Dispose();
        }

        var finished = result is null
            ? new ScreenshotRefreshStatus(Failed, startedAt, DateTimeOffset.UtcNow, 0, 0, 0, 0, [], error)
            : new ScreenshotRefreshStatus(
                Completed, startedAt, DateTimeOffset.UtcNow,
                result.Checked, result.Updated, result.Current, result.Failed,
                result.Errors,
                result.Failed > 0 ? "Some apps failed; see errors." : null);
        lock (_lock)
        {
            _status = finished;
        }

        logger.LogInformation(
            "Screenshots refresh {State}: checked={Checked} updated={Updated} current={Current} failed={Failed}",
            finished.State, finished.Checked, finished.Updated, finished.Current, finished.Failed);
    }
}
