using System.IO.Compression;
using System.Text;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// Stages one drawable tree for the Paparazzi renderer: binary AXML from
/// the APK becomes text XML under a flat generated namespace, referenced
/// rasters are copied alongside. LayoutLib links real Android resources,
/// so every reference must either inline to a literal or point at a staged
/// file. Anything unresolvable (framework refs, theme attrs, missing
/// files) aborts the whole stage and the caller falls back to an avatar.
/// An adaptive-icon root is written beside res/ instead of into it:
/// LayoutLib cannot inflate AdaptiveIconDrawable (no device mask string
/// off-device), Paparazzi pre-parses every res XML and fails the render on
/// it, so the Java side reads that one file straight from disk and only
/// its layers resolve as resources.
/// </summary>
public sealed class DrawableStager
{
    private const int MaxFiles = 64;
    private const int MaxDepth = 6;
    private const long MaxTotalBytes = 8 * 1024 * 1024;

    public sealed record StagedDrawable(string ResDir, string DrawableName, string? RootFile);

    private readonly ZipArchive _zip;
    private readonly byte[]? _arsc;
    private readonly string _resDir;
    private readonly string _workDir;
    private readonly string _prefix;
    private readonly Dictionary<string, string> _staged = new(StringComparer.Ordinal);
    private int _xmlCount;
    private int _rasterCount;
    private long _totalBytes;

    private DrawableStager(ZipArchive zip, byte[]? arsc, string resDir, string workDir, string prefix)
    {
        _zip = zip;
        _arsc = arsc;
        _resDir = resDir;
        _workDir = workDir;
        _prefix = prefix;
    }

    /// <returns>Staged dir plus drawable name, or null when not stageable.</returns>
    public static StagedDrawable? Stage(
        ZipArchive zip, byte[]? arsc, string rootPath, string workDir, string prefix = "shizu")
    {
        try
        {
            var resDir = Path.Combine(workDir, "res");
            Directory.CreateDirectory(Path.Combine(resDir, "drawable"));
            Directory.CreateDirectory(Path.Combine(resDir, "drawable-nodpi"));
            var stager = new DrawableStager(zip, arsc, resDir, workDir, prefix);
            var name = stager.StageDrawable(rootPath, 0);
            if (name is null)
            {
                return null;
            }

            // Adaptive roots live beside res/ (see RootTargetDir); the batch
            // renderer needs their path, the single renderer re-derives it.
            var rootFile = Path.Combine(workDir, name + ".xml");
            return new StagedDrawable(resDir, name, File.Exists(rootFile) ? rootFile : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? StageDrawable(string apkPath, int depth)
    {
        if (depth > MaxDepth || _xmlCount >= MaxFiles)
        {
            return null;
        }

        if (_staged.TryGetValue(apkPath, out var existing))
        {
            return existing;
        }

        var bytes = ZipEntryReader.Read(_zip, apkPath);
        var root = bytes is null ? null : BinaryXml.Parse(bytes, _arsc);
        if (root is null)
        {
            return null;
        }

        if (depth == 0 && root.Name == "adaptive-icon")
        {
            NormalizeAdaptiveRoot(root);
        }

        // Reserve the name before recursing so reference cycles terminate.
        // The prefix keeps batch renders collision-free: many apps stage
        // into one shared res tree, each under its own app-id prefix.
        var name = $"{_prefix}_{_xmlCount++}";
        _staged[apkPath] = name;
        try
        {
            var xml = RenderNode(root, depth);
            var text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + xml;
            var fileBytes = Encoding.UTF8.GetBytes(text);
            _totalBytes += fileBytes.Length;
            if (_totalBytes > MaxTotalBytes)
            {
                return null;
            }

            File.WriteAllBytes(RootTargetDir(root.Name, depth, name), fileBytes);
            return name;
        }
        catch (UnstageableException)
        {
            _staged.Remove(apkPath);
            return null;
        }
    }

    /// <summary>Normalizes an adaptive-icon root before text rendering.
    /// Single-child <c>inset</c> layers stay intact: the renderer honors
    /// their padding device-exactly (a 24dp inset on the 108dp viewport
    /// draws the layer at 60dp centered, as launchers do), and the
    /// existing recursion already stages the inner drawable. Other
    /// single-child wrappers lift the inner drawable onto the layer,
    /// since the renderer would otherwise resolve the layer to nothing
    /// and drop it. A missing background (valid: foreground-only
    /// adaptive icons) would likewise render transparent; launchers show
    /// those over a light surface, so default to white for the store
    /// PNG.</summary>
    public static void NormalizeAdaptiveRoot(XmlTreeNode root)
    {
        if (root.Name != "adaptive-icon")
        {
            return;
        }

        foreach (var layer in root.Children)
        {
            var hops = 0;
            while (!layer.Attributes.ContainsKey("drawable")
                && layer.Children.Count == 1
                && layer.Children[0].Name != "inset"
                && hops++ < 4)
            {
                if (!layer.Children[0].Attributes.TryGetValue("drawable", out var drawable))
                {
                    break;
                }

                layer.Attributes["drawable"] = drawable;
                layer.Children.Clear();
            }
        }

        if (!root.Children.Any(c => c.Name == "background"))
        {
            var bg = new XmlTreeNode("background");
            bg.Attributes["drawable"] = "#FFFFFFFF";
            root.Children.Insert(0, bg);
        }
    }

    /// <summary>Adaptive-icon roots live beside res/ (see class comment);
    /// everything else is a linked resource under res/drawable.</summary>
    private string RootTargetDir(string rootName, int depth, string name) =>
        depth == 0 && rootName == "adaptive-icon"
            ? Path.Combine(_workDir, name + ".xml")
            : Path.Combine(_resDir, "drawable", name + ".xml");

    private string RenderNode(XmlTreeNode node, int depth)
    {
        var sb = new StringBuilder();
        sb.Append('<').Append(node.Name);
        // Binary XML pools local names, so every attribute is assumed to be
        // an android: one (true for drawable XML outside rare compat libs).
        // A wrong guess fails the Gradle link and falls back to an avatar.
        sb.Append(" xmlns:android=\"http://schemas.android.com/apk/res/android\"");

        foreach (var (attr, raw) in node.Attributes)
        {
            sb.Append(" android:").Append(attr).Append("=\"")
                .Append(Escape(RewriteValue(raw, depth))).Append('"');
        }

        if (node.Children.Count == 0)
        {
            sb.Append(" />");
            return sb.ToString();
        }

        sb.Append('>');
        foreach (var child in node.Children)
        {
            sb.Append(RenderNode(child, depth));
        }

        sb.Append("</").Append(node.Name).Append('>');
        return sb.ToString();
    }

    private string RewriteValue(string raw, int depth)
    {
        raw = raw.Trim();
        if (TryParseHexRef(raw, out var id))
        {
            return RewriteRef(id, depth);
        }

        if (raw.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            && _zip.GetEntry(raw) is not null)
        {
            return "@drawable/" + (StageDrawable(raw, depth + 1)
                ?? throw new UnstageableException());
        }

        if (IconProcessor.IsRasterPath(raw) && _zip.GetEntry(raw) is not null)
        {
            return "@drawable/" + (StageRaster(raw) ?? throw new UnstageableException());
        }

        if (raw.StartsWith('@') || raw.StartsWith('?'))
        {
            throw new UnstageableException();
        }

        return raw;
    }

    private string RewriteRef(uint id, int depth)
    {
        if (_arsc is null)
        {
            throw new UnstageableException();
        }

        var resolved = ApkResourceTable.ResolveString(_arsc, id, allowXml: true);
        if (resolved is null)
        {
            throw new UnstageableException();
        }

        return RewriteValue(resolved, depth);
    }

    private string? StageRaster(string apkPath)
    {
        if (_staged.TryGetValue(apkPath, out var existing))
        {
            return existing;
        }

        if (_rasterCount >= MaxFiles)
        {
            return null;
        }

        var bytes = ZipEntryReader.Read(_zip, apkPath);
        if (bytes is null)
        {
            return null;
        }

        _totalBytes += bytes.Length;
        if (_totalBytes > MaxTotalBytes)
        {
            return null;
        }

        var name = $"{_prefix}_r{_rasterCount++}";
        _staged[apkPath] = name;
        File.WriteAllBytes(
            Path.Combine(_resDir, "drawable-nodpi", name + Path.GetExtension(apkPath)), bytes);
        return name;
    }

    private static bool TryParseHexRef(string raw, out uint id)
    {
        id = 0;
        return raw.StartsWith("(0x", StringComparison.Ordinal)
            && raw.EndsWith(')')
            && uint.TryParse(raw[3..^1],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal);

    private sealed class UnstageableException : Exception;
}

/// <summary>Shared zip entry read (case-insensitive fallback).</summary>
internal static class ZipEntryReader
{
    internal static byte[]? Read(ZipArchive zip, string path)
    {
        try
        {
            var entry = zip.GetEntry(path)
                ?? zip.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }
}
