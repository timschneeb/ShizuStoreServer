using System.Globalization;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Overrides;

/// <summary>
/// Registry of the App columns an <c>app_overrides</c> row may target. Values
/// are text and parsed here, so the applier never has to know individual
/// columns. Identity, scheduling, publish-gate and usage columns are
/// deliberately absent: overriding them would break matching or the delta feed.
/// </summary>
internal static class AppOverrideFields
{
    internal const string AvailabilityName = "availability";
    internal const string ExcludedReasonName = "excluded_reason";
    internal const string IconHashName = "icon_hash";
    internal const string AddedAtName = "added_at";
    internal const string ListUpdatedAtName = "list_updated_at";

    internal static readonly AppOverrideField AvailabilityField = new(
        AvailabilityName,
        AppOverrideOwnership.Visibility,
        app => app.Availability.ToString(),
        (app, value) => TryParseAvailability(value, out var parsed)
            ? Set(() => app.Availability = parsed)
            : "expected direct_apk, play_redirect, link_only or excluded");

    internal static readonly AppOverrideField ExcludedReasonField = new(
        ExcludedReasonName,
        AppOverrideOwnership.Visibility,
        app => app.ExcludedReason ?? string.Empty,
        (app, value) => Set(() => app.ExcludedReason = value.Length == 0 ? null : value));

    private static readonly Dictionary<string, AppOverrideField> Fields = new(StringComparer.Ordinal)
    {
        ["name"] = RequiredText("name", AppOverrideOwnership.List, app => app.Name, (app, value) => app.Name = value),
        ["description"] = RequiredText("description", AppOverrideOwnership.List, app => app.Description, (app, value) => app.Description = value),
        [AddedAtName] = RequiredTimestamp(AddedAtName, AppOverrideOwnership.List, app => app.AddedAt, (app, value) => app.AddedAt = value),
        // Served as listUpdatedAt, which drives the client's Recently added
        // row and the storefront's listed-age label.
        [ListUpdatedAtName] = Timestamp(ListUpdatedAtName, AppOverrideOwnership.List, app => app.ListUpdatedAt, (app, value) => app.ListUpdatedAt = value),
        ["display_name"] = OptionalText("display_name", AppOverrideOwnership.Enrichment, app => app.DisplayName, (app, value) => app.DisplayName = value),
        ["apk_label"] = OptionalText("apk_label", AppOverrideOwnership.Enrichment, app => app.ApkLabel, (app, value) => app.ApkLabel = value),
        ["license"] = OptionalText("license", AppOverrideOwnership.List, app => app.License, (app, value) => app.License = value),
        ["is_recommended"] = Bool("is_recommended", AppOverrideOwnership.List, app => app.IsRecommended, (app, value) => app.IsRecommended = value),
        ["has_paid"] = Bool("has_paid", AppOverrideOwnership.List, app => app.HasPaid, (app, value) => app.HasPaid = value),
        ["has_iap"] = Bool("has_iap", AppOverrideOwnership.List, app => app.HasIap, (app, value) => app.HasIap = value),
        ["has_ads"] = Bool("has_ads", AppOverrideOwnership.List, app => app.HasAds, (app, value) => app.HasAds = value),
        ["trial_days"] = Int("trial_days", AppOverrideOwnership.List, app => app.TrialDays, (app, value) => app.TrialDays = value),
        ["requires_root"] = Bool("requires_root", AppOverrideOwnership.List, app => app.RequiresRoot, (app, value) => app.RequiresRoot = value),
        ["source_url"] = OptionalText("source_url", AppOverrideOwnership.List, app => app.SourceUrl, (app, value) => app.SourceUrl = value),
        ["package_name"] = OptionalText("package_name", AppOverrideOwnership.Enrichment, app => app.PackageName, (app, value) => app.PackageName = value),
        ["permissions"] = LineList("permissions", AppOverrideOwnership.Enrichment, app => app.Permissions, (app, value) => app.Permissions = value),
        ["author_name"] = OptionalText("author_name", AppOverrideOwnership.Enrichment, app => app.AuthorName, (app, value) => app.AuthorName = value),
        ["author_url"] = OptionalText("author_url", AppOverrideOwnership.Enrichment, app => app.AuthorUrl, (app, value) => app.AuthorUrl = value),
        ["author_key"] = OptionalText("author_key", AppOverrideOwnership.Enrichment, app => app.AuthorKey, (app, value) => app.AuthorKey = value),
        ["full_description"] = OptionalText("full_description", AppOverrideOwnership.Enrichment, app => app.FullDescription, (app, value) => app.FullDescription = value),
        ["readme_url"] = OptionalText("readme_url", AppOverrideOwnership.Enrichment, app => app.ReadmeUrl, (app, value) => app.ReadmeUrl = value),
        ["changelog"] = OptionalText("changelog", AppOverrideOwnership.Enrichment, app => app.Changelog, (app, value) => app.Changelog = value),
        ["changelog_url"] = OptionalText("changelog_url", AppOverrideOwnership.Enrichment, app => app.ChangelogUrl, (app, value) => app.ChangelogUrl = value),
        ["store_url"] = OptionalText("store_url", AppOverrideOwnership.Enrichment, app => app.StoreUrl, (app, value) => app.StoreUrl = value),
        ["version_name"] = OptionalText("version_name", AppOverrideOwnership.Enrichment, app => app.VersionName, (app, value) => app.VersionName = value),
        ["version_updated_at"] = Timestamp("version_updated_at", AppOverrideOwnership.Enrichment, app => app.VersionUpdatedAt, (app, value) => app.VersionUpdatedAt = value),
        ["stars"] = Int("stars", AppOverrideOwnership.Enrichment, app => app.Stars, (app, value) => app.Stars = value),
        ["download_total"] = Long("download_total", AppOverrideOwnership.Enrichment, app => app.DownloadTotal, (app, value) => app.DownloadTotal = value),
        [IconHashName] = Hex64(IconHashName, AppOverrideOwnership.Enrichment, app => app.IconHash, (app, value) => app.IconHash = value),
        ["icon_adaptive"] = Bool("icon_adaptive", AppOverrideOwnership.Enrichment, app => app.IconAdaptive, (app, value) => app.IconAdaptive = value),
        ["screenshots"] = LineList("screenshots", AppOverrideOwnership.Enrichment, app => app.Screenshots, (app, value) => app.Screenshots = value),
        [AvailabilityName] = AvailabilityField,
        [ExcludedReasonName] = ExcludedReasonField,
    };

    internal static bool TryGet(string name, out AppOverrideField field) => Fields.TryGetValue(name, out field!);

    internal static bool IsVisibility(string name) =>
        name is AvailabilityName or ExcludedReasonName;

    // Operators write the column-style snake_case form (link_only); enum
    // names have no separator, so normalize before parsing.
    internal static bool TryParseAvailability(string value, out Availability availability) =>
        Enum.TryParse(value.Trim().Replace("_", string.Empty), ignoreCase: true, out availability)
        && Enum.IsDefined(availability);

    private static string? Set(Action action)
    {
        action();
        return null;
    }

    private static AppOverrideField RequiredText(
        string name, AppOverrideOwnership ownership, Func<App, string> get, Action<App, string> set) => new(
        name, ownership,
        app => get(app),
        (app, value) =>
        {
            if (value.Length == 0)
            {
                return "value is required";
            }
            set(app, value);
            return null;
        });

    private static AppOverrideField OptionalText(
        string name, AppOverrideOwnership ownership, Func<App, string?> get, Action<App, string?> set) => new(
        name, ownership,
        app => get(app) ?? string.Empty,
        (app, value) => Set(() => set(app, value.Length == 0 ? null : value)));

    private static AppOverrideField Bool(
        string name, AppOverrideOwnership ownership, Func<App, bool> get, Action<App, bool> set) => new(
        name, ownership,
        app => get(app) ? "true" : "false",
        (app, value) =>
        {
            if (!bool.TryParse(value.Trim(), out var parsed))
            {
                return "expected true or false";
            }
            set(app, parsed);
            return null;
        });

    private static AppOverrideField Int(
        string name, AppOverrideOwnership ownership, Func<App, int?> get, Action<App, int?> set) => new(
        name, ownership,
        app => get(app)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        (app, value) =>
        {
            if (value.Length == 0)
            {
                return Set(() => set(app, null));
            }
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return "expected an integer";
            }
            if (parsed < 0)
            {
                return "must not be negative";
            }
            set(app, parsed);
            return null;
        });

    private static AppOverrideField Long(
        string name, AppOverrideOwnership ownership, Func<App, long?> get, Action<App, long?> set) => new(
        name, ownership,
        app => get(app)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        (app, value) =>
        {
            if (value.Length == 0)
            {
                return Set(() => set(app, null));
            }
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return "expected an integer";
            }
            if (parsed < 0)
            {
                return "must not be negative";
            }
            set(app, parsed);
            return null;
        });

    private static AppOverrideField Timestamp(
        string name, AppOverrideOwnership ownership, Func<App, DateTimeOffset?> get, Action<App, DateTimeOffset?> set) => new(
        name, ownership,
        app => get(app)?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
        (app, value) =>
        {
            if (value.Length == 0)
            {
                return Set(() => set(app, null));
            }
            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return "expected an ISO-8601 timestamp";
            }
            set(app, parsed);
            return null;
        });

    private static AppOverrideField RequiredTimestamp(
        string name, AppOverrideOwnership ownership, Func<App, DateTimeOffset> get, Action<App, DateTimeOffset> set) => new(
        name, ownership,
        app => get(app).ToString("O", CultureInfo.InvariantCulture),
        (app, value) =>
        {
            if (value.Length == 0)
            {
                return "value is required";
            }
            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return "expected an ISO-8601 timestamp";
            }
            set(app, parsed);
            return null;
        });

    private static AppOverrideField Hex64(
        string name, AppOverrideOwnership ownership, Func<App, string?> get, Action<App, string?> set) => new(
        name, ownership,
        app => get(app) ?? string.Empty,
        (app, value) =>
        {
            if (value.Length == 0)
            {
                return Set(() => set(app, null));
            }
            if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            {
                return "expected 64 hex characters";
            }
            set(app, value.ToLowerInvariant());
            return null;
        });

    private static AppOverrideField LineList(
        string name, AppOverrideOwnership ownership, Func<App, List<string>> get, Action<App, List<string>> set) => new(
        name, ownership,
        app => string.Join('\n', get(app)),
        (app, value) =>
        {
            var items = value.Length == 0
                ? new List<string>()
                : value.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();
            set(app, items);
            return null;
        });
}
