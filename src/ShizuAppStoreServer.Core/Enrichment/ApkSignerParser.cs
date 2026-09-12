using System.Text.RegularExpressions;
using ShizuAppStoreServer.Core;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>One APK signer's certificate digests (all optional except presence).</summary>
public sealed record SignerCertificates(string? Sha256, string? Sha1, string? Md5);

public sealed class ApkSignerParseException(string message) : Exception(message);

/// <summary>
/// Parses <c>apksigner verify --print-certs</c> output. Collects every
/// <c>Signer #N certificate … digest:</c> line (key rotation yields several
/// signers); digests are normalized via <see cref="CertFingerprint"/>.
/// Older build-tools print SHA-256 + SHA-1 only — MD5 is optional per signer.
/// </summary>
public static partial class ApkSignerParser
{
    [GeneratedRegex(@"^Signer #(\d+) certificate (SHA-256|SHA-1|MD5) digest:\s*([0-9A-Fa-f:\s]+?)\s*$",
        RegexOptions.Multiline)]
    private static partial Regex DigestLine();

    public static IReadOnlyList<SignerCertificates> Parse(string output)
    {
        var bySigner = new SortedDictionary<int, Dictionary<string, string>>();
        foreach (Match match in DigestLine().Matches(output))
        {
            var index = int.Parse(match.Groups[1].Value);
            if (!bySigner.TryGetValue(index, out var digests))
            {
                digests = new Dictionary<string, string>(StringComparer.Ordinal);
                bySigner[index] = digests;
            }

            digests.TryAdd(match.Groups[2].Value, match.Groups[3].Value);
        }

        var signers = bySigner.Values.Select(d => new SignerCertificates(
            d.TryGetValue("SHA-256", out var sha256) ? CertFingerprint.Normalize(sha256) : null,
            d.TryGetValue("SHA-1", out var sha1) ? CertFingerprint.Normalize(sha1) : null,
            d.TryGetValue("MD5", out var md5) ? CertFingerprint.Normalize(md5) : null)).ToList();

        if (signers.Count == 0)
        {
            throw new ApkSignerParseException("No signer certificate digests in apksigner output.");
        }

        return signers;
    }
}
