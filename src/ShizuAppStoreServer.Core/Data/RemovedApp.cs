namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Tombstone for a hard-deleted catalog row (table <c>removed_apps</c>).
/// The upserter (<see cref="Sync.CatalogUpserter"/>) hard-deletes stale
/// <c>(url, category)</c> pairs; without a record of the deletion,
/// <c>GET /v1/changes</c> could not report them in <c>removed[]</c>.
/// Resurrecting an entry (same slug re-added) clears its tombstone.
/// Rows are tiny and kept indefinitely so arbitrarily old <c>since=</c>
/// cursors stay correct.
/// </summary>
public sealed class RemovedApp
{
    public long Id { get; set; }

    /// <summary>Slug of the deleted app. Unique: re-deleting refreshes the row.</summary>
    public required string Slug { get; set; }

    public string? Name { get; set; }

    public Listing Listing { get; set; }

    public DateTimeOffset RemovedAt { get; set; }
}
