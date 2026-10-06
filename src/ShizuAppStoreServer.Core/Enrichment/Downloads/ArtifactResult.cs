namespace ShizuAppStoreServer.Core.Enrichment.Downloads;

/// <summary>
/// Outcome of downloading and analyzing one artifact. <c>Unchanged</c> is
/// true when the computed checksum matched the recorded one, in which case
/// no badging, signer or icon work ran and <c>Analysis</c> is null.
/// </summary>
internal sealed record ArtifactResult(ArtifactAnalysis? Analysis, bool Unchanged, string? Error);
