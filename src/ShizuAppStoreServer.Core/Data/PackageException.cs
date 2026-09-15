namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Operator override for the Shizuku-permission gate (table
/// <c>package_exceptions</c>). Keyed by package so it survives list changes;
/// rows are seeded by migration and edited with SQL (no admin endpoint).
/// </summary>
public sealed class PackageException
{
    public long Id { get; set; }

    /// <summary>The <c>apps.package_name</c> this override applies to.</summary>
    public required string PackageName { get; set; }

    public PackageExceptionAction Action { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
