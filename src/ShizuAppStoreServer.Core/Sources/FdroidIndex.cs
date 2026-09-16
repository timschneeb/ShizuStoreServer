using System.Text;
using System.Xml;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>Known F-Droid-compatible repos and their <c>index.xml</c> layout.</summary>
public static class FdroidRepos
{
    public const string FDroidBase = "https://f-droid.org/repo/";
    public const string IzzyBase = "https://apt.izzysoft.de/fdroid/repo/";

    public static string BaseFor(SourceKind kind) =>
        kind == SourceKind.Izzy ? IzzyBase : FDroidBase;

    /// <summary>Icon download candidates: high-density first, legacy fallback second.</summary>
    public static IReadOnlyList<string> IconUrls(string repoBase, string iconFile) =>
    [
        $"{repoBase.TrimEnd('/')}/icons-640/{iconFile}",
        $"{repoBase.TrimEnd('/')}/icons/{iconFile}",
    ];
}

/// <summary>Latest package entry for one app in a repo <c>index.xml</c>.</summary>
public sealed record FdroidPackageInfo(
    string PackageName,
    long VersionCode,
    string? VersionName,
    string ApkName,
    string? Sha256,
    long? Size,
    int? MinSdk,
    string? IconFile,
    /// <summary>Signing-cert MD5 from <c>&lt;sig&gt;</c> (matches apksigner MD5).</summary>
    string? SigMd5,
    /// <summary>Upstream source repo URL from the application-level <c>&lt;source&gt;</c>.</summary>
    string? SourceUrl = null,
    /// <summary>Native ABI from <c>&lt;nativecode&gt;</c>; null for fat/universal builds.</summary>
    string? Abi = null,
    /// <summary>Long description from the application-level <c>&lt;desc&gt;</c> (HTML).</summary>
    string? LongDescription = null);

/// <summary>
/// Streaming parser for F-Droid repo <c>index.xml</c> (v1 format): collects
/// every <c>&lt;package&gt;</c> per <c>&lt;application&gt;</c> in document
/// order (newest versionCode first). A release can ship one APK per
/// architecture, so all siblings are kept and the caller decides which to
/// analyze. Real indexes carry <c>version</c>/<c>versioncode</c> as child
/// elements (package attributes are accepted as a fallback), plus
/// <c>apkname</c>, <c>hash</c>, <c>size</c>, <c>sdkver</c> (min SDK),
/// <c>sig</c> (signing-cert MD5), <c>nativecode</c> and the top-level
/// <c>&lt;icon&gt;</c>/<c>&lt;source&gt;</c>/<c>&lt;desc&gt;</c>. Unknown
/// elements are ignored, so <c>&lt;localized&gt;</c> blocks and future fields
/// don't break parsing.
/// </summary>
public static class FdroidIndexParser
{
    public static IReadOnlyDictionary<string, IReadOnlyList<FdroidPackageInfo>> Parse(Stream xml)
    {
        var result = new Dictionary<string, IReadOnlyList<FdroidPackageInfo>>(StringComparer.Ordinal);
        using var reader = XmlReader.Create(xml, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "application")
            {
                var id = reader.GetAttribute("id");
                if (string.IsNullOrEmpty(id) || result.ContainsKey(id) || reader.IsEmptyElement)
                {
                    continue;
                }

                if (ReadApplication(reader, id) is { Count: > 0 } packages)
                {
                    result[id] = packages;
                }
            }
        }

        return result;
    }

    private static List<FdroidPackageInfo> ReadApplication(XmlReader reader, string id)
    {
        var depth = reader.Depth;
        string? icon = null;
        string? source = null;
        string? desc = null;
        var packages = new List<FdroidPackageInfo>();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 && !reader.IsEmptyElement)
            {
                if (reader.Name == "icon" && icon is null)
                {
                    icon = ReadLeafText(reader);
                }
                else if (reader.Name == "source" && source is null)
                {
                    source = ReadLeafText(reader);
                }
                else if (reader.Name == "desc" && desc is null)
                {
                    desc = ReadLeafText(reader);
                }
                else if (reader.Name == "package" && ReadPackage(reader, id) is { } package)
                {
                    packages.Add(package);
                }
            }
        }

        // The application-level icon/source/desc are shared by every package,
        // and may appear before or after the packages.
        if (string.IsNullOrWhiteSpace(icon) && string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(desc))
        {
            return packages;
        }

        for (var i = 0; i < packages.Count; i++)
        {
            packages[i] = packages[i] with
            {
                IconFile = string.IsNullOrWhiteSpace(icon) ? packages[i].IconFile : icon,
                SourceUrl = string.IsNullOrWhiteSpace(source) ? packages[i].SourceUrl : source,
                LongDescription = string.IsNullOrWhiteSpace(desc) ? packages[i].LongDescription : desc,
            };
        }

        return packages;
    }

    private static FdroidPackageInfo? ReadPackage(XmlReader reader, string id)
    {
        var versionName = NullIfBlank(reader.GetAttribute("version"));
        long? versionCode = long.TryParse(reader.GetAttribute("versioncode"), out var versionCodeAttr)
            ? versionCodeAttr
            : null;
        if (reader.IsEmptyElement)
        {
            return null;
        }

        var depth = reader.Depth;
        string? apkName = null;
        string? hash = null;
        long? size = null;
        int? minSdk = null;
        string? sigMd5 = null;
        string? abi = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 && !reader.IsEmptyElement)
            {
                switch (reader.Name)
                {
                    case "version":
                        versionName ??= NullIfBlank(ReadLeafText(reader));
                        break;
                    case "versioncode":
                        if (versionCode is null && long.TryParse(ReadLeafText(reader), out var vc))
                        {
                            versionCode = vc;
                        }

                        break;
                    case "apkname":
                        apkName ??= ReadLeafText(reader);
                        break;
                    case "hash":
                        hash ??= ReadLeafText(reader);
                        break;
                    case "size":
                        if (size is null && long.TryParse(ReadLeafText(reader), out var s))
                        {
                            size = s;
                        }

                        break;
                    case "sdkver":
                        if (minSdk is null && int.TryParse(ReadLeafText(reader), out var m))
                        {
                            minSdk = m;
                        }

                        break;
                    case "sig":
                        sigMd5 ??= CertFingerprint.Normalize(ReadLeafText(reader));
                        break;
                    case "nativecode":
                        abi ??= NullIfBlank(ReadLeafText(reader));
                        break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(apkName))
        {
            return null;
        }

        return new FdroidPackageInfo(
            id,
            versionCode ?? 0,
            versionName,
            apkName,
            string.IsNullOrWhiteSpace(hash) ? null : hash,
            size,
            minSdk,
            IconFile: null,
            SigMd5: sigMd5,
            Abi: abi);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Reads a leaf element's inner text. Unlike
    /// <c>ReadElementContentAsString</c> (which parks the reader <i>on</i>
    /// the next sibling start, causing the caller's <c>Read()</c> loop to
    /// skip it), this leaves the reader on the element's own end tag, so
    /// sibling detection keeps working.
    /// </summary>
    private static string ReadLeafText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        var depth = reader.Depth;
        var sb = new StringBuilder();
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
            {
                sb.Append(reader.Value);
            }
        }

        return sb.ToString().Trim();
    }
}
