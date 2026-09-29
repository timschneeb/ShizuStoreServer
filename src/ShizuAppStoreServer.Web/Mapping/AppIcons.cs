namespace ShizuAppStoreServer.Web.Mapping;

/// <summary>
/// Material Symbols Rounded glyph names backed by the vendored subset font, mirroring
/// the Android client's icon choices. Categories without a Material glyph fall back
/// to the inline SVG sprite.
/// </summary>
public static class AppIcons
{
    public const string Recommended = "editor_choice";
    public const string CategoryFallback = "category";

    public static string? CategorySymbol(string? slug) => slug switch
    {
        "ai-agents" => "smart_toy",
        "android-auto" => "directions_car",
        "android-tv" => "tv",
        "audio" => "graphic_eq",
        "automation" => "precision_manufacturing",
        "communication" => "forum",
        "customization" => "palette",
        "development-utilities" => "code",
        "device-owner-dpm" => "shield_person",
        "dhizuku" => null,
        "display-management" => "display_settings",
        "entertainment" => "movie",
        "file-management" => "folder",
        "games" => "sports_esports",
        "input-methods" => "keyboard",
        "installer-app-stores" => "storefront",
        "miscellaneous" => CategoryFallback,
        "network" => "wifi",
        "patching" => "handyman",
        "power-management" => "charger",
        "privacy" => "encrypted",
        "productivity" => "task_alt",
        "quick-settings" => "tune",
        "root" => "terminal",
        "shizuku-implementations" => null,
        "software-management" => "deployed_code",
        "task-manager" => "memory",
        "terminals" => "terminal",
        "vendor-specific" => "devices",
        "vendor-specific-other" => "devices_other",
        _ when slug is not null && slug.StartsWith("vendor-specific-", StringComparison.Ordinal) => "mobile_2",
        _ => CategoryFallback,
    };

    public static string CategorySpriteId(string? slug) => slug switch
    {
        "ai-agents" => "smart-toy",
        "android-auto" => "car",
        "android-tv" => "tv",
        "audio" => "graphic-eq",
        "automation" => "factory",
        "communication" => "forum",
        "customization" => "palette",
        "development-utilities" => "code",
        "device-owner-dpm" => "shield-person",
        "dhizuku" => "shizuku",
        "display-management" => "display-settings",
        "entertainment" => "movie",
        "file-management" => "folder",
        "games" => "sports-esports",
        "input-methods" => "keyboard",
        "installer-app-stores" => "storefront",
        "miscellaneous" => "category",
        "network" => "wifi",
        "patching" => "handyman",
        "power-management" => "charger",
        "privacy" => "encrypted",
        "productivity" => "task-alt",
        "quick-settings" => "tune",
        "root" => "terminal",
        "shizuku-implementations" => "shizuku",
        "software-management" => "package",
        "task-manager" => "memory",
        "terminals" => "terminal",
        "vendor-specific" => "devices",
        "vendor-specific-other" => "devices-other",
        _ when slug is not null && slug.StartsWith("vendor-specific-", StringComparison.Ordinal) => "mobile",
        _ => "category",
    };

    public static string SortSymbol(AppListSort sort) => sort switch
    {
        AppListSort.Name => "sort_by_alpha",
        AppListSort.RecentlyAdded => "schedule",
        AppListSort.RecentlyUpdated => "update",
        AppListSort.Stars => "star",
        AppListSort.Downloads => "download",
        _ => "sd_card",
    };

    public static string PriceSymbol(AppListPrice? price) => price switch
    {
        AppListPrice.Free => "money_off",
        AppListPrice.Iap => "redeem",
        AppListPrice.IapOrPaid => "paid",
        _ => "attach_money",
    };
}
