namespace ShizuAppStoreServer.Tracking;

/// <summary>
/// Non-client request log. Binds to the <c>RequestLog</c> config section;
/// tests shorten the flush interval via a DI swap.
/// </summary>
public sealed class RequestLogOptions
{
    private string _excludedIps = "";
    private HashSet<string> _excludedIpSet = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Off skips both the middleware and the flush worker.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often buffered hits are written to the database.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Bounded in-memory buffer; hits beyond it are dropped and logged.</summary>
    public int MaxBufferedHits { get; set; } = 2000;

    /// <summary>Comma-separated client IPs whose requests are never logged.</summary>
    public string ExcludedIps
    {
        get => _excludedIps;
        set
        {
            _excludedIps = value ?? "";
            _excludedIpSet = _excludedIps
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>True when the client IP is on the <see cref="ExcludedIps"/> list.</summary>
    public bool IsExcludedIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip) && _excludedIpSet.Contains(ip);
}
