using ShizuAppStoreServer.Core.Enrichment.Icons;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// Phase-A outcome of a batched icon refresh: either final already
/// (raster written, up-to-date, failed) or a staged <c>Pending</c> entry
/// awaiting the shared render plus a commit call.
/// </summary>
public sealed record PrepareIconResult(
    EnrichOutcome Outcome, string? Error, PendingBatchIcon? Pending);
