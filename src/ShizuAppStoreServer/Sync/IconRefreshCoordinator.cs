using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sync;

namespace ShizuAppStoreServer.Sync;

/// <summary>Snapshot of the operator-triggered icon refresh.</summary>
public sealed record IconRefreshStatus(
    string State,
    bool Force,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int Checked,
    int Refreshed,
    int AlreadyCurrent,
    int Failed,
    IReadOnlyList<string> Errors,
    string? Error)
{
    public static readonly IconRefreshStatus Idle =
        new("idle", false, null, null, 0, 0, 0, 0, [], null);
}

/// <summary>
/// Runs <see cref="SyncService.RefreshIconsAsync"/> inside the live server so
/// a heal needs no downtime and no second process. The run takes the shared
/// <see cref="SyncGate"/>, so it excludes the fast and nightly passes exactly
/// like they exclude each other; the API keeps serving reads while it holds
/// the gate. One run at a time.
/// </summary>
public sealed class IconRefreshCoordinator(
    IServiceScopeFactory scopes,
    SyncGate gate,
    EnrichmentOptions enrichment,
    IHostApplicationLifetime lifetime,
    ILogger<IconRefreshCoordinator> logger)
{
    private const string Idle = "idle";
    private const string Running = "running";
    private const string Completed = "completed";
    private const string Failed = "failed";

    private readonly object _lock = new();
    private IconRefreshStatus _status = IconRefreshStatus.Idle;
    private CancellationTokenSource? _runCts;

    public IconRefreshStatus Snapshot()
    {
        lock (_lock)
        {
            return _status;
        }
    }

    /// <summary>
    /// Starts a refresh unless one is running, disabled, or a sync pass holds
    /// the gate. <paramref name="reason"/> explains a refusal.
    /// </summary>
    public (bool Started, string? Reason, IconRefreshStatus Status) TryStart(bool force)
    {
        lock (_lock)
        {
            if (_status.State == Running)
            {
                return (false, "An icon refresh is already running.", _status);
            }

            if (enrichment.SkipApkAnalysis)
            {
                return (false,
                    "Enrichment:SkipApkAnalysis is enabled; icon refresh needs APK analysis.", _status);
            }

            // Non-blocking: never queue behind (or starve) a sync pass. The
            // operator retries once the pass finishes.
            if (!gate.Wait(0))
            {
                return (false, "A sync pass is running; retry when it finishes.", _status);
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            _runCts = cts;
            _status = new IconRefreshStatus(Running, force, DateTimeOffset.UtcNow, null, 0, 0, 0, 0, [], null);
            var started = _status;
            _ = Task.Run(() => RunAsync(force, started.StartedAt!.Value, cts));
            return (true, null, started);
        }
    }

    /// <summary>Cancels the running refresh; false when nothing is running.</summary>
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

    private async Task RunAsync(bool force, DateTimeOffset startedAt, CancellationTokenSource cts)
    {
        IconRefreshResult? result = null;
        string? error = null;
        try
        {
            using var scope = scopes.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<SyncService>();
            result = await service.RefreshIconsAsync(cts.Token, force);
        }
        catch (OperationCanceledException)
        {
            error = "Icon refresh was cancelled.";
        }
        catch (Exception ex)
        {
            error = $"Icon refresh crashed: {ex.Message}";
            logger.LogError(ex, "Icon refresh crashed.");
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
            ? new IconRefreshStatus(Failed, force, startedAt, DateTimeOffset.UtcNow, 0, 0, 0, 0, [], error)
            : new IconRefreshStatus(
                Completed, force, startedAt, DateTimeOffset.UtcNow,
                result.Checked, result.Refreshed, result.AlreadyCurrent, result.Failed,
                result.Errors,
                result.Failed > 0 ? "Some icons failed; see errors." : null);
        lock (_lock)
        {
            _status = finished;
        }

        logger.LogInformation(
            "Icon refresh {State}: checked={Checked} refreshed={Refreshed} current={Current} failed={Failed}",
            finished.State, finished.Checked, finished.Refreshed, finished.AlreadyCurrent, finished.Failed);
    }
}
