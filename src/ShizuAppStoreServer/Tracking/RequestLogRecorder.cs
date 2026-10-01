using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Tracking;

/// <summary>
/// Appends a drained batch of hits to <c>request_logs</c> in one save. Rows
/// are append-only: no aggregation, no upsert, no pruning.
/// </summary>
public static class RequestLogRecorder
{
    public static async Task FlushAsync(
        ShizuDbContext db, IReadOnlyList<RequestLogHit> hits, CancellationToken ct = default)
    {
        if (hits.Count == 0)
        {
            return;
        }

        db.RequestLogs.AddRange(hits.Select(h => new RequestLog
        {
            SeenAt = h.SeenAt,
            SeenDay = DateOnly.FromDateTime(h.SeenAt.UtcDateTime),
            Method = h.Method,
            Path = h.Path,
            QueryString = h.QueryString,
            RawTarget = h.RawTarget,
            Protocol = h.Protocol,
            Scheme = h.Scheme,
            Host = h.Host,
            RawRequest = h.RawRequest,
            Headers = h.Headers,
            StatusCode = h.StatusCode,
            DurationMs = h.DurationMs,
            UserAgent = h.UserAgent,
            Origin = h.Origin,
            RemoteIp = h.RemoteIp,
            ClientIp = h.ClientIp,
            ForwardedFor = h.ForwardedFor,
            CfRay = h.CfRay,
            Country = h.Country,
            TraceId = h.TraceId,
        }));

        await db.SaveChangesAsync(ct);
    }
}
