using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Sync;

namespace ShizuAppStoreServer.Controllers;

/// <summary>
/// Operator endpoint that re-renders every direct-APK icon inside the
/// running server. The run takes the shared sync gate, so it serializes
/// with the fast and nightly passes while the API keeps serving reads.
/// </summary>
[ApiController]
[Route("v1/admin/refresh-icons")]
[EnableRateLimiting("api")]
public sealed class AdminIconRefreshController(
    AdminOptions adminOptions, IconRefreshCoordinator coordinator) : ControllerBase
{
    /// <summary>
    /// Starts a refresh and returns immediately; poll GET for progress.
    /// Authenticates with <c>Authorization: Bearer &lt;token&gt;</c>. The
    /// optional JSON body carries <c>{ "force": true }</c> to rewrite
    /// icons whose fresh bytes already match the recorded hash.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(4096)]
    [ProducesResponseType<IconRefreshStatusDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IconRefreshStatusDto>> Start(CancellationToken ct = default)
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var force = false;
        using (var body = new MemoryStream())
        {
            await Request.Body.CopyToAsync(body, ct);
            try
            {
                using var json = JsonDocument.Parse(body.ToArray());
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("force", out var forceProp)
                    && forceProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    force = forceProp.GetBoolean();
                }
            }
            catch (JsonException)
            {
                // Non-JSON bodies are fine, the token already authenticated them.
            }
        }

        var (started, reason, status) = coordinator.TryStart(force);
        if (!started)
        {
            return Problem(reason, statusCode: StatusCodes.Status409Conflict,
                title: "Icon refresh not started");
        }

        return Accepted(ToDto(status));
    }

    /// <summary>Returns the current refresh state; 200 in every state.</summary>
    [HttpGet]
    [ProducesResponseType<IconRefreshStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<IconRefreshStatusDto> Get()
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        return Ok(ToDto(coordinator.Snapshot()));
    }

    /// <summary>Cancels a running refresh; 409 when nothing is running.</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult Cancel()
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        return coordinator.TryCancel()
            ? Accepted()
            : Problem("No icon refresh is running.", statusCode: StatusCodes.Status409Conflict);
    }

    private ActionResult? Authorize()
    {
        var token = adminOptions.Token;
        if (string.IsNullOrEmpty(token))
        {
            return Problem("Icon refresh is not configured (missing admin token).",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return AdminAuth.IsValidToken(Request, token)
            ? null
            : Problem("Invalid or missing token.", statusCode: StatusCodes.Status401Unauthorized);
    }

    private static IconRefreshStatusDto ToDto(IconRefreshStatus status) => new(
        status.State, status.Force, status.StartedAt, status.FinishedAt,
        status.Checked, status.Refreshed, status.AlreadyCurrent, status.Failed,
        status.Errors, status.Error);
}
