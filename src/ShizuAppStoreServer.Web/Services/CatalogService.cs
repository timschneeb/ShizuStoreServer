using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Web.Mapping;

namespace ShizuAppStoreServer.Web.Services;

/// <summary>
/// Read-only catalog reads over the API's database. The API owns the schema
/// and is the only writer; this app only selects.
/// </summary>
public sealed class CatalogService(ShizuDbContext db)
{
    /// <summary>
    /// Detail row for a slug, mirroring the API's detail query: excluded rows
    /// read as missing, variants load their parent for the cross-link.
    /// </summary>
    public Task<App?> GetAppAsync(string slug, CancellationToken ct = default) =>
        db.Apps.AsNoTracking()
            .Include(a => a.Category).ThenInclude(c => c!.Parent)
            .Include(a => a.Parent)
            .Include(a => a.Downloads)
            .FirstOrDefaultAsync(a => a.Slug == slug && a.Availability != Availability.Excluded, ct);

    /// <summary>Carousel source: other apps by the same author, best installed first.</summary>
    public Task<List<App>> GetMoreFromAuthorAsync(App app, int limit, CancellationToken ct = default) =>
        db.Apps.AsNoTracking()
            .Where(a => a.AuthorKey == app.AuthorKey
                && a.Id != app.Id
                && (app.RootAppId == null || a.Id != app.RootAppId)
                && a.RootAppId == null
                && a.Availability != Availability.Excluded)
            .OrderByDescending(a => a.InstallCount)
            .ThenBy(a => a.Name)
            .Take(limit)
            .ToListAsync(ct);

    /// <summary>Carousel source: other apps in the same category.</summary>
    public Task<List<App>> GetMoreFromCategoryAsync(App app, int limit, CancellationToken ct = default) =>
        db.Apps.AsNoTracking()
            .Where(a => a.CategoryId == app.CategoryId
                && a.Id != app.Id
                && (app.RootAppId == null || a.Id != app.RootAppId)
                && a.RootAppId == null
                && a.Availability != Availability.Excluded)
            .OrderByDescending(a => a.InstallCount)
            .ThenBy(a => a.Name)
            .Take(limit)
            .ToListAsync(ct);

    /// <summary>Root categories for the list filter, name-sorted.</summary>
    public Task<List<Category>> GetRootCategoriesAsync(CancellationToken ct = default) =>
        db.Categories.AsNoTracking()
            .Where(c => c.Section == CategorySection.Apps && c.ParentId == null)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    /// <summary>
    /// Filtered and sorted list page, mirroring the client's AppListQueryBuilder
    /// (main listing, non-excluded, search over name/description/package).
    /// </summary>
    public async Task<(IReadOnlyList<App> Items, int Total)> GetAppsAsync(AppListQuery query, CancellationToken ct = default)
    {
        var apps = db.Apps.AsNoTracking()
            .Where(a => a.Availability != Availability.Excluded && a.Listing == Listing.Main);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLower();
            apps = apps.Where(a =>
                a.Name.ToLower().Contains(search) ||
                (a.DisplayName != null && a.DisplayName.ToLower().Contains(search)) ||
                a.Description.ToLower().Contains(search) ||
                (a.PackageName != null && a.PackageName.ToLower().Contains(search)));
        }

        if (!string.IsNullOrWhiteSpace(query.CategorySlug))
        {
            var categoryIds = await GetCategorySubtreeIdsAsync(query.CategorySlug, ct);
            apps = categoryIds.Count == 0
                ? apps.Where(_ => false)
                : apps.Where(a => categoryIds.Contains(a.CategoryId));
        }

        apps = query.Price switch
        {
            AppListPrice.Free => apps.Where(a => !a.HasPaid && !a.HasIap),
            AppListPrice.Iap => apps.Where(a => !a.HasPaid && a.HasIap),
            AppListPrice.IapOrPaid => apps.Where(a => a.HasPaid || a.HasIap),
            _ => apps,
        };

        if (query.Recommended)
        {
            apps = apps.Where(a => a.IsRecommended);
        }

        var total = await apps.CountAsync(ct);
        var useInstallCounts = await UseInstallCountsForPopularityAsync(ct);

        var sorted = query.Sort switch
        {
            AppListSort.RecentlyAdded => apps
                .OrderBy(a => a.ListUpdatedAt == null)
                .ThenByDescending(a => a.ListUpdatedAt)
                .ThenBy(a => a.Name),
            AppListSort.RecentlyUpdated => apps
                .OrderBy(a => a.VersionUpdatedAt == null)
                .ThenByDescending(a => a.VersionUpdatedAt)
                .ThenBy(a => a.Name),
            AppListSort.Stars => apps
                .OrderBy(a => a.Stars == null)
                .ThenByDescending(a => a.Stars)
                .ThenBy(a => a.Name),
            AppListSort.Downloads => useInstallCounts
                ? apps.OrderByDescending(a => a.InstallCount).ThenBy(a => a.Name)
                : apps.OrderBy(a => a.DownloadTotal == null)
                    .ThenByDescending(a => a.DownloadTotal)
                    .ThenBy(a => a.Name),
            AppListSort.SizeDesc => apps
                .OrderBy(a => a.Downloads.Where(d => d.IsPrimary).Select(d => d.SizeBytes).FirstOrDefault() == null)
                .ThenByDescending(a => a.Downloads.Where(d => d.IsPrimary).Select(d => d.SizeBytes).FirstOrDefault())
                .ThenBy(a => a.Name),
            _ => apps.OrderBy(a => a.DisplayName ?? a.Name),
        };

        var items = await sorted
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Include(a => a.Downloads)
            .ToListAsync(ct);

        return (items, total);
    }

    private async Task<List<long>> GetCategorySubtreeIdsAsync(string slug, CancellationToken ct)
    {
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (category is null)
        {
            return [];
        }

        return await db.Categories.AsNoTracking()
            .Where(c => c.Id == category.Id || c.ParentId == category.Id)
            .Select(c => c.Id)
            .ToListAsync(ct);
    }

    private async Task<bool> UseInstallCountsForPopularityAsync(CancellationToken ct)
    {
        var flag = await db.ConfigFlags.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Key == "use_install_counts_for_popularity", ct);
        return flag is not null && bool.TryParse(flag.Value, out var value) && value;
    }
}
