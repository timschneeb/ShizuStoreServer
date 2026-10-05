namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// One analyzed APK download: badging + file hash/size + (best-effort)
/// signer certs. Owns the temp file; dispose when done (icon extraction
/// via <c>IconProcessor</c> happens first, on <see cref="ApkPath"/>).
/// </summary>
internal sealed record AnalyzedApk(
    string ApkPath,
    BadgingInfo Badging,
    string FileSha256,
    long FileSize,
    ApkSignerInfo Signers,
    ApkInspection Inspection) : IDisposable
{
    public void Dispose()
    {
        try { File.Delete(ApkPath); } catch { /* best effort */ }
    }
}
