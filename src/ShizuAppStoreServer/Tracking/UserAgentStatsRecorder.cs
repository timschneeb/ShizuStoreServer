using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Tracking;

/// <summary>
/// Merges a drained batch of hits into the two stats tables. Aggregation is
/// in-memory (one row per UA and one per UA per UTC day) so a batch costs two
/// lookups and one save regardless of its size. Only the flush worker writes,
/// so read-modify-write needs no conflict handling.
/// </summary>
public static class UserAgentStatsRecorder
{
    public static async Task FlushAsync(
        ShizuDbContext db, IReadOnlyList<UserAgentHit> hits, CancellationToken ct = default)
    {
        if (hits.Count == 0)
        {
            return;
        }

        var agents = hits
            .GroupBy(h => h.UserAgent, StringComparer.Ordinal)
            .Select(g => new AgentBatch(
                g.Key,
                g.LongCount(),
                g.Min(h => h.SeenAt),
                g.Max(h => h.SeenAt),
                g.OrderBy(h => h.SeenAt).Last().Path))
            .ToList();

        var agentKeys = agents.Select(a => a.UserAgent).ToList();
        var existingAgents = await db.ClientUserAgents
            .Where(x => agentKeys.Contains(x.UserAgent))
            .ToDictionaryAsync(x => x.UserAgent, ct);

        foreach (var batch in agents)
        {
            if (existingAgents.TryGetValue(batch.UserAgent, out var row))
            {
                row.RequestCount += batch.RequestCount;
                if (batch.FirstSeenAt < row.FirstSeenAt)
                {
                    row.FirstSeenAt = batch.FirstSeenAt;
                }

                if (batch.LastSeenAt >= row.LastSeenAt)
                {
                    row.LastSeenAt = batch.LastSeenAt;
                    row.LastPath = batch.Path;
                }
            }
            else
            {
                db.ClientUserAgents.Add(new ClientUserAgent
                {
                    UserAgent = batch.UserAgent,
                    RequestCount = batch.RequestCount,
                    FirstSeenAt = batch.FirstSeenAt,
                    LastSeenAt = batch.LastSeenAt,
                    LastPath = batch.Path,
                });
            }
        }

        var days = hits
            .GroupBy(h => (h.UserAgent, Day: DateOnly.FromDateTime(h.SeenAt.UtcDateTime)))
            .Select(g => new DayBatch(g.Key.UserAgent, g.Key.Day, g.LongCount()))
            .ToList();

        var dayKeys = days.Select(d => d.UserAgent).Distinct(StringComparer.Ordinal).ToList();
        var existingDays = await db.ClientUserAgentDays
            .Where(x => dayKeys.Contains(x.UserAgent))
            .ToListAsync(ct);
        var daysByKey = existingDays.ToDictionary(x => (x.UserAgent, x.Day));

        foreach (var batch in days)
        {
            if (daysByKey.TryGetValue((batch.UserAgent, batch.Day), out var row))
            {
                row.RequestCount += batch.RequestCount;
            }
            else
            {
                db.ClientUserAgentDays.Add(new ClientUserAgentDay
                {
                    UserAgent = batch.UserAgent,
                    Day = batch.Day,
                    RequestCount = batch.RequestCount,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private readonly record struct AgentBatch(
        string UserAgent, long RequestCount, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, string Path);

    private readonly record struct DayBatch(string UserAgent, DateOnly Day, long RequestCount);
}
