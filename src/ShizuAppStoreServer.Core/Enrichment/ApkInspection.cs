namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// Static signals derived from one analyzed APK: manifest-level Shizuku
/// facts plus Exodus tracker code-signature matches. Candidates carry
/// null when no analysis ran (index-only rows), so those never overwrite
/// what an earlier analysis recorded.
/// </summary>
internal sealed record ApkInspection(ApkSignals Signals, IReadOnlyList<TrackerHit> Trackers);
