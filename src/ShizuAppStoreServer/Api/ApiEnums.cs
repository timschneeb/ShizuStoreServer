using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Api;

/// <summary>
/// snake_case wire representation of the <c>Core.Data</c> enums. The DB
/// stores the PascalCase member names (<c>HasConversion&lt;string&gt;</c>);
/// the public API uses lowercase snake_case instead, so both directions
/// are mapped explicitly here (single source of truth for controllers).
/// </summary>
public static class ApiEnums
{
    public static string ToApiString(Listing v) => v switch
    {
        Listing.Main => "main",
        Listing.ClosedSource => "closed_source",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null),
    };

    public static string ToApiString(AppType v) => v switch
    {
        AppType.App => "app",
        AppType.Library => "library",
        AppType.Flow => "flow",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null),
    };

    public static string ToApiString(CategorySection v) => v switch
    {
        CategorySection.Apps => "apps",
        CategorySection.Libraries => "libraries",
        CategorySection.Misc => "misc",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null),
    };

    public static string ToApiString(SourceKind v) => v switch
    {
        SourceKind.GitHub => "github",
        SourceKind.GitLab => "gitlab",
        SourceKind.Codeberg => "codeberg",
        SourceKind.FDroid => "fdroid",
        SourceKind.Izzy => "izzy",
        SourceKind.Play => "play",
        SourceKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null),
    };

    public static string ToApiString(Availability v) => v switch
    {
        Availability.DirectApk => "direct_apk",
        Availability.PlayRedirect => "play_redirect",
        Availability.LinkOnly => "link_only",
        Availability.Excluded => "excluded",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null),
    };

    public static bool TryParseListing(string? s, out Listing v) => TryParse(s, out v);
    public static bool TryParseAppType(string? s, out AppType v) => TryParse(s, out v);
    public static bool TryParseAvailability(string? s, out Availability v) => TryParse(s, out v);

    private static bool TryParse<T>(string? s, out T v) where T : struct, Enum
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        foreach (var name in Enum.GetNames<T>())
        {
            var candidate = (T)Enum.Parse<T>(name);
            if (ToApiStringBoxed(candidate).Equals(s.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                v = candidate;
                return true;
            }
        }

        return false;
    }

    private static string ToApiStringBoxed<T>(T v) where T : struct, Enum => v switch
    {
        Listing l => ToApiString(l),
        AppType t => ToApiString(t),
        CategorySection s => ToApiString(s),
        SourceKind k => ToApiString(k),
        Availability a => ToApiString(a),
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null),
    };
}
