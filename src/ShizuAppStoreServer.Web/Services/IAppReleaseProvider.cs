namespace ShizuAppStoreServer.Web.Services;

/// <summary>Resolves the download link for the latest ShizuStore client APK.</summary>
public interface IAppReleaseProvider
{
    Task<string> GetLatestApkUrlAsync(CancellationToken ct = default);
}
