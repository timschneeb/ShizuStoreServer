using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>One <c>application-icon[-density]:'path'</c> line. Density 0 = unqualified fallback line.</summary>
public sealed record BadgingIcon(int Density, string Path);

/// <summary>Parsed <c>aapt2 dump badging</c> output.</summary>
public sealed record BadgingInfo(
    string PackageName,
    long? VersionCode,
    string? VersionName,
    int? MinSdk,
    IReadOnlyList<BadgingIcon> Icons);

public sealed class BadgingParseException(string message) : Exception(message);

/// <summary>
/// Parses <c>aapt2 dump badging</c> stdout. Pure static + tested against
/// canned real-world output; the exact format is stable across build-tools
/// 30–36 (verified locally with 35.0.0, see <c>docs/server-setup.md</c>).
/// </summary>
public static partial class BadgingParser
{
    [GeneratedRegex(@"^package:\s+name='(?<name>[^']*)'\s+versionCode='(?<code>[^']*)'(?:\s+versionName='(?<vname>[^']*)')?", RegexOptions.Multiline)]
    private static partial Regex PackageLine();

    // minSdkVersion = build-tools 30+; sdkVersion = older output. Both seen in the wild.
    [GeneratedRegex(@"^(?:minSdkVersion|sdkVersion):'(?<sdk>\d+)'", RegexOptions.Multiline)]
    private static partial Regex SdkLine();

    [GeneratedRegex(@"^application-icon(?:-(?<density>\d+))?:'(?<path>[^']*)'", RegexOptions.Multiline)]
    private static partial Regex IconLine();

    public static BadgingInfo Parse(string output)
    {
        var package = PackageLine().Match(output);
        if (!package.Success)
        {
            throw new BadgingParseException("No 'package:' line in aapt2 dump badging output.");
        }

        long? versionCode = null;
        if (long.TryParse(package.Groups["code"].Value, out var code))
        {
            versionCode = code;
        }

        var versionName = package.Groups["vname"] is { Success: true } v && v.Value.Length > 0 ? v.Value : null;

        int? minSdk = null;
        if (SdkLine().Match(output) is { Success: true } sdk
            && int.TryParse(sdk.Groups["sdk"].Value, out var sdkNum))
        {
            minSdk = sdkNum;
        }

        var icons = IconLine().Matches(output)
            .Select(m => new BadgingIcon(
                m.Groups["density"] is { Success: true } d && int.TryParse(d.Value, out var dens) ? dens : 0,
                m.Groups["path"].Value))
            .Where(i => i.Path.Length > 0)
            .Distinct()
            .ToList();

        return new BadgingInfo(package.Groups["name"].Value, versionCode, versionName, minSdk, icons);
    }
}
