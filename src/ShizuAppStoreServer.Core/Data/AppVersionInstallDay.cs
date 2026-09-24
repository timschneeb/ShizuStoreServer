namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Per-app per-version install counts (table <c>app_version_install_days</c>),
/// split by install type so fresh installs can be told apart from updates.
/// <c>version_code</c> 0 and <see cref="Unknown"/> stand in for reports from
/// older clients, which send neither field. Rows are upserted by
/// <c>POST /v1/apps/{slug}/installs</c> and disappear with their app.
/// </summary>
public sealed class AppVersionInstallDay
{
    public const int MaxInstallTypeLength = 16;

    public const string Fresh = "fresh";
    public const string Update = "update";
    public const string Unknown = "unknown";

    public long AppId { get; set; }
    public App? App { get; set; }

    public long VersionCode { get; set; }
    public string InstallType { get; set; } = Unknown;
    public DateOnly Day { get; set; }
    public long InstallCount { get; set; }

    /// <summary>Lowercases the client value and collapses anything else to unknown.</summary>
    public static string NormalizeInstallType(string? value) => value?.ToLowerInvariant() switch
    {
        Fresh => Fresh,
        Update => Update,
        _ => Unknown,
    };
}
