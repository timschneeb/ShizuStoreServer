namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// Declared Shizuku-family manager signals from one analyzed APK. These are
/// "declares" facts, never capability claims: what a build actually does with
/// Shizuku comes from source analysis later, so the copy derived from these
/// flags must say "can", not "does". The Shizuku permission itself is not
/// tracked: almost every app in the catalog declares it, so it carries no
/// signal.
/// </summary>
public sealed record ApkSignals(bool DhizukuDeclared)
{
    public static ApkSignals None { get; } = new(false);
}

/// <summary>Derives declared manager signals from the declared permission list.</summary>
public static class ShizukuSignalScanner
{
    public const string DhizukuPermissionPrefix = "com.rosan.dhizuku.permission.";

    public static ApkSignals Scan(IReadOnlyList<string> declaredPermissions)
    {
        var dhizukuDeclared = declaredPermissions.Any(p =>
            p.StartsWith(DhizukuPermissionPrefix, StringComparison.Ordinal));

        return new ApkSignals(dhizukuDeclared);
    }
}
