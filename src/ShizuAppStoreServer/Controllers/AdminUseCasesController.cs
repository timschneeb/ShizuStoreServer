using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Controllers;

/// <summary>
/// Operator endpoints for the use case vocabulary: edit tags, review AI
/// proposals and promote, dismiss or merge candidates. Vocabulary changes
/// trigger a catalog-wide re-tag so existing reports pick up the new tag.
/// </summary>
[ApiController]
[Route("v1/admin/use-cases")]
[EnableRateLimiting("api")]
public sealed class AdminUseCasesController(
    ShizuDbContext db,
    AdminOptions adminOptions,
    IUseCasePromoter promoter,
    IUsageAnalysisQueue queue) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<UseCaseAdminDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<UseCaseAdminDto>>> List(CancellationToken ct = default)
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var counts = await db.AppUseCases
            .AsNoTracking()
            .Where(link => link.App!.Availability != Availability.Excluded && link.App.PublishedAt != null)
            .GroupBy(link => link.UseCaseId)
            .Select(g => new { UseCaseId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var byUseCase = counts.ToDictionary(x => x.UseCaseId, x => x.Count);
        var useCases = await db.UseCases.AsNoTracking().OrderBy(u => u.Name).ToListAsync(ct);
        return Ok(useCases
            .Select(u => new UseCaseAdminDto(
                u.Id, u.Slug, u.Name, u.Definition, u.IsActive, byUseCase.GetValueOrDefault(u.Id), u.UpdatedAt))
            .ToList());
    }

    [HttpPost]
    [RequestSizeLimit(4096)]
    [ProducesResponseType<UseCaseAdminDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UseCaseAdminDto>> Upsert(
        [FromBody] UseCaseUpsertRequestDto? body, CancellationToken ct = default)
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var name = body?.Name?.Trim();
        // Normalize explicit slugs too: the table's slug drives the public API
        // filter and the synced vocabulary, so it must stay in slug shape.
        var slug = body?.Slug is { } rawSlug ? UseCaseTagValidator.Slugify(rawSlug) : null;
        if (string.IsNullOrEmpty(slug))
        {
            slug = string.IsNullOrEmpty(name) ? null : UseCaseTagValidator.Slugify(name);
        }

        if (string.IsNullOrEmpty(slug))
        {
            return Problem("A use case needs a name.", statusCode: StatusCodes.Status400BadRequest);
        }

        var now = DateTimeOffset.UtcNow;
        var useCase = await db.UseCases.FirstOrDefaultAsync(u => u.Slug == slug, ct);
        var retag = false;
        if (useCase is null)
        {
            // Only creation needs a name; updates may toggle a tag by slug alone.
            if (string.IsNullOrEmpty(name))
            {
                return Problem("A use case needs a name.", statusCode: StatusCodes.Status400BadRequest);
            }

            useCase = new UseCase
            {
                Slug = slug,
                Name = name,
                Definition = body?.Definition?.Trim() ?? name,
                IsActive = body?.IsActive ?? true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.UseCases.Add(useCase);
            retag = useCase.IsActive;
        }
        else
        {
            if (!string.IsNullOrEmpty(name))
            {
                useCase.Name = name;
            }

            if (body?.Definition is { } definition && definition.Trim() != useCase.Definition)
            {
                useCase.Definition = definition.Trim();
                retag = useCase.IsActive;
            }

            if (body?.IsActive is { } active)
            {
                // Deactivation drops the tag from every future tagging run, so a
                // reactivated tag needs a catalog-wide retag to refill it.
                if (active && !useCase.IsActive)
                {
                    retag = true;
                }

                useCase.IsActive = active;
            }

            useCase.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        if (retag)
        {
            await queue.TaggingBackfillAsync(onlyMissing: false, includeStale: false, force: false, all: true, slug: null, limit: null, ct);
        }

        var count = await db.AppUseCases.CountAsync(
            link => link.UseCaseId == useCase.Id
                && link.App!.Availability != Availability.Excluded
                && link.App.PublishedAt != null, ct);
        return Ok(ToDto(useCase, count));
    }

    [HttpGet("candidates")]
    [ProducesResponseType<IReadOnlyList<UseCaseCandidateDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<UseCaseCandidateDto>>> Candidates(CancellationToken ct = default)
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var candidates = await db.UseCaseCandidates
            .AsNoTracking()
            .Where(c => c.Status == UseCaseCandidateStatus.Pending)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
        var candidateIds = candidates.Select(c => c.Id).ToList();
        var proposals = await db.AppUseCaseProposals
            .AsNoTracking()
            .Where(p => candidateIds.Contains(p.CandidateId))
            .Select(p => new { p.CandidateId, p.Reason, p.CreatedAt })
            .ToListAsync(ct);
        var counts = proposals.GroupBy(p => p.CandidateId).ToDictionary(g => g.Key, g => g.Count());
        var latest = proposals
            .Where(p => !string.IsNullOrWhiteSpace(p.Reason))
            .GroupBy(p => p.CandidateId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.CreatedAt).First().Reason);
        return Ok(candidates
            .Select(c => new UseCaseCandidateDto(
                c.Id, c.Slug, c.Name, c.Status.ToString(), counts.GetValueOrDefault(c.Id),
                latest.GetValueOrDefault(c.Id), c.UpdatedAt))
            .ToList());
    }

    [HttpPost("candidates/{slug}/promote")]
    [RequestSizeLimit(4096)]
    [ProducesResponseType<UseCaseAdminDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UseCaseAdminDto>> Promote(
        string slug, [FromBody] UseCasePromoteRequestDto? body, CancellationToken ct = default)
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var candidate = await db.UseCaseCandidates.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (candidate is null)
        {
            return NotFound();
        }

        if (candidate.Status != UseCaseCandidateStatus.Pending)
        {
            return Problem(
                $"Candidate '{slug}' is {candidate.Status}.",
                statusCode: StatusCodes.Status409Conflict);
        }

        await promoter.PromoteAsync(candidate, body?.Name, body?.Definition, DateTimeOffset.UtcNow, ct);
        var useCase = await db.UseCases.AsNoTracking().FirstAsync(u => u.Slug == candidate.Slug, ct);
        var count = await db.AppUseCases.CountAsync(
            link => link.UseCaseId == useCase.Id
                && link.App!.Availability != Availability.Excluded
                && link.App.PublishedAt != null, ct);
        return Ok(ToDto(useCase, count));
    }

    [HttpPost("candidates/{slug}/dismiss")]
    [ProducesResponseType<UseCaseCandidateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UseCaseCandidateDto>> Dismiss(string slug, CancellationToken ct = default)
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var candidate = await db.UseCaseCandidates.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (candidate is null)
        {
            return NotFound();
        }

        candidate.Status = UseCaseCandidateStatus.Dismissed;
        candidate.MergedIntoUseCaseId = null;
        candidate.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(candidate, 0, null));
    }

    [HttpPost("candidates/{slug}/merge")]
    [RequestSizeLimit(4096)]
    [ProducesResponseType<UseCaseAdminDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UseCaseAdminDto>> Merge(
        string slug, [FromBody] UseCaseMergeRequestDto? body, CancellationToken ct = default)
    {
        if (Authorize() is { } problem)
        {
            return problem;
        }

        var targetSlug = body?.TargetSlug?.Trim();
        if (string.IsNullOrEmpty(targetSlug))
        {
            return Problem("A merge target slug is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var candidate = await db.UseCaseCandidates.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        var target = await db.UseCases.FirstOrDefaultAsync(u => u.Slug == targetSlug, ct);
        if (candidate is null || target is null)
        {
            return NotFound();
        }

        await promoter.MergeAsync(candidate, target.Slug, DateTimeOffset.UtcNow, ct);
        var count = await db.AppUseCases.CountAsync(
            link => link.UseCaseId == target.Id
                && link.App!.Availability != Availability.Excluded
                && link.App.PublishedAt != null, ct);
        return Ok(ToDto(target, count));
    }

    private static UseCaseAdminDto ToDto(UseCase useCase, int appCount) =>
        new(useCase.Id, useCase.Slug, useCase.Name, useCase.Definition, useCase.IsActive, appCount, useCase.UpdatedAt);

    private static UseCaseCandidateDto ToDto(UseCaseCandidate candidate, int appCount, string? latestReason) =>
        new(candidate.Id, candidate.Slug, candidate.Name, candidate.Status.ToString(), appCount, latestReason, candidate.UpdatedAt);

    private ActionResult? Authorize()
    {
        if (string.IsNullOrEmpty(adminOptions.Token))
        {
            return Problem(
                "The admin API is not configured. Set Admin:Token or SHIZU_ADMIN_TOKEN.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!AdminAuth.IsValidToken(Request, adminOptions.Token))
        {
            return Problem("Invalid admin token.", statusCode: StatusCodes.Status401Unauthorized);
        }

        return null;
    }
}
