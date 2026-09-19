using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Sync;

namespace ShizuAppStoreServer.Controllers;

/// <summary>Operator endpoints (token-authenticated, not for public clients).</summary>
[ApiController]
[Route("v1/admin/sync")]
[EnableRateLimiting("api")]
public sealed class AdminController(
    ShizuDbContext db, AdminOptions adminOptions, SyncSignal signal) : ControllerBase
{
    /// <summary>
    /// Queues a list-sync run and wakes the fast loop immediately.
    /// Authenticates with <c>Authorization: Bearer &lt;token&gt;</c>; the
    /// token comes from <c>Admin:Token</c> config or the
    /// <c>SHIZU_ADMIN_TOKEN</c> / legacy <c>SHIZU_ADMIN_SECRET</c> environment
    /// variable. The optional JSON body carries a free-form <c>reason</c>.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(4096)]
    [ProducesResponseType<SyncAcceptedDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SyncAcceptedDto>> RequestSync(CancellationToken ct = default)
    {
        var token = adminOptions.Token;
        if (string.IsNullOrEmpty(token))
        {
            return Problem("Sync webhook is not configured (missing admin token).",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!AdminAuth.IsValidToken(Request, token))
        {
            return Problem("Invalid or missing token.", statusCode: StatusCodes.Status401Unauthorized);
        }

        using var body = new MemoryStream();
        await Request.Body.CopyToAsync(body, ct);
        var bodyBytes = body.ToArray();

        string? reason = null;
        try
        {
            using var json = JsonDocument.Parse(bodyBytes);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("reason", out var reasonProp)
                && reasonProp.ValueKind == JsonValueKind.String)
            {
                reason = reasonProp.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON bodies are fine, the token already authenticated them.
        }

        db.SyncRequests.Add(new SyncRequest
        {
            RequestedAt = DateTimeOffset.UtcNow,
            Reason = reason,
            Processed = false,
        });
        await db.SaveChangesAsync(ct);
        signal.Request();

        return Accepted(new SyncAcceptedDto(true));
    }
}
