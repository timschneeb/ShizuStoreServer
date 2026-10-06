namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Operator override for one column of one list entry (table
/// <c>app_overrides</c>). The sync materializer applies active rows to the
/// <c>apps</c> row after enrichment and stores the pre-override value in
/// <see cref="BaselineValue"/> so deleting the row restores it. Rows are
/// seeded with SQL or managed through the stats dashboard; there is no API
/// admin endpoint.
/// </summary>
public sealed class AppOverride
{
    public long Id { get; set; }

    /// <summary>The <c>apps.slug</c> of the entry the override applies to.</summary>
    public required string AppSlug { get; set; }

    /// <summary>The App column name in snake_case, restricted to the applier registry.</summary>
    public required string Field { get; set; }

    /// <summary>Textual value parsed per field by the applier.</summary>
    public required string Value { get; set; }

    /// <summary>
    /// Value last materialized on the app row. Null until the first apply.
    /// While it equals <see cref="Value"/> the applier writes the override
    /// back after each enrichment pass without bumping <c>apps.updated_at</c>,
    /// so enrichment drift does not fake delta events for <c>/v1/changes</c>.
    /// </summary>
    public string? AppliedValue { get; set; }

    /// <summary>Value captured before the first apply; used to restore on removal.</summary>
    public string? BaselineValue { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Soft delete marker; the applier restores the baseline and drops the row.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
