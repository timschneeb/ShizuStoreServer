using Microsoft.AspNetCore.Mvc;
using ShizuAppStoreServer.Api;

namespace ShizuAppStoreServer.Controllers;

/// <summary>Liveness probe. Deliberately outside the API rate limiter.</summary>
[ApiController]
[Route("healthz")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [ProducesResponseType<HealthDto>(StatusCodes.Status200OK)]
    public ActionResult<HealthDto> Get() => Ok(new HealthDto("ok"));
}
