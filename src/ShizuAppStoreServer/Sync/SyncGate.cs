namespace ShizuAppStoreServer.Sync;

/// <summary>
/// Mutual exclusion between the fast loop and the nightly pass (singleton).
/// Passes try to enter without waiting — a contested tick is skipped and
/// retried next period, never queued.
/// </summary>
public sealed class SyncGate() : SemaphoreSlim(1, 1);
