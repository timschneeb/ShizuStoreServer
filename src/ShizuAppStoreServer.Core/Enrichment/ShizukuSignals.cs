namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// Declared Shizuku-family manager signals from one analyzed APK. These are
/// "declares" facts, never capability claims: what a build actually does with
/// Shizuku comes from source analysis later, so the copy derived from these
/// flags must say "can", not "does". The Shizuku declaration is tracked per
/// row because the availability gate accepts an app when any recorded build
/// declares it: a forge release can predate the app's Shizuku support while
/// the F-Droid build already uses it.
/// </summary>
public sealed record ApkSignals(bool DhizukuDeclared, bool ShizukuDeclared)
{
    public static ApkSignals None { get; } = new(false, false);
}

/// <summary>Derives declared manager signals from the declared permission list.</summary>
public static class ShizukuSignalScanner
{
    public const string DhizukuPermissionPrefix = "com.rosan.dhizuku.permission.";

    public static ApkSignals Scan(IReadOnlyList<string> declaredPermissions)
    {
        var dhizukuDeclared = declaredPermissions.Any(p =>
            p.StartsWith(DhizukuPermissionPrefix, StringComparison.Ordinal));

        return new ApkSignals(dhizukuDeclared, ShizukuPermission.IsDeclared(declaredPermissions));
    }
}
