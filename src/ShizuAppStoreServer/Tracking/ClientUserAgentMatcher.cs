using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Tracking;

/// <summary>
/// Recognizes the ShizuStore client by its exact User-Agent prefix
/// (<c>ShizuStore/&lt;major&gt;.&lt;minor&gt;.&lt;patch&gt;</c>; debug builds append
/// <c>-&lt;commit&gt;</c>, which the prefix accepts). Case-sensitive on purpose:
/// anything else, including a missing header, counts as non-client traffic.
/// </summary>
public static partial class ClientUserAgentMatcher
{
    [GeneratedRegex(@"^ShizuStore/\d+\.\d+\.\d+")]
    public static partial Regex ClientUserAgentPattern();

    public static bool IsClient(string? userAgent) =>
        !string.IsNullOrEmpty(userAgent) && ClientUserAgentPattern().IsMatch(userAgent);
}
