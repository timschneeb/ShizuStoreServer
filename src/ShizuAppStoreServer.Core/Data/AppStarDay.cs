namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Per-app per-UTC-day star snapshots (table <c>app_star_days</c>), the
/// velocity companion to <see cref="App.Stars"/>. Each enrichment pass that
/// observes a star count upserts today's row, so the nightly recheck keeps
/// one point per day for every tracked app. Rows disappear with their app.
/// </summary>
public sealed class AppStarDay
{
    public long AppId { get; set; }
    public App? App { get; set; }

    public DateOnly Day { get; set; }
    public int Stars { get; set; }
}
