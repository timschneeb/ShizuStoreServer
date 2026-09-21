namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Per-User-Agent per-UTC-day request counts (table <c>client_user_agent_days</c>),
/// the trend companion to <see cref="ClientUserAgent"/>. Same writer, same rules.
/// </summary>
public sealed class ClientUserAgentDay
{
    public long Id { get; set; }

    public string UserAgent { get; set; } = "";
    public DateOnly Day { get; set; }
    public long RequestCount { get; set; }
}
