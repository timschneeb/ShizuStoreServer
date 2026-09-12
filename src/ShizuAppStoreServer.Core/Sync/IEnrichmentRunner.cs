using ShizuAppStoreServer.Core.Enrichment;

namespace ShizuAppStoreServer.Core.Sync;

/// <summary>
/// Per-app enrichment behind an interface so <see cref="SyncService"/>
/// stays testable without HTTP/aapt2. The production implementation
/// (Web project) enriches in a fresh DI scope per call and saves;
/// tests substitute a stub.
/// </summary>
public interface IEnrichmentRunner
{
    Task<EnrichResult> EnrichAsync(long appId, bool force, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Batch phase A: download + analyze + stage one app's XML icon into
    /// the shared batch dir (raster icons resolve immediately). Phase C
    /// (<see cref="CommitIconRefreshAsync"/>) adopts the shared render.
    /// </summary>
    Task<PrepareIconResult> PrepareIconRefreshAsync(
        long appId, string batchWorkDir, string prefix, CancellationToken ct = default, bool force = false);

    /// <summary>Batch phase C: adopt one shared-render PNG for the app.
    /// <paramref name="isAdaptive"/> carries the staged root kind (only
    /// <c>&lt;adaptive-icon&gt;</c> roots count) into the row flag.</summary>
    Task<EnrichResult> CommitIconRefreshAsync(long appId, byte[]? png, CancellationToken ct = default, bool force = false, bool isAdaptive = false);
}
