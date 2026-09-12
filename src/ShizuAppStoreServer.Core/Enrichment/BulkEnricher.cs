namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// Parallel fan-out over items, bounded by a <see cref="SemaphoreSlim"/>
/// (PLAN §5: max ~4 parallel enrichments). Order-preserving, fault-isolating:
/// one item's crash becomes a <c>Failed</c> result, never a lost batch.
/// The M6 hosted service fans out over app <em>ids</em> (entities can't
/// cross the per-item DI scopes the loop enriches in); tests pass lambdas.
/// The tuple element is still named <c>App</c> for history (it used to be
/// <c>App</c>-typed before the M6 generalization).
/// </summary>
public static class BulkEnricher
{
    public static Task<IReadOnlyList<(T App, EnrichResult Result)>> EnrichManyAsync<T>(
        IEnumerable<T> items,
        Func<T, CancellationToken, Task<EnrichResult>> enrichOne,
        int maxParallelism,
        CancellationToken ct = default) =>
        EnrichManyAsync(items, enrichOne, maxParallelism,
            ex => new EnrichResult(EnrichOutcome.Failed, $"enrich crashed: {ex.Message}"), ct);

    /// <summary>
    /// Result-generic fan-out (the icon-refresh prepare phase returns
    /// <c>PrepareIconResult</c>, not <c>EnrichResult</c>); crashes map via
    /// <paramref name="onCrash"/>.
    /// </summary>
    public static async Task<IReadOnlyList<(T App, R Result)>> EnrichManyAsync<T, R>(
        IEnumerable<T> items,
        Func<T, CancellationToken, Task<R>> enrichOne,
        int maxParallelism,
        Func<Exception, R> onCrash,
        CancellationToken ct = default)
    {
        var list = items.ToList();
        var results = new (T, R)?[list.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallelism));

        async Task RunOne(int index)
        {
            await gate.WaitAsync(ct);
            try
            {
                results[index] = (list[index], await enrichOne(list[index], ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results[index] = (list[index], onCrash(ex));
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(list.Select((_, i) => RunOne(i)));
        return results.Select(r => r!.Value).ToList();
    }
}
