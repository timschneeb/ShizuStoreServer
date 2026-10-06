namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Global blocklist of screenshot URLs that are false detections in source
/// repos (table <c>blocked_screenshot_urls</c>). Blocked URLs are filtered
/// when screenshots are collected and when they are served, so they never
/// appear in the catalog. Rows are seeded with SQL, there is no admin endpoint.
/// </summary>
public sealed class BlockedScreenshotUrl
{
    public long Id { get; set; }

    /// <summary>Exact screenshot URL to hide.</summary>
    public required string Url { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Soft delete marker; the row stops hiding the URL.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
