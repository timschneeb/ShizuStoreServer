using System.IO.Compression;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// One Exodus tracker matched in an APK's DEX code. <c>Tags</c> are the
/// tracker's Exodus categories (Analytics, Advertisement, Location, ...) as
/// the catalog lists them.
/// </summary>
public sealed record TrackerHit(int Id, string Name, string Signature, IReadOnlyList<string> Tags);

/// <summary>
/// Static Exodus tracker detection over the APK's <c>classes*.dex</c> string
/// pools. Only code signatures are matched, because the server does no
/// dynamic traffic analysis; a tracker found only through its network
/// signature stays undetected. Absence of a hit means "not detected by code
/// signature", never "tracker-free".
/// </summary>
public static class TrackerScanner
{
    /// <summary>Per-dex-entry decode cap; real <c>classes.dex</c> files are far smaller.</summary>
    private const long MaxDexEntryBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Matches the catalog against the APK's DEX text. Best-effort: an APK
    /// that cannot be opened scans as no hits (callers treat the tracker list
    /// as a garnish signal).
    /// </summary>
    public static IReadOnlyList<TrackerHit> ScanApk(string apkPath, IReadOnlyList<TrackerSignature> catalog)
    {
        if (catalog.Count == 0)
        {
            return [];
        }

        try
        {
            using var archive = ZipFile.OpenRead(apkPath);
            List<string>? text = null;
            foreach (var entry in archive.Entries)
            {
                if (!IsDexEntry(entry) || entry.Length > MaxDexEntryBytes)
                {
                    continue;
                }

                (text ??= []).Add(Decode(entry));
            }

            return text is null ? [] : ScanText(text, catalog);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Pure matcher over already-decoded DEX text slices.</summary>
    public static IReadOnlyList<TrackerHit> ScanText(
        IReadOnlyList<string> dexText,
        IReadOnlyList<TrackerSignature> catalog)
    {
        List<TrackerHit>? hits = null;
        foreach (var tracker in catalog)
        {
            if (MostSpecificMatch(dexText, tracker.CodeSignature) is { } signature)
            {
                (hits ??= []).Add(new TrackerHit(
                    tracker.Id, tracker.Name, signature, tracker.Categories));
            }
        }

        return hits ?? [];
    }

    /// <summary>
    /// Most specific code signature of the tracker present in the DEX text.
    /// A signature is a <c>|</c>-joined list of class-name prefixes: a plain
    /// <c>com.foo.bar</c> matches classes in that package (DEX token
    /// <c>Lcom/foo/bar</c>), while a leading dot is a relative class name
    /// (<c>.LigatusManager</c> becomes token <c>/LigatusManager</c>) that
    /// matches in any package. Returns the matched raw signature for evidence;
    /// when several alternatives match, the longest one wins so the evidence
    /// names the narrowest package that is actually present.
    /// </summary>
    private static string? MostSpecificMatch(IReadOnlyList<string> dexText, string codeSignature)
    {
        var signatures = codeSignature.Split(
            '|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? best = null;
        foreach (var raw in signatures)
        {
            if (best is not null && raw.Length <= best.Length)
            {
                continue;
            }

            var token = SignatureToken(raw);
            if (token.Length == 0)
            {
                continue;
            }

            foreach (var text in dexText)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                {
                    best = raw;
                    break;
                }
            }
        }

        return best;
    }

    private static string SignatureToken(string signature) =>
        signature.StartsWith('.')
            ? "/" + signature[1..].Replace('.', '/')
            : "L" + signature.Replace('.', '/');

    private static bool IsDexEntry(ZipArchiveEntry entry) =>
        entry.FullName.StartsWith("classes", StringComparison.Ordinal)
        && entry.FullName.EndsWith(".dex", StringComparison.Ordinal);

    private static string Decode(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        stream.CopyTo(buffer);
        var bytes = buffer.GetBuffer();
        var length = (int)buffer.Length;
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)bytes[i];
        }

        return new string(chars);
    }
}
