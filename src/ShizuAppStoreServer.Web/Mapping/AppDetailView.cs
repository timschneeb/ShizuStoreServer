using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Web.Mapping;

public sealed record TrackerGroup(string Name, IReadOnlyList<string> Tags);

/// <summary>Tag chip data: text, an optional sprite icon id, and the category slug when the chip links to the filtered list.</summary>
public sealed record DetailTag(string Text, string? IconId, string? CategorySlug = null);

/// <summary>Link row data for the app-style link list.</summary>
public sealed record DetailLink(string Label, string IconId, string Url);

/// <summary>One install candidate row for the sources section.</summary>
public sealed record SourceRow(string Version, string Origin, string Package, bool IsPrimary, string DownloadUrl, string? ArchiveEntry, string? AbiLabel);

/// <summary>Compact tile data for the "more from" carousels.</summary>
public sealed record AppTile(string Slug, string Name, string? IconPath, bool IconAdaptive, string? SizeLabel);

/// <summary>
/// Presentation view over an app detail row. Mirrors the API's mapping rules
/// (display name fallback, primary download, download ordering, category
/// path, tracker grouping, signature sets) so the page shows the same data
/// the Android client sees.
/// </summary>
public sealed class AppDetailView(App app)
{
    public App App { get; } = app;

    public AppDownload? Primary { get; } = app.Downloads.FirstOrDefault(d => d.IsPrimary);

    public IReadOnlyList<AppDownload> Downloads { get; } = app.Downloads
        .OrderByDescending(d => d.IsPrimary)
        .ThenByDescending(d => d.VersionCode ?? -1)
        .ToList();

    public string Slug => App.Slug;

    public string Name => App.DisplayName ?? App.Name;

    /// <summary>Name without the variant parenthetical, for titles and link previews.</summary>
    public string TitleName => TrailingParenthetical.Replace(Name, string.Empty).Trim();

    private static readonly Regex TrailingParenthetical = new(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);

    public string Description => App.Description;

    public string? PackageName => App.PackageName ?? Primary?.PackageName;

    public string? IconPath => string.IsNullOrEmpty(App.IconHash) ? null : $"/icons/{App.IconHash}.png";

    public bool IconAdaptive => App.IconAdaptive;

    public string? VersionName => Primary?.VersionName ?? App.VersionName;

    public long? VersionCode => Primary?.VersionCode;

    public long? SizeBytes => Primary?.SizeBytes;

    public int? MinSdk => Primary?.MinSdk;

    public int? TargetSdk => Primary?.TargetSdk;

    public IReadOnlyList<string> Abis => Primary?.Abis ?? [];

    public int LocaleCount => Primary?.Locales.Count ?? 0;

    public bool RequiresDhizuku => Primary?.DhizukuDeclared ?? false;

    public bool IsClosedSource => App.Listing == Listing.ClosedSource;

    public string TypeLabel => Label(App.Type);

    public string SourceLabel => Label(App.SourceKind);

    public string? SignerDn => Primary?.SignerDn;

    public string? SignerScheme => Primary?.SignerScheme;

    public string? SignerKeyAlgorithm => Primary?.SignerKeyAlgorithm;

    public IReadOnlyList<string> SigSha256 => SplitSet(Primary?.SigSha256);

    public IReadOnlyList<string> SigMd5 => SplitSet(Primary?.SigMd5);

    public IReadOnlyList<Category> CategoryPath => BuildCategoryPath(App.Category);

    public IReadOnlyList<TrackerGroup> TrackerGroups => BuildTrackerGroups(Primary);

    /// <summary>Base for resolving relative links and images in markdown bodies.</summary>
    public string? BaseUrl => App.SourceUrl ?? App.Url;

    public IReadOnlyList<string> Badges => BuildBadges();

    public bool IsDirectApk => App.Availability == Availability.DirectApk;

    public bool IsPlayRedirect => App.Availability == Availability.PlayRedirect;

    public bool IsLinkOnly => App.Availability == Availability.LinkOnly;

    public DateTimeOffset? UpdatedAt => App.VersionUpdatedAt ?? App.UpdatedAt;

    public string UpdatedAge => Display.RelativeAge(UpdatedAt);

    public string? CategoryName => CategoryPath.FirstOrDefault()?.Name;

    public string CategoryIconId => AppIcons.CategorySpriteId(CategoryPath.FirstOrDefault()?.Slug);

    /// <summary>The client renders its stats strip for direct downloads with data.</summary>
    public bool HasStats => IsDirectApk
        && (App.InstallCount > 0 || App.DownloadTotal > 0 || App.Stars > 0 || SizeBytes > 1);

    public string? StoreNotice => IsPlayRedirect
        ? "This app is only available on Google Play. Tap to open the listing."
        : null;

    public string? BillingNotice
    {
        get
        {
            var parts = new List<string>();
            if (App.HasPaid && App.HasIap)
            {
                parts.Add("This is a paid app with in-app purchases.");
            }
            else
            {
                if (App.HasPaid)
                {
                    parts.Add("This is a paid app.");
                }

                if (App.HasIap)
                {
                    parts.Add("This app offers in-app purchases.");
                }
            }

            if (App.HasAds)
            {
                parts.Add("This app contains ads.");
            }

            return parts.Count == 0 ? null : string.Join(' ', parts);
        }
    }

    public string? ClosedSourceNotice => IsClosedSource
        ? "This app is closed source. Its code is not public, so the community cannot easily verify what it does."
        : null;

    public IReadOnlyList<DetailTag> Tags => BuildTags();

    public IReadOnlyList<DetailLink> Links => BuildLinks();

    public IReadOnlyList<SourceRow> Sources => BuildSources();

    public static string Label(Enum value)
    {
        var name = value.ToString();
        return value.GetType().GetField(name)?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? name;
    }

    /// <summary>Tile for the "more from" carousels.</summary>
    public static AppTile ToTile(App app)
    {
        var primary = app.Downloads.FirstOrDefault(d => d.IsPrimary);
        return new AppTile(
            app.Slug,
            app.DisplayName ?? app.Name,
            string.IsNullOrEmpty(app.IconHash) ? null : $"/icons/{app.IconHash}.png",
            app.IconAdaptive,
            primary?.SizeBytes is { } size && size > 1 ? Display.SizeLabel(size) : null);
    }

    private IReadOnlyList<DetailTag> BuildTags()
    {
        var tags = new List<DetailTag>();
        if (!IsDirectApk && App.Stars is > 0)
        {
            tags.Add(new DetailTag(Display.CompactCount(App.Stars), "star"));
        }

        if (CategoryPath.FirstOrDefault() is { } category)
        {
            tags.Add(new DetailTag(category.Name, CategoryIconId, category.Slug));
        }

        if (MinSdk is > 0)
        {
            tags.Add(new DetailTag(Display.AndroidRequirement(MinSdk), "android"));
        }

        if (UpdatedAt is not null)
        {
            tags.Add(new DetailTag(UpdatedAge, "updates"));
        }

        if (!string.IsNullOrWhiteSpace(App.License))
        {
            tags.Add(new DetailTag(App.License.Trim(), null));
        }

        if (RequiresDhizuku)
        {
            tags.Add(new DetailTag("Dhizuku", "shizuku"));
        }

        // Extras the client shows on its list screens; kept here so the web
        // page does not lose information the client presents elsewhere.
        if (App.IsRecommended)
        {
            tags.Add(new DetailTag("Recommended", "check-circle"));
        }

        if (App.TrialDays is { } days)
        {
            tags.Add(new DetailTag($"Trial {days} days", null));
        }

        if (App.RequiresRoot)
        {
            tags.Add(new DetailTag("Requires root", "shield"));
        }

        if (App.Type != AppType.App)
        {
            tags.Add(new DetailTag(TypeLabel, "package"));
        }

        return tags;
    }

    private IReadOnlyList<DetailLink> BuildLinks()
    {
        var links = new List<DetailLink>();
        var source = App.SourceUrl;
        var url = App.Url;
        var sourceUsed = false;
        if (!string.IsNullOrWhiteSpace(source))
        {
            if (IsClosedSource)
            {
                links.Add(new DetailLink("Website", "language", source));
            }
            else if (App.SourceKind == SourceKind.FDroid)
            {
                links.Add(new DetailLink("F-Droid", "language", source));
            }
            else
            {
                links.Add(new DetailLink("Source code", "code", source));
                sourceUsed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(url) && !string.Equals(url, source, StringComparison.OrdinalIgnoreCase))
        {
            var forge = App.SourceKind is SourceKind.GitHub or SourceKind.GitLab or SourceKind.Codeberg;
            if (forge && !sourceUsed && !IsClosedSource)
            {
                links.Add(new DetailLink("Source code", "code", url));
            }
            else
            {
                links.Add(new DetailLink("Website", "language", url));
            }
        }

        if (!IsPlayRedirect && !string.IsNullOrWhiteSpace(App.StoreUrl)
            && !string.Equals(App.StoreUrl, url, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(App.StoreUrl, source, StringComparison.OrdinalIgnoreCase))
        {
            links.Add(new DetailLink("Store", "storefront", App.StoreUrl));
        }

        return links;
    }

    private IReadOnlyList<SourceRow> BuildSources() =>
        Downloads
            .Where(d => !string.IsNullOrWhiteSpace(d.ApkUrl))
            .Select(d => new SourceRow(
                Version: string.IsNullOrWhiteSpace(d.VersionName) ? "-" : d.VersionName,
                Origin: OriginLabel(d),
                Package: d.PackageName ?? PackageName ?? "-",
                IsPrimary: d.IsPrimary,
                DownloadUrl: d.ApkUrl,
                ArchiveEntry: d.ArchiveEntry,
                AbiLabel: AbiLabel(d)))
            .ToList();

    /// <summary>
    /// Distinguishes otherwise identical candidates: the ABI of a per-arch
    /// build, "Universal" when one APK carries several, null when the build
    /// was never analyzed.
    /// </summary>
    private static string? AbiLabel(AppDownload download)
    {
        if (download.Abi is { Length: > 0 } single && download.Abis.Count <= 1)
        {
            return single;
        }

        return download.Abis.Count switch
        {
            > 1 => "Universal",
            1 => download.Abis[0],
            _ => null,
        };
    }

    private static string OriginLabel(AppDownload download) =>
        string.IsNullOrWhiteSpace(download.SourceRef)
            ? Label(download.Source)
            : $"{Label(download.Source)} · {download.SourceRef}";

    private IReadOnlyList<string> BuildBadges()
    {
        var badges = new List<string>();
        if (App.IsRecommended)
        {
            badges.Add("Recommended");
        }

        if (IsClosedSource)
        {
            badges.Add("Closed source");
        }

        if (App.HasAds)
        {
            badges.Add("Contains ads");
        }

        if (App.HasIap)
        {
            badges.Add("In-app purchases");
        }

        if (App.HasPaid)
        {
            badges.Add("Paid");
        }

        if (App.TrialDays is { } days)
        {
            badges.Add($"Trial {days} days");
        }

        if (App.RequiresRoot)
        {
            badges.Add("Requires root");
        }

        if (RequiresDhizuku)
        {
            badges.Add("Dhizuku");
        }

        if (App.Type != AppType.App)
        {
            badges.Add(TypeLabel);
        }

        if (App.Availability == Availability.PlayRedirect)
        {
            badges.Add("Play Store only");
        }
        else if (App.Availability == Availability.LinkOnly)
        {
            badges.Add("Website only");
        }

        return badges;
    }

    private static IReadOnlyList<Category> BuildCategoryPath(Category? category)
    {
        var chain = new List<Category>();
        for (var current = category; current is not null; current = current.Parent)
        {
            chain.Add(current);
        }

        chain.Reverse();
        return chain;
    }

    private static IReadOnlyList<TrackerGroup> BuildTrackerGroups(AppDownload? download)
    {
        if (download is null || download.TrackerTags.Count == 0)
        {
            return [];
        }

        var order = new List<string>();
        var tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entry in download.TrackerTags)
        {
            var separator = entry.LastIndexOf(':');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                continue;
            }

            var name = entry[..separator];
            var tag = entry[(separator + 1)..];
            if (!tags.TryGetValue(name, out var list))
            {
                list = [];
                tags[name] = list;
                order.Add(name);
            }

            list.Add(tag);
        }

        return order.Select(name => new TrackerGroup(name, tags[name])).ToList();
    }

    private static IReadOnlyList<string> SplitSet(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
