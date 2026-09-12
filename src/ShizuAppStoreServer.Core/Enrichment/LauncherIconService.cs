using System.IO.Compression;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ShizuAppStoreServer.Core.Enrichment;

public interface ILauncherIconService
{
    /// <returns>Normalized icon, or null when nothing usable found.</returns>
    Task<ProcessedIcon?> ResolveAsync(string apkPath, BadgingInfo badging, CancellationToken ct = default);

    /// <summary>
    /// Batch half of icon resolution: stages the XML icon path (if any)
    /// into a shared batch dir under a unique prefix, without rendering.
    /// Null when the caller should resolve a raster instead.
    /// </summary>
    PendingBatchIcon? PrepareBatchRender(
        ZipArchive zip, byte[]? arsc, BadgingInfo badging, string batchWorkDir, string prefix);
}

/// <summary>
/// One batch entry: staged drawable name plus adaptive root file (null
/// for plain drawables). The caller stages many apps into one shared
/// dir (unique prefixes) and renders them with a single Gradle call.
/// </summary>
public sealed record PendingBatchIcon(string DrawableName, string? RootFile)
{
    // Only <adaptive-icon> roots stage beside res/ (DrawableStager keys
    // on the depth-0 root name), so a set RootFile means the icon is
    // truly adaptive; plain vectors stage under res/ and are not.
    public bool IsAdaptive => RootFile is not null;
}

/// <summary>
/// Launcher icon resolution without guessing file names: solid manifest
/// colors first, then the manifest <c>android:icon</c> reference resolved
/// through <c>resources.arsc</c>, then badging entries. XML drawables beat
/// rasters at every level (a real vector render stays sharp while a stale
/// PNG does not); anything that fails to stage or render falls through to
/// the next raster. XML rendering runs Google's LayoutLib via the Paparazzi
/// Gradle tool; a null renderer disables the XML path (unit tests) and
/// anything unexpected yields null while the caller falls back to an avatar.
/// </summary>
public sealed class LauncherIconService(IPaparazziRenderer? renderer = null) : ILauncherIconService
{
    public const int TargetSize = 192;

    /// <summary>Render size passed to Paparazzi, then normalized down.</summary>
    public const int RenderSize = 432;


    public async Task<ProcessedIcon?> ResolveAsync(string apkPath, BadgingInfo badging, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var zip = ZipFile.OpenRead(apkPath);
            byte[]? arsc = ZipEntryReader.Read(zip, "resources.arsc");

            if (ManifestIconRef(zip) is { } manifestRef)
            {
                if (TryParseHexColor(manifestRef, out var solid))
                {
                    using var flat = new Image<Rgba32>(TargetSize, TargetSize, solid);
                    return IconProcessor.FromImage(flat);
                }

                if (TryParseRefId(manifestRef, out var manifestId) && arsc is not null)
                {
                    if (ApkResourceTable.ResolveXml(arsc, manifestId) is { } xml
                        && await RenderStagedAsync(zip, arsc, xml, ct) is { } rendered)
                    {
                        return rendered;
                    }

                    if (ApkResourceTable.ResolveRaster(arsc, manifestId) is { } raster
                        && RenderRaster(zip, raster) is { } extracted)
                    {
                        return extracted;
                    }
                }
                else if (!manifestRef.StartsWith('@'))
                {
                    // Direct zip path: XML first, raster only when no
                    // drawable stages from it.
                    if (await RenderStagedAsync(zip, arsc, manifestRef, ct) is { } direct)
                    {
                        return direct;
                    }

                    if (RenderRaster(zip, manifestRef) is { } directRaster)
                    {
                        return directRaster;
                    }
                }
            }

            // No manifest hit: an XML path straight from badging (anydpi
            // adaptive file) renders as its own root before any raster.
            var xmlPath = badging.Icons
                .Where(i => i.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.Density)
                .Select(i => i.Path)
                .FirstOrDefault();
            if (xmlPath is not null
                && await RenderStagedAsync(zip, arsc, xmlPath, ct) is { } rootRendered)
            {
                return rootRendered;
            }

            if (IconProcessor.ExtractBestIcon(apkPath, badging) is { } fallback)
            {
                return fallback;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }

        return null;
    }

    private async Task<ProcessedIcon?> RenderStagedAsync(
        ZipArchive zip, byte[]? arsc, string apkPath, CancellationToken ct)
    {
        if (renderer is null
            || !apkPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var workDir = Path.Combine(Path.GetTempPath(), $"shizu-stage-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(workDir);
            var staged = DrawableStager.Stage(zip, arsc, apkPath, workDir);
            if (staged is null)
            {
                return null;
            }

            var png = await renderer.RenderAsync(staged.ResDir, staged.DrawableName, RenderSize, ct);
            return NormalizeRender(png, staged.RootFile is not null);
        }
        catch (Exception ex) when (ex is PaparazziException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Batch half of <see cref="RenderStagedAsync"/>: stages the XML icon
    /// path (if any) into a shared batch dir under a unique prefix, without
    /// rendering. Returns null when the caller should resolve a raster
    /// instead. Precedence mirrors <see cref="ResolveAsync"/> exactly
    /// (manifest XML, manifest raster, badging XML, badging raster): the
    /// batch refresh must pick the same icon single renders pick, or icons
    /// would flip between the two on alternating runs.
    /// </summary>
    public PendingBatchIcon? PrepareBatchRender(
        ZipArchive zip, byte[]? arsc, BadgingInfo badging, string batchWorkDir, string prefix)
    {
        try
        {
            if (renderer is null)
            {
                return null;
            }

            if (ManifestIconRef(zip) is { } manifestRef && !IsSolidColor(manifestRef))
            {
                string? manifestXml = null;
                if (TryParseRefId(manifestRef, out var manifestId) && arsc is not null)
                {
                    manifestXml = ApkResourceTable.ResolveXml(arsc, manifestId);
                    if (manifestXml is not null)
                    {
                        var staged = DrawableStager.Stage(zip, arsc, manifestXml, batchWorkDir, prefix);
                        if (staged is not null)
                        {
                            return new PendingBatchIcon(staged.DrawableName, staged.RootFile);
                        }
                    }

                    // Unstageable manifest XML still loses to a manifest
                    // raster, exactly like the single path.
                    if (ApkResourceTable.ResolveRaster(arsc, manifestId) is { } raster
                        && HasRaster(zip, raster))
                    {
                        return null;
                    }
                }
                else if (!manifestRef.StartsWith('@'))
                {
                    if (manifestRef.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        var staged = DrawableStager.Stage(zip, arsc, manifestRef, batchWorkDir, prefix);
                        if (staged is not null)
                        {
                            return new PendingBatchIcon(staged.DrawableName, staged.RootFile);
                        }
                    }

                    if (HasRaster(zip, manifestRef))
                    {
                        return null;
                    }
                }
            }

            // No manifest hit: an XML path straight from badging (anydpi
            // adaptive file) renders as its own root before any raster.
            var xmlPath = badging.Icons
                .Where(i => i.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.Density)
                .Select(i => i.Path)
                .FirstOrDefault();
            if (xmlPath is null)
            {
                return null;
            }

            var stagedRoot = DrawableStager.Stage(zip, arsc, xmlPath, batchWorkDir, prefix);
            return stagedRoot is null
                ? null
                : new PendingBatchIcon(stagedRoot.DrawableName, stagedRoot.RootFile);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool HasRaster(ZipArchive zip, string path) =>
        IconProcessor.IsRasterPath(path) && ZipEntryReader.Read(zip, path) is not null;

    private static bool IsSolidColor(string manifestRef)
    {
        var text = manifestRef.Trim();
        if (!text.StartsWith('#'))
        {
            return false;
        }

        var hex = text[1..];
        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        return hex.Length == 8
            && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// Batch half of <see cref="RenderStagedAsync"/>: normalizes rendered
    /// PNG bytes (exact-size square guard, then the standard 192px icon).
    /// Null when the bytes are not a usable image. Only renders of an
    /// <c>&lt;adaptive-icon&gt;</c> root count as adaptive (the caller
    /// passes whether the staged root was one); plain vectors render
    /// full-bleed but stay non-adaptive.
    /// </summary>
    public static ProcessedIcon? NormalizeRender(byte[] png, bool adaptive)
    {
        try
        {
            using var full = Image.Load<Rgba32>(png);
            // The Java side writes the exact requested square itself;
            // the crop only guards against a misbehaving renderer.
            var side = Math.Min(RenderSize, Math.Min(full.Width, full.Height));
            full.Mutate(x => x.Crop(new Rectangle(0, 0, side, side)));
            return IconProcessor.FromImage(full) is { } icon
                ? icon with { Adaptive = adaptive }
                : null;
        }
        catch (Exception ex) when (ex is UnknownImageFormatException
            or InvalidImageContentException
            or InvalidDataException
            or IOException
            or ArgumentException)
        {
            return null;
        }
    }

    private static ProcessedIcon? RenderRaster(ZipArchive zip, string path)
    {
        if (!IconProcessor.IsRasterPath(path))
        {
            return null;
        }

        var bytes = ZipEntryReader.Read(zip, path);
        return bytes is null ? null : IconProcessor.ProcessRawImage(bytes);
    }

    private static bool TryParseRefId(string reference, out uint id)
    {
        id = 0;
        return reference.StartsWith("(0x", StringComparison.Ordinal)
            && reference.EndsWith(')')
            && uint.TryParse(reference[3..^1],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    private static bool TryParseHexColor(string text, out Rgba32 color)
    {
        color = default;
        text = text.Trim();
        if (!text.StartsWith('#'))
        {
            return false;
        }

        var hex = text[1..];
        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        if (hex.Length != 8
            || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var argb))
        {
            return false;
        }

        color = new Rgba32(
            (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
        return true;
    }

    private static string? ManifestIconRef(ZipArchive zip)
    {
        var bytes = ZipEntryReader.Read(zip, "AndroidManifest.xml");
        if (bytes is null)
        {
            return null;
        }

        var manifest = BinaryXml.Parse(bytes);
        if (manifest?.Name != "manifest")
        {
            return null;
        }

        var app = manifest.Children.FirstOrDefault(c => c.Name == "application");
        if (app is null
            || !app.Attributes.TryGetValue("icon", out var icon)
            || string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        return icon.Trim();
    }
}
