using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Tracking;

/// <summary>
/// Recognizes the ShizuStore clients by their exact User-Agent prefix:
/// <c>ShizuStore/&lt;major&gt;.&lt;minor&gt;.&lt;patch&gt;</c> (debug builds use the
/// app name <c>ShizuStore (Debug)</c>, nightly builds append <c>-&lt;commit&gt;</c>,
/// both accepted here) and the web frontend's <c>ShizuStoreWeb/...</c>.
/// Case-sensitive on purpose: anything else, including a missing header,
/// counts as non-client traffic.
/// </summary>
public static partial class ClientUserAgentMatcher
{
    [GeneratedRegex(@"^(?:ShizuStore(?: \(Debug\))?/\d+\.\d+\.\d+|ShizuStoreWeb/)")]
    public static partial Regex ClientUserAgentPattern();

    public static bool IsClient(string? userAgent) =>
        !string.IsNullOrEmpty(userAgent) && ClientUserAgentPattern().IsMatch(userAgent);
}
