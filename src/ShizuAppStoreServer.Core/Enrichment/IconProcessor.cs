using System.IO.Compression;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>Normalized icon PNG + its sha256 (doubles as the <c>/icons/{sha256}.png</c> filename).</summary>
public sealed record ProcessedIcon(string Sha256, byte[] Png, bool Adaptive = false);

/// <summary>
/// Extracts the best-density raster icon from an APK and normalizes it to a
/// ≤192px PNG (PNG and WebP both decode). Returns null when the badging
/// icon paths yield no raster (XML-only icons, missing entry, corrupt
/// image): callers try the mipmap table next, then the letter-avatar.
/// </summary>
public static class IconProcessor
{
    public const int TargetSize = 192;

    /// <summary>Raster extensions ImageSharp decodes (WebP included).</summary>
    public static bool IsRasterPath(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes raw image bytes (F-Droid repo icons) to a ≤192px PNG.
    /// Returns null for corrupt/unknown formats — the caller falls back to
    /// <see cref="LetterAvatarGenerator"/>.
    /// </summary>
    public static ProcessedIcon? ProcessRawImage(byte[] bytes)
    {
        try
        {
            using var image = Image.Load(bytes);
            return Normalize(image);
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

    public static ProcessedIcon? ExtractBestIcon(string apkPath, BadgingInfo badging)
    {
        var candidates = badging.Icons
            .Where(i => IsRasterPath(i.Path))
            .OrderByDescending(i => i.Density)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        using var zip = ZipFile.OpenRead(apkPath);
        foreach (var candidate in candidates)
        {
            try
            {
                var entry = zip.GetEntry(candidate.Path)
                    ?? zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.FullName, candidate.Path, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    continue;
                }

                using var stream = entry.Open();
                using var image = Image.Load(stream);
                return Normalize(image);
            }
            catch (Exception ex) when (ex is UnknownImageFormatException
                or InvalidImageContentException
                or InvalidDataException
                or IOException)
            {
                // Try the next lower density.
            }
        }

        return null;
    }

    /// <summary>Normalizes an in-memory image (rendered vectors, decoded layers).</summary>
    public static ProcessedIcon FromImage(Image image) => Normalize(image);

    /// <summary>Shrink-to-fit (never upscale) + PNG encode + sha256.</summary>
    private static ProcessedIcon Normalize(Image image)
    {
        if (image.Width > TargetSize || image.Height > TargetSize)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(TargetSize, TargetSize),
                Mode = ResizeMode.Max,
            }));
        }

        using var png = new MemoryStream();
        image.Save(png, new PngEncoder());
        var bytes = png.ToArray();
        return new ProcessedIcon(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
    }
}
