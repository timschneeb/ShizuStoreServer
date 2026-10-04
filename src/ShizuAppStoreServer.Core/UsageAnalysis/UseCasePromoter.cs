using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>
/// Turns pending candidates into vocabulary. Automatic threshold promotion and
/// operator promotion share this path so merged slugs, proposing-app
/// assignments and the catalog-wide re-tag can never diverge.
/// </summary>
public interface IUseCasePromoter
{
    /// <summary>Promotes every candidate proposed by at least <paramref name="minApps"/> apps.</summary>
    Task<int> PromoteAboveThresholdAsync(int minApps, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Promotes one candidate; optional overrides replace the inferred name and
    /// definition. Returns true when a new tag was created, false when the slug
    /// already existed and the candidate was merged into it.
    /// </summary>
    Task<bool> PromoteAsync(
        UseCaseCandidate candidate, string? name, string? definition, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Assigns the candidate's proposers to an existing tag; null when the slug is unknown.</summary>
    Task<UseCase?> MergeAsync(
        UseCaseCandidate candidate, string targetSlug, DateTimeOffset now, CancellationToken ct = default);
}

public sealed class UseCasePromoter(
    ShizuDbContext db,
    IUsageAnalysisQueue queue,
    ILogger<UseCasePromoter>? log = null) : IUseCasePromoter
{
    private const int MaxDefinitionChars = 500;

    public async Task<int> PromoteAboveThresholdAsync(int minApps, DateTimeOffset now, CancellationToken ct = default)
    {
        var threshold = Math.Max(1, minApps);
        var counts = await db.AppUseCaseProposals
            .Where(p => p.Candidate!.Status == UseCaseCandidateStatus.Pending)
            .GroupBy(p => p.CandidateId)
            .Select(g => new { CandidateId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var promoted = 0;
        foreach (var group in counts.Where(c => c.Count >= threshold))
        {
            var candidate = await db.UseCaseCandidates.FirstAsync(c => c.Id == group.CandidateId, ct);
            if (await PromoteAsync(candidate, name: null, definition: null, now, ct))
            {
                promoted++;
            }
        }

        return promoted;
    }

    public async Task<bool> PromoteAsync(
        UseCaseCandidate candidate, string? name, string? definition, DateTimeOffset now, CancellationToken ct = default)
    {
        var existing = await db.UseCases.FirstOrDefaultAsync(u => u.Slug == candidate.Slug, ct);
        if (existing is not null)
        {
            // An inactive tag already owns the slug. Merging keeps the
            // vocabulary free of near-duplicates.
            await MergeIntoAsync(candidate, existing, now, ct);
            await db.SaveChangesAsync(ct);
            return false;
        }

        var inferred = definition ?? await LatestReasonAsync(candidate.Id, ct) ?? candidate.Name;

        var useCase = new UseCase
        {
            Slug = candidate.Slug,
            Name = name ?? candidate.Name,
            Definition = Truncate(inferred, MaxDefinitionChars),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.UseCases.Add(useCase);

        candidate.Status = UseCaseCandidateStatus.Promoted;
        candidate.UpdatedAt = now;
        await AssignProposersAsync(candidate.Id, useCase, now, ct);

        await db.SaveChangesAsync(ct);

        // The promoted capability is new vocabulary for every stored report,
        // so the whole catalog re-tags against it. Apps with an active run
        // catch up inline when that run finishes.
        await queue.TaggingBackfillAsync(
            onlyMissing: false, includeStale: false, force: false, all: true, slug: null, limit: null, ct);

        log?.LogInformation("Use case candidate {Slug} promoted.", candidate.Slug);
        return true;
    }

    public async Task<UseCase?> MergeAsync(
        UseCaseCandidate candidate, string targetSlug, DateTimeOffset now, CancellationToken ct = default)
    {
        var target = await db.UseCases.FirstOrDefaultAsync(u => u.Slug == targetSlug, ct);
        if (target is null)
        {
            return null;
        }

        await MergeIntoAsync(candidate, target, now, ct);
        await db.SaveChangesAsync(ct);
        return target;
    }

    private async Task MergeIntoAsync(
        UseCaseCandidate candidate, UseCase target, DateTimeOffset now, CancellationToken ct)
    {
        candidate.Status = UseCaseCandidateStatus.Merged;
        candidate.MergedIntoUseCaseId = target.Id;
        candidate.UpdatedAt = now;
        await AssignProposersAsync(candidate.Id, target, now, ct);
        log?.LogInformation(
            "Use case candidate {Slug} merged into {Target}.", candidate.Slug, target.Slug);
    }

    private async Task AssignProposersAsync(long candidateId, UseCase useCase, DateTimeOffset now, CancellationToken ct)
    {
        var appIds = await db.AppUseCaseProposals
            .Where(p => p.CandidateId == candidateId)
            .Select(p => p.AppId)
            .Distinct()
            .ToListAsync(ct);
        var assigned = await db.AppUseCases
            .Where(x => x.UseCaseId == useCase.Id && appIds.Contains(x.AppId))
            .Select(x => x.AppId)
            .ToListAsync(ct);
        var missing = appIds.Except(assigned).ToList();
        foreach (var appId in missing)
        {
            db.AppUseCases.Add(new AppUseCase { AppId = appId, UseCase = useCase });
        }

        if (missing.Count == 0)
        {
            return;
        }

        var apps = await db.Apps.Where(a => missing.Contains(a.Id)).ToListAsync(ct);
        foreach (var app in apps)
        {
            app.UpdatedAt = now;
        }
    }

    private async Task<string?> LatestReasonAsync(long candidateId, CancellationToken ct)
    {
        // SQLite cannot order DateTimeOffset in SQL, so rank the reasons in memory.
        var reasons = await db.AppUseCaseProposals
            .Where(p => p.CandidateId == candidateId && p.Reason != null)
            .Select(p => new { p.Reason, p.CreatedAt })
            .ToListAsync(ct);
        return reasons.OrderByDescending(r => r.CreatedAt).Select(r => r.Reason).FirstOrDefault();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
