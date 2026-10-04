namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Curated use case tag (table <c>use_cases</c>) describing a Shizuku-mediated
/// capability aimed at other apps or system state. Slugs are the stable wire
/// identity; the AI classifier may only assign active rows, and new rows are
/// created from promoted candidates or by an operator.
/// </summary>
public sealed class UseCase
{
    public long Id { get; set; }

    /// <summary>Stable lowercase kebab-case slug.</summary>
    public required string Slug { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// One-line scope note handed to the classifier. This is where the
    /// boundary is stated (for example self-updates do not count as
    /// installing apps), so the wording is behavior, not decoration.
    /// </summary>
    public required string Definition { get; set; }

    /// <summary>
    /// Inactive tags keep existing assignments but are hidden from the API and
    /// never newly assigned, so a bad tag can be retired without data loss.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
