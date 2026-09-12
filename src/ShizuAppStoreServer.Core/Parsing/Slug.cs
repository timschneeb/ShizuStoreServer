using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Core.Parsing;

public static partial class Slug
{
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    public static string Slugify(string value)
    {
        var slug = NonAlphanumeric().Replace(value.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "unnamed" : slug;
    }
}
