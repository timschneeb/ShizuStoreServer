namespace ShizuAppStoreServer.Web.Mapping;

/// <summary>Sort options mirroring the client's AppSort enum.</summary>
public enum AppListSort
{
    Name,
    RecentlyAdded,
    RecentlyUpdated,
    Stars,
    Downloads,
    SizeDesc,
}

/// <summary>Price filters mirroring the client's AppPrice enum.</summary>
public enum AppListPrice
{
    Free,
    Iap,
    IapOrPaid,
}

public sealed record AppListQuery(
    string? Search,
    string? CategorySlug,
    AppListSort Sort,
    AppListPrice? Price,
    bool Recommended,
    int Page,
    int PageSize);
