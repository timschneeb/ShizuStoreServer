using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShizuAppStoreServer.Core.Enrichment;

namespace ShizuAppStoreServer.Controllers;

/// <summary>Serves normalized app icons / letter-avatars from the icon store.</summary>
[ApiController]
[Route("icons")]
[EnableRateLimiting("api")]
public sealed class IconsController(EnrichmentOptions enrichment) : ControllerBase
{
    /// <summary>
    /// Returns <c>{sha256}.png</c>. Files are content-addressed and never
    /// change, so clients may cache them immutably for a year.
    /// </summary>
    [HttpGet("{sha}.png")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Get(string sha)
    {
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
        {
            return Problem($"Invalid icon hash '{sha}'.", statusCode: StatusCodes.Status400BadRequest);
        }

        // Validated hex above, so no path traversal is possible.
        var path = Path.Combine(enrichment.IconStorePath, sha + ".png");
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return PhysicalFile(Path.GetFullPath(path), "image/png");
    }
}
