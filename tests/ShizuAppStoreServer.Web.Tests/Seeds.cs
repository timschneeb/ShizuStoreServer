using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Web.Tests;

/// <summary>Small builders for endpoint-test fixtures.</summary>
public static class Seeds
{
    public static Category NewCategory(
        string slug,
        string? name = null,
        CategorySection section = CategorySection.Apps,
        Category? parent = null) => new()
        {
            Slug = slug,
            Name = name ?? slug,
            Section = section,
            Parent = parent,
        };

    public static App NewApp(
        string slug,
        Category category,
        string? name = null,
        string? url = null,
        Listing listing = Listing.Main,
        AppType type = AppType.App,
        Availability availability = Availability.LinkOnly,
        DateTimeOffset? addedAt = null,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? listUpdatedAt = null,
        bool recommended = false,
        string? license = null,
        string? packageName = null,
        App? parent = null,
        string? authorKey = null,
        string? authorName = null,
        string? authorUrl = null,
        List<string>? permissions = null,
        string? fullDescription = null,
        string? versionName = null,
        string? displayName = null) => new()
        {
            Slug = slug,
            Name = name ?? slug,
            DisplayName = displayName,
            Url = url ?? $"https://github.com/example/{slug}",
            Description = $"{slug} description",
            License = license,
            Listing = listing,
            Type = type,
            Availability = availability,
            IsRecommended = recommended,
            PackageName = packageName,
            AuthorKey = authorKey,
            AuthorName = authorName,
            AuthorUrl = authorUrl,
            Permissions = permissions ?? [],
            FullDescription = fullDescription,
            VersionName = versionName,
            Category = category,
            Parent = parent,
            AddedAt = addedAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ListUpdatedAt = listUpdatedAt,
            UpdatedAt = updatedAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
}
