using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Web.Tests;

internal static class Seeds
{
    public static Category Category(long id, string slug, string name, long? parentId = null) => new()
    {
        Id = id,
        Slug = slug,
        Name = name,
        ParentId = parentId,
        Section = CategorySection.Apps,
    };

    public static App App(long id, string slug, string name, long categoryId, bool published = true) => new()
    {
        Id = id,
        Slug = slug,
        Name = name,
        Description = $"{name} description",
        Url = $"https://example.com/{slug}",
        CategoryId = categoryId,
        Availability = Availability.DirectApk,
        PublishedAt = published ? new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) : null,
        AddedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        VersionUpdatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
    };

    public static AppDownload Download(
        long appId,
        string url,
        bool primary = true,
        long? versionCode = 10,
        string packageName = "com.example.app") => new()
    {
        AppId = appId,
        ApkUrl = url,
        IsPrimary = primary,
        VersionCode = versionCode,
        VersionName = $"1.{versionCode}",
        PackageName = packageName,
        SigKey = $"key:{url}",
        ResolvedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
    };
}
