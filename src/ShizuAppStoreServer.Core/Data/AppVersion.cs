namespace ShizuAppStoreServer.Core.Data;

/// <summary>Append-only release history (table <c>app_versions</c>); feeds update checks.</summary>
public sealed class AppVersion
{
    public long Id { get; set; }

    public long AppId { get; set; }
    public App? App { get; set; }

    public long? VersionCode { get; set; }
    public string? VersionName { get; set; }
    public string? ApkUrl { get; set; }

    public DateTimeOffset DetectedAt { get; set; }
}
