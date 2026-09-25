using System.Text.RegularExpressions;
using ShizuAppStoreServer.Core;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>One APK signer's certificate facts (digests optional except presence).</summary>
public sealed record SignerCertificates(
    string? Sha256,
    string? Sha1,
    string? Md5,
    string? Dn = null,
    string? KeyAlgorithm = null,
    int? KeySizeBits = null);

/// <summary>
/// Parsed <c>apksigner verify --print-certs</c> output: every signer (key
/// rotation yields several) plus the signature schemes that verified.
/// </summary>
public sealed record ApkSignerInfo(IReadOnlyList<SignerCertificates> Signers, IReadOnlyList<string> Schemes)
{
    public static ApkSignerInfo None { get; } = new([], []);

    /// <summary>Display form of <see cref="Schemes"/>, e.g. <c>v1+v2+v3</c>; null when none verified.</summary>
    public string? Scheme => Schemes.Count == 0 ? null : string.Join('+', Schemes);

    /// <summary>Distinct signer DNs, newline-joined so key rotation keeps every identity.</summary>
    public string? Dn => Join(Signers.Select(s => s.Dn));

    /// <summary>Distinct signer key algorithms with size, e.g. <c>RSA 2048</c>.</summary>
    public string? KeyAlgorithm => Join(Signers.Select(s => s.KeyAlgorithm is null
        ? null
        : s.KeySizeBits is null ? s.KeyAlgorithm : $"{s.KeyAlgorithm} {s.KeySizeBits}"));

    private static string? Join(IEnumerable<string?> values)
    {
        var list = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return list.Count == 0 ? null : string.Join('\n', list);
    }
}

public sealed class ApkSignerParseException(string message) : Exception(message);

/// <summary>
/// Parses <c>apksigner verify --print-certs</c> output. Collects every
/// <c>Signer #N certificate … digest:</c> line plus the signer's DN and key
/// algorithm/size; digests are normalized via <see cref="CertFingerprint"/>.
/// Older build-tools print SHA-256 + SHA-1 only; MD5 is optional per signer.
/// Scheme lines appear once per APK, not per signer.
/// </summary>
public static partial class ApkSignerParser
{
    [GeneratedRegex(@"^Signer #(\d+) certificate (SHA-256|SHA-1|MD5) digest:\s*([0-9A-Fa-f:\s]+?)\s*$",
        RegexOptions.Multiline)]
    private static partial Regex DigestLine();

    [GeneratedRegex(@"^Signer #(\d+) certificate DN:\s*(?<dn>\S.*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex DnLine();

    [GeneratedRegex(@"^Signer #(\d+) key algorithm:\s*(?<algo>\S.*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex KeyAlgorithmLine();

    [GeneratedRegex(@"^Signer #(\d+) key size \(bits\):\s*(?<bits>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex KeySizeLine();

    [GeneratedRegex(@"^Verified using v(?<scheme>[0-9]+(?:\.[0-9]+)?) scheme[^:]*:\s*(?<ok>true|false)\s*$",
        RegexOptions.Multiline)]
    private static partial Regex SchemeLine();

    public static ApkSignerInfo Parse(string output)
    {
        var bySigner = new SortedDictionary<int, SignerFacts>();
        foreach (Match match in DigestLine().Matches(output))
        {
            var facts = Facts(bySigner, int.Parse(match.Groups[1].Value));
            facts.Digests.TryAdd(match.Groups[2].Value, match.Groups[3].Value);
        }

        foreach (Match match in DnLine().Matches(output))
        {
            Facts(bySigner, int.Parse(match.Groups[1].Value)).Dn ??= match.Groups["dn"].Value;
        }

        foreach (Match match in KeyAlgorithmLine().Matches(output))
        {
            Facts(bySigner, int.Parse(match.Groups[1].Value)).KeyAlgorithm ??= match.Groups["algo"].Value;
        }

        foreach (Match match in KeySizeLine().Matches(output))
        {
            Facts(bySigner, int.Parse(match.Groups[1].Value)).KeySizeBits ??= int.Parse(match.Groups["bits"].Value);
        }

        var signers = bySigner.Values.Select(f => new SignerCertificates(
            f.Digests.TryGetValue("SHA-256", out var sha256) ? CertFingerprint.Normalize(sha256) : null,
            f.Digests.TryGetValue("SHA-1", out var sha1) ? CertFingerprint.Normalize(sha1) : null,
            f.Digests.TryGetValue("MD5", out var md5) ? CertFingerprint.Normalize(md5) : null,
            f.Dn,
            f.KeyAlgorithm,
            f.KeySizeBits)).ToList();

        if (signers.Count == 0)
        {
            throw new ApkSignerParseException("No signer certificate digests in apksigner output.");
        }

        var schemes = SchemeLine().Matches(output)
            .Where(m => m.Groups["ok"].Value == "true")
            .Select(m => "v" + m.Groups["scheme"].Value)
            .ToList();

        return new ApkSignerInfo(signers, schemes);
    }

    private static SignerFacts Facts(SortedDictionary<int, SignerFacts> bySigner, int index)
    {
        if (!bySigner.TryGetValue(index, out var facts))
        {
            facts = new SignerFacts();
            bySigner[index] = facts;
        }

        return facts;
    }

    private sealed class SignerFacts
    {
        public Dictionary<string, string> Digests { get; } = new(StringComparer.Ordinal);
        public string? Dn { get; set; }
        public string? KeyAlgorithm { get; set; }
        public int? KeySizeBits { get; set; }
    }
}
