using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Core.Enrichment.Apk;

/// <summary>One <c>application-icon[-density]:'path'</c> line. Density 0 = unqualified fallback line.</summary>
public sealed record BadgingIcon(int Density, string Path);

/// <summary>Parsed <c>aapt2 dump badging</c> output.</summary>
public sealed record BadgingInfo(
    string PackageName,
    long? VersionCode,
    string? VersionName,
    int? MinSdk,
    IReadOnlyList<BadgingIcon> Icons,
    IReadOnlyList<string> Permissions,
    string? Abi = null,
    string? ApplicationLabel = null)
{
    /// <summary>Required <c>uses-feature</c> names (never the not-required ones).</summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    /// <summary>Declared <c>targetSdkVersion</c>, when the output lists one.</summary>
    public int? TargetSdk { get; init; }

    /// <summary>Declared <c>compileSdkVersion</c> from the package line, when listed.</summary>
    public int? CompileSdk { get; init; }

    /// <summary>Declared resource locales; the <c>--_--</c> pseudo-locale is dropped.</summary>
    public IReadOnlyList<string> Locales { get; init; } = [];

    /// <summary>Every <c>native-code</c> ABI of the build; empty when it has no native code.</summary>
    public IReadOnlyList<string> Abis { get; init; } = [];

    /// <summary>
    /// Localized app names from the <c>application-label-&lt;locale&gt;</c> lines,
    /// keyed by resource qualifier. First line per locale wins.
    /// </summary>
    public IReadOnlyDictionary<string, string> LocalizedLabels { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Android TV build (leanback launcher or the legacy television type).</summary>
    public bool IsTvFormFactor =>
        Features.Contains(FeatureLeanback) || Features.Contains(FeatureTelevision);

    /// <summary>Wear OS build.</summary>
    public bool IsWearFormFactor => Features.Contains(FeatureWatch);

    private const string FeatureLeanback = "android.software.leanback";
    private const string FeatureTelevision = "android.hardware.type.television";
    private const string FeatureWatch = "android.hardware.type.watch";
}

public sealed class BadgingParseException(string message) : Exception(message);

/// <summary>
/// Parses <c>aapt2 dump badging</c> stdout. Pure static + tested against
/// canned real-world output; the exact format is stable across build-tools 30–36.
/// </summary>
public static partial class BadgingParser
{
    [GeneratedRegex(@"^package:\s+name='(?<name>[^']*)'\s+versionCode='(?<code>[^']*)'(?:\s+versionName='(?<vname>[^']*)')?", RegexOptions.Multiline)]
    private static partial Regex PackageLine();

    // minSdkVersion = build-tools 30+; sdkVersion = older output. Both seen in the wild.
    [GeneratedRegex(@"^(?:minSdkVersion|sdkVersion):'(?<sdk>\d+)'", RegexOptions.Multiline)]
    private static partial Regex SdkLine();

    [GeneratedRegex(@"^targetSdkVersion:'(?<sdk>\d+)'", RegexOptions.Multiline)]
    private static partial Regex TargetSdkLine();

    // compileSdkVersion is an attribute of the package line.
    [GeneratedRegex(@"^package:.*\bcompileSdkVersion='(?<sdk>\d+)'", RegexOptions.Multiline)]
    private static partial Regex CompileSdkLine();

    // `locales: '--_--' 'de' 'en' ...`; the pseudo-locale is not a real translation.
    [GeneratedRegex(@"^locales:(?<list>.*)$", RegexOptions.Multiline)]
    private static partial Regex LocalesLine();

    [GeneratedRegex(@"^application-icon(?:-(?<density>\d+))?:'(?<path>[^']*)'", RegexOptions.Multiline)]
    private static partial Regex IconLine();

    // Unqualified app display name; localized lines carry a config suffix.
    [GeneratedRegex(@"^application-label:'(?<label>[^']*)'", RegexOptions.Multiline)]
    private static partial Regex ApplicationLabelLine();

    [GeneratedRegex(@"^application-label-(?<lang>[^:']+):'(?<label>[^']*)'", RegexOptions.Multiline)]
    private static partial Regex LocalizedApplicationLabelLine();

    // aapt2 emits `uses-permission:` plus `uses-permission-sdk-23:` for
    // runtime-only declarations; both are requested permissions.
    [GeneratedRegex(@"^uses-permission(?:-sdk-\d+)?:\s+name='(?<name>[^']*)'", RegexOptions.Multiline)]
    private static partial Regex PermissionLine();

    // Single-ABI builds print one code; fat builds print several or none.
    [GeneratedRegex(@"^native-code:(?<codes>.*)$", RegexOptions.Multiline)]
    private static partial Regex NativeCodeLine();

    // `uses-feature:` is required; `uses-feature-not-required:` must not match,
    // so the anchor includes the colon and no optional suffix. aapt2 indents
    // these inside a `feature-group:` block.
    [GeneratedRegex(@"^\s*uses-feature:\s+name='(?<name>[^']*)'", RegexOptions.Multiline)]
    private static partial Regex RequiredFeatureLine();

    [GeneratedRegex(@"'(?<abi>[^']+)'")]
    private static partial Regex AbiToken();

    private const string PseudoLocale = "--_--";

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

        var versionName = package.Groups["vname"] is { Success: true } v && v.Value.Length > 0
            ? VersionNames.Sanitize(v.Value)
            : null;

        int? minSdk = null;
        if (SdkLine().Match(output) is { Success: true } sdk
            && int.TryParse(sdk.Groups["sdk"].Value, out var sdkNum))
        {
            minSdk = sdkNum;
        }

        int? targetSdk = null;
        if (TargetSdkLine().Match(output) is { Success: true } target
            && int.TryParse(target.Groups["sdk"].Value, out var targetNum))
        {
            targetSdk = targetNum;
        }

        int? compileSdk = null;
        if (CompileSdkLine().Match(output) is { Success: true } compile
            && int.TryParse(compile.Groups["sdk"].Value, out var compileNum))
        {
            compileSdk = compileNum;
        }

        var locales = LocalesLine().Matches(output)
            .SelectMany(m => AbiToken().Matches(m.Groups["list"].Value))
            .Select(m => m.Groups["abi"].Value)
            .Where(l => l.Length > 0 && l != PseudoLocale)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var icons = IconLine().Matches(output)
            .Select(m => new BadgingIcon(
                m.Groups["density"] is { Success: true } d && int.TryParse(d.Value, out var dens) ? dens : 0,
                m.Groups["path"].Value))
            .Where(i => i.Path.Length > 0)
            .Distinct()
            .ToList();

        var permissions = PermissionLine().Matches(output)
            .Select(m => m.Groups["name"].Value)
            .Where(p => p.Length > 0)
            .Distinct()
            .ToList();

        var features = RequiredFeatureLine().Matches(output)
            .Select(m => m.Groups["name"].Value)
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var abis = NativeCodeLine().Matches(output)
            .SelectMany(m => AbiToken().Matches(m.Groups["codes"].Value))
            .Select(m => m.Groups["abi"].Value)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // A single native ABI names the build; several means a fat APK that runs anywhere.
        var abi = abis.Count == 1 ? abis[0] : null;

        var localizedLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in LocalizedApplicationLabelLine().Matches(output))
        {
            var locale = match.Groups["lang"].Value;
            // A label that spans lines would break the newline-joined storage
            // encoding, so collapse it to a single line.
            var value = match.Groups["label"].Value.ReplaceLineEndings(" ").Trim();
            if (locale.Length > 0 && value.Length > 0)
            {
                localizedLabels.TryAdd(locale, value);
            }
        }

        var label = ApplicationLabelLine().Match(output) is { Success: true } direct && direct.Groups["label"].Value.Length > 0
            ? direct.Groups["label"].Value
            : localizedLabels.Values.FirstOrDefault();

        return new BadgingInfo(package.Groups["name"].Value, versionCode, versionName, minSdk, icons, permissions, abi, label)
        {
            Features = features,
            TargetSdk = targetSdk,
            CompileSdk = compileSdk,
            Locales = locales,
            Abis = abis,
            LocalizedLabels = localizedLabels,
        };
    }
}
