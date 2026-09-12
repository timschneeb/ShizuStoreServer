namespace ShizuAppStoreServer.Core;

/// <summary>
/// Signing-certificate fingerprint helpers (APK signatures). Digests are
/// stored lowercase hex without separators; rotated keys (multiple signers)
/// are space-joined sets that clients match by membership.
/// </summary>
public static class CertFingerprint
{
    /// <summary>Lowercase hex without colons/whitespace; null when blank.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Replace(":", "").Replace(" ", "").ToLowerInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>Space-joins distinct normalized digests; null when there are none.</summary>
    public static string? Join(IEnumerable<string?> digests)
    {
        var set = digests.Select(Normalize).OfType<string>().Distinct().ToArray();
        return set.Length == 0 ? null : string.Join(' ', set);
    }
}
