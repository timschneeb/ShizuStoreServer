using ShizuAppStoreServer.Core.Sync;

namespace ShizuAppStoreServer.Sync;

/// <summary>
/// Runs one sync pass inside the gate (shared by both workers so the logic lives in exactly one place).
/// Never throws except on shutdown cancellation: <see cref="SyncService"/>
/// already converts pass failures into error results + error run rows, and anything else is logged here.
/// </summary>
public sealed class SyncPassRunner(
    IServiceScopeFactory scopes, SyncGate gate, ILogger<SyncPassRunner> logger)
{
    public async Task RunOnceAsync(string trigger, bool fullRecheck, CancellationToken ct)
    {
        bool entered;
        try
        {
            entered = await gate.WaitAsync(0, ct);
        }
        catch (OperationCanceledException)
        {
            return; // shutting down
        }

        if (!entered)
        {
            logger.LogInformation("Sync pass ({Trigger}) skipped: another pass is running.", trigger);
            return;
        }

        try
        {
            using var scope = scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<SyncService>();
            var r = await svc.RunAsync(trigger, fullRecheck, DateTimeOffset.UtcNow, ct);
            if (r.Error is not null)
            {
                logger.LogError("Sync pass ({Trigger}) failed: {Error}", r.Trigger, r.Error);
            }
            else
            {
                logger.LogInformation(
                    "Sync pass ({Trigger}): added={Added} updated={Updated} removed={Removed} "
                    + "enriched={Enriched} upToDate={UpToDate} failed={Failed} drained={Drained} "
                    + "warnings={Warnings} archived={Archived} skipped={Skipped} head={Head}",
                    r.Trigger, r.Added, r.Updated, r.Removed, r.Enriched, r.UpToDate,
                    r.Failed, r.DrainedRequests, r.ParseWarnings, r.ArchivedChanged,
                    r.Skipped, r.HeadCommit);
                foreach (var message in r.FailedMessages)
                {
                    logger.LogWarning("Sync pass ({Trigger}) app failure: {Message}", r.Trigger, message);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Sync pass ({Trigger}) crashed outside the service guard.", trigger);
        }
        finally
        {
            gate.Release();
        }
    }
}
