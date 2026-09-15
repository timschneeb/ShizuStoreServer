namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// The Shizuku-permission gate's detection rule. A DirectApk row must declare
/// a Shizuku permission to stay available unless an operator exception allows
/// it (see <c>package_exceptions</c>).
/// </summary>
public static class ShizukuPermission
{
    /// <summary>Exclusion reason for DirectApk rows that do not declare Shizuku.</summary>
    public const string Reason = "APK does not declare a Shizuku permission.";

    /// <summary>Quality issue rule for a gated row with no operator exception.</summary>
    public const string Rule = "shizuku_permission_missing";

    /// <summary>
    /// True when any declared permission names Shizuku. Matches every observed
    /// namespace (moe.shizuku, rikka.shizuku, dev.rikka.shizuku, af.shizuku,
    /// moe.shizuku.api) without matching AndroidX's
    /// <c>DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION</c> artifacts.
    /// </summary>
    public static bool IsDeclared(IEnumerable<string> permissions) =>
        permissions.Any(p => p.Contains("shizuku", StringComparison.OrdinalIgnoreCase));
}
