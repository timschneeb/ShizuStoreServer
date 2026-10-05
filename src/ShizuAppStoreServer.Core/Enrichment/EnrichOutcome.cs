namespace ShizuAppStoreServer.Core.Enrichment;

public enum EnrichOutcome
{
    /// <summary>APK downloaded + aapt2 parsed, row updated (possibly with letter-avatar icon).</summary>
    Enriched,
    /// <summary>Release feed / repo index answered 304 Not Modified (or the same asset is already recorded); only <c>last_checked_at</c> touched.</summary>
    UpToDate,
    /// <summary>No forge source: letter-avatar icon, source kind set, no APK fields.</summary>
    AvatarFallback,
    /// <summary>Something failed; <c>last_error</c> set, backoff applies. Previous good values kept.</summary>
    Failed,
    /// <summary>Checked recently (success or backoff window); no work done.</summary>
    SkippedFresh,
}
