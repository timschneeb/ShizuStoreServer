namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Per-app per-UTC-day install counts (table <c>app_install_days</c>), the
/// trend companion to <see cref="App.InstallCount"/>. Rows are upserted by
/// <c>POST /v1/apps/{slug}/installs</c> and disappear with their app.
/// </summary>
public sealed class AppInstallDay
{
    public long AppId { get; set; }
    public App? App { get; set; }

    public DateOnly Day { get; set; }
    public long InstallCount { get; set; }
}
