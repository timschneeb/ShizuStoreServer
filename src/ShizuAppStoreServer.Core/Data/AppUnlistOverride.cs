namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Operator override that unlists one list entry from the catalog without
/// touching the upstream list (table <c>app_unlist_overrides</c>). The sync
/// applies it as <see cref="Availability.Excluded"/> with a dedicated reason
/// and a removal tombstone, and keeps the row, downloads and usage rows so
/// install history survives; deleting the row restores the entry. Rows are
/// seeded with SQL, there is no admin endpoint.
/// </summary>
public sealed class AppUnlistOverride
{
    public long Id { get; set; }

    /// <summary>The <c>apps.slug</c> of the list entry to unlist.</summary>
    public required string AppSlug { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
