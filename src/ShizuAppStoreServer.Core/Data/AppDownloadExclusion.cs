namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Operator override that hides one APK package from one list entry (table
/// <c>app_download_exclusions</c>). Enrichment treats the package as absent
/// for that app, so a release that ships a conflicting build (for example a
/// drop-in replacement sharing another app's package) offers only the wanted
/// APK. Scoped by app slug so the same package can still be served by another
/// entry; rows are seeded with SQL, there is no admin endpoint.
/// </summary>
public sealed class AppDownloadExclusion
{
    public long Id { get; set; }

    /// <summary>The <c>apps.slug</c> of the list entry the override applies to.</summary>
    public required string AppSlug { get; set; }

    /// <summary>The APK package to drop from that entry's download candidates.</summary>
    public required string PackageName { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
