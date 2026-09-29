using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Web.Mapping;
using ShizuAppStoreServer.Web.Services;

namespace ShizuAppStoreServer.Web.Pages;

[OutputCache(PolicyName = "pages")]
public sealed class AppsModel(CatalogService catalog) : PageModel
{
    public const int PageSize = 48;

    public string? Query { get; private set; }
    public string? CategorySlug { get; private set; }
    public string? CategoryName { get; private set; }
    public AppListSort Sort { get; private set; } = AppListSort.Stars;
    public AppListPrice? Price { get; private set; }
    public bool Recommended { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int Total { get; private set; }
    public IReadOnlyList<AppListItemView> Items { get; private set; } = [];
    public IReadOnlyList<Category> Categories { get; private set; } = [];

    public int TotalPages => Math.Max(1, (Total + PageSize - 1) / PageSize);

    public string SortKey => Sort.ToString().ToLowerInvariant();

    public string? PriceKey => Price?.ToString().ToLowerInvariant();

    public async Task OnGetAsync(
        string? q,
        string? category,
        string? sort,
        string? price,
        bool recommended = false,
        [FromQuery(Name = "page")] int page = 1,
        CancellationToken ct = default)
    {
        Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        CategorySlug = string.IsNullOrWhiteSpace(category) ? null : category;
        Recommended = recommended;
        PageNumber = Math.Max(1, page);
        Sort = Enum.TryParse<AppListSort>(sort, ignoreCase: true, out var parsedSort)
            ? parsedSort
            : AppListSort.Stars;
        Price = Enum.TryParse<AppListPrice>(price, ignoreCase: true, out var parsedPrice)
            ? parsedPrice
            : null;

        var result = await catalog.GetAppsAsync(
            new AppListQuery(Query, CategorySlug, Sort, Price, Recommended, PageNumber, PageSize), ct);
        Items = result.Items.Select(app => AppListItemView.Create(app, Sort)).ToList();
        Total = result.Total;
        Categories = await catalog.GetRootCategoriesAsync(ct);
        CategoryName = Categories.FirstOrDefault(c => c.Slug == CategorySlug)?.Name;
        Response.Headers.CacheControl = "public, max-age=60";
    }

    public string SortLabel(AppListSort sort) => sort switch
    {
        AppListSort.RecentlyAdded => "Recently added",
        AppListSort.RecentlyUpdated => "Recently updated",
        AppListSort.Stars => "Most starred",
        AppListSort.Downloads => "Popularity",
        AppListSort.SizeDesc => "Largest first",
        _ => "Name",
    };

    public string PriceLabel(AppListPrice? price) => price switch
    {
        AppListPrice.Free => "Free",
        AppListPrice.Iap => "IAP",
        AppListPrice.IapOrPaid => "IAP or Paid",
        _ => "All prices",
    };

    public IReadOnlyList<int?> PageNumbers()
    {
        if (TotalPages <= 7)
        {
            return Enumerable.Range(1, TotalPages).Select(page => (int?)page).ToList();
        }

        var first = Math.Clamp(PageNumber - 1, 2, TotalPages - 3);
        var pages = new List<int?> { 1 };
        if (first > 2)
        {
            pages.Add(null);
        }

        for (var page = first; page < first + 3; page++)
        {
            pages.Add(page);
        }

        if (first + 3 < TotalPages)
        {
            pages.Add(null);
        }

        pages.Add(TotalPages);
        return pages;
    }

    public string ListUrl(string? category, AppListSort sort, AppListPrice? price, bool recommended, int page = 1)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Query))
        {
            parts.Add($"q={Uri.EscapeDataString(Query)}");
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            parts.Add($"category={Uri.EscapeDataString(category)}");
        }

        if (sort != AppListSort.Stars)
        {
            parts.Add($"sort={sort.ToString().ToLowerInvariant()}");
        }

        if (price is { } priceValue)
        {
            parts.Add($"price={priceValue.ToString().ToLowerInvariant()}");
        }

        if (recommended)
        {
            parts.Add("recommended=true");
        }

        if (page > 1)
        {
            parts.Add($"page={page}");
        }

        return parts.Count == 0 ? "/apps" : "/apps?" + string.Join("&", parts);
    }
}
