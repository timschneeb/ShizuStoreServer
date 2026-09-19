using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Sync;

namespace ShizuAppStoreServer.Controllers;

/// <summary>
/// Operator endpoint that re-resolves screenshots for every served app inside
/// the running server (F-Droid/Izzy plus the repo fallback, forced past the
/// per-app recheck window). Takes the shared sync gate, so it serializes with
/// the fast and nightly passes while the API keeps serving reads.
/// </summary>
[ApiController]
[Route("v1/admin/refresh-screenshots")]
[EnableRateLimiting("api")]
public sealed class AdminScreenshotRefreshController(
    AdminOptions adminOptions, ScreenshotRefreshCoordinator coordinator) : ControllerBase
{
    /// <summary>
    /// Starts a pass and returns immediately; poll GET for progress.
    /// Authenticates with <c>Authorization: Bearer &lt;token&gt;</c>.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ScreenshotRefreshStatusDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<ScreenshotRefreshStatusDto> Start()
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var (started, reason, status) = coordinator.TryStart();
        if (!started)
        {
            return Problem(reason, statusCode: StatusCodes.Status409Conflict,
                title: "Screenshots refresh not started");
        }

        return Accepted(ToDto(status));
    }

    /// <summary>Returns the current refresh state; 200 in every state.</summary>
    [HttpGet]
    [ProducesResponseType<ScreenshotRefreshStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<ScreenshotRefreshStatusDto> Get()
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        return Ok(ToDto(coordinator.Snapshot()));
    }

    /// <summary>Cancels a running pass; 409 when nothing is running.</summary>
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
            : Problem("No screenshots refresh is running.", statusCode: StatusCodes.Status409Conflict);
    }

    private ActionResult? Authorize()
    {
        var token = adminOptions.Token;
        if (string.IsNullOrEmpty(token))
        {
            return Problem("Screenshots refresh is not configured (missing admin token).",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return AdminAuth.IsValidToken(Request, token)
            ? null
            : Problem("Invalid or missing token.", statusCode: StatusCodes.Status401Unauthorized);
    }

    private static ScreenshotRefreshStatusDto ToDto(ScreenshotRefreshStatus status) => new(
        status.State, status.StartedAt, status.FinishedAt,
        status.Checked, status.Updated, status.Current, status.Failed,
        status.Errors, status.Error);
}
