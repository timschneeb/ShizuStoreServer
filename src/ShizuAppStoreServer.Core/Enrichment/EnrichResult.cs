namespace ShizuAppStoreServer.Core.Enrichment;

public sealed record EnrichResult(EnrichOutcome Outcome, string? Error)
{
    /// <summary>
    /// Optional human-readable reason for a non-action outcome, e.g. why a run
    /// skipped an app or only skipped APK analysis. Written to the run log.
    /// </summary>
    public string? Detail { get; init; }
}
