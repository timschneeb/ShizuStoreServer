using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Controllers;

/// <summary>Operator endpoints (HMAC-authenticated, not for public clients).</summary>
[ApiController]
[Route("v1/admin/sync")]
[EnableRateLimiting("api")]
public sealed class AdminController(ShizuDbContext db, AdminOptions adminOptions) : ControllerBase
{
    private const string SignatureHeader = "X-Shizu-Signature";

    /// <summary>
    /// Queues a list-sync run (drained by the M6 fast loop). Authenticates
    /// with <c>X-Shizu-Signature: hex(HMAC-SHA256(raw_body, secret))</c>;
    /// the secret comes from <c>Admin:HmacSecret</c> config or the
    /// <c>SHIZU_ADMIN_SECRET</c> environment variable. The optional JSON
    /// body carries a free-form <c>reason</c>.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(4096)]
    [ProducesResponseType<SyncAcceptedDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SyncAcceptedDto>> RequestSync(CancellationToken ct = default)
    {
        var secret = adminOptions.HmacSecret;
        if (string.IsNullOrEmpty(secret))
        {
            return Problem("Sync webhook is not configured (missing admin secret).",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        using var body = new MemoryStream();
        await Request.Body.CopyToAsync(body, ct);
        var bodyBytes = body.ToArray();

        if (!Request.Headers.TryGetValue(SignatureHeader, out var signatureValues)
            || !IsValidSignature(bodyBytes, signatureValues.ToString(), secret))
        {
            return Problem("Invalid or missing signature.", statusCode: StatusCodes.Status401Unauthorized);
        }

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
            // Non-JSON bodies are fine — the HMAC already authenticated them.
        }

        db.SyncRequests.Add(new SyncRequest
        {
            RequestedAt = DateTimeOffset.UtcNow,
            Reason = reason,
            Processed = false,
        });
        await db.SaveChangesAsync(ct);

        return Accepted(new SyncAcceptedDto(true));
    }

    private static bool IsValidSignature(byte[] body, string signatureHex, string secret)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromHexString(signatureHex.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), body);
        return signature.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(signature, expected);
    }
}
