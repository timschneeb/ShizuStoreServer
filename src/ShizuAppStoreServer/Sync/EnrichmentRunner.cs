using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sync;

namespace ShizuAppStoreServer.Sync;

/// <summary>
/// Production <see cref="IEnrichmentRunner"/>: enriches one app in a fresh
/// DI scope (its own <see cref="ShizuDbContext"/>, required — contexts are
/// not thread-safe and the loop fans out) and saves. A vanished row (deleted
/// by a concurrent pass, which the gate normally prevents) becomes
/// <c>Failed</c>, never a crash.
/// </summary>
public sealed class EnrichmentRunner(IServiceScopeFactory scopes) : IEnrichmentRunner
{
    public async Task<EnrichResult> EnrichAsync(
        long appId, bool force, DateTimeOffset now, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<ShizuDbContext>();
        App? app = null;
        try
        {
            app = await db.Apps.FindAsync([appId], ct);
            if (app is null)
            {
                return new EnrichResult(EnrichOutcome.Failed, "App row vanished mid-pass.");
            }

            var result = await provider.GetRequiredService<AppEnricher>().EnrichAsync(app, now, ct, force);
            await db.SaveChangesAsync(ct);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Infrastructure failure outside the enricher (DB blip, runner bug):
            // without this, BulkEnricher counts a silent Failed that no log
            // and no row ever explains (seen live 2026-09-12). Record on the
            // row when possible so backoff applies; the message always flows
            // to the pass log via FailedMessages. Shutdown still propagates.
            var message = $"Enrichment host error: {ex.Message}";
            try
            {
                if (app is not null)
                {
                    app.LastCheckedAt = now;
                    app.LastError = message.Length > 500 ? message[..500] + "…" : message;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch
            {
                // The row update itself failed; the message below still reports.
            }

            return new EnrichResult(EnrichOutcome.Failed, message);
        }
    }

    public async Task<PrepareIconResult> PrepareIconRefreshAsync(
        long appId, string batchWorkDir, string prefix, CancellationToken ct = default, bool force = false)
    {
        using var scope = scopes.CreateScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<ShizuDbContext>();
        try
        {
            var app = await db.Apps.FindAsync([appId], ct);
            if (app is null)
            {
                return new PrepareIconResult(EnrichOutcome.Failed, "App row vanished mid-refresh.", null);
            }

            var result = await provider.GetRequiredService<AppEnricher>()
                .PrepareIconRefreshAsync(app, batchWorkDir, prefix, ct, force);
            await db.SaveChangesAsync(ct);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PrepareIconResult(EnrichOutcome.Failed, $"Icon refresh host error: {ex.Message}", null);
        }
    }

    public async Task<EnrichResult> CommitIconRefreshAsync(long appId, byte[]? png, CancellationToken ct = default, bool force = false, bool isAdaptive = false)
    {
        using var scope = scopes.CreateScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<ShizuDbContext>();
        try
        {
            var app = await db.Apps.FindAsync([appId], ct);
            if (app is null)
            {
                return new EnrichResult(EnrichOutcome.Failed, "App row vanished mid-refresh.");
            }

            var result = await provider.GetRequiredService<AppEnricher>().CommitIconRefreshAsync(app, png, ct, force, isAdaptive);
            await db.SaveChangesAsync(ct);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Icon-only run: never touch the row (no backoff disturbance);
            // the message still reaches the run summary.
            return new EnrichResult(EnrichOutcome.Failed, $"Icon refresh host error: {ex.Message}");
        }
    }
}
