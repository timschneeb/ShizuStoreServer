using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Web.Mapping;

/// <summary>One row on the app list page, mirroring the client's list item.</summary>
public sealed record AppListItemView(
    string Slug,
    string Name,
    string Description,
    string? IconPath,
    bool IconAdaptive,
    string Meta,
    IReadOnlyList<string> Badges)
{
    public static AppListItemView Create(App app, AppListSort sort)
    {
        var primary = app.Downloads.FirstOrDefault(d => d.IsPrimary);
        var badges = new List<string>();
        if (app.HasIap)
        {
            badges.Add("IAP");
        }

        if (app.HasPaid)
        {
            badges.Add("Paid");
        }

        if (app.HasAds)
        {
            badges.Add("Ads");
        }

        var meta = sort switch
        {
            AppListSort.Stars when app.Stars is > 0 => $"★ {Display.CompactCount(app.Stars)}",
            AppListSort.RecentlyAdded => Display.RelativeAge(app.ListUpdatedAt),
            AppListSort.RecentlyUpdated => Display.RelativeAge(app.VersionUpdatedAt),
            _ => JoinVersionAndSize(primary),
        };

        return new AppListItemView(
            app.Slug,
            app.DisplayName ?? app.Name,
            app.Description,
            string.IsNullOrEmpty(app.IconHash) ? null : $"/icons/{app.IconHash}.png",
            app.IconAdaptive,
            meta,
            badges);
    }

    private static string JoinVersionAndSize(AppDownload? primary)
    {
        var version = primary?.VersionName;
        var size = Display.SizeLabel(primary?.SizeBytes);
        if (string.IsNullOrWhiteSpace(version))
        {
            return size == "-" ? string.Empty : size;
        }

        return size == "-" ? version : $"{version} · {size}";
    }
}
