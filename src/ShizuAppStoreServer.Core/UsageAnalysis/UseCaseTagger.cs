using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>
/// Outcome of one classification attempt. <see cref="Retryable"/> is false for
/// terminal conditions such as a missing report; callers then park the run
/// instead of spending another model call.
/// </summary>
public sealed record UseCaseTagOutcome(
    bool Succeeded,
    bool Retryable,
    string? Error,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    int ModelCalls,
    int TagCount,
    int Promotions,
    bool TagsChanged);

/// <summary>
/// Classifies an app's stored usage report against the active use case
/// vocabulary and replaces the app's tags and proposals. Calls happen inline
/// after a successful analysis and from tag-only queue runs, so the class must
/// be idempotent: the same report always produces the same tag set.
/// </summary>
public interface IUseCaseTagger
{
    Task<UseCaseTagOutcome> TagAsync(App app, CancellationToken ct = default);
}

/// <summary>
/// Applies classifier output to the catalog. Tags are replaced (an app that
/// drops a capability loses its tag on the next run), proposals are kept in
/// sync per app, and candidates that enough distinct apps proposed are
/// promoted to real vocabulary, which then triggers a catalog-wide re-tag.
/// </summary>
public sealed class UseCaseTagger(
    ShizuDbContext db,
    UsageAnalysisOptions options,
    IUseCaseClassifier classifier,
    IUseCasePromoter promoter,
    ILogger<UseCaseTagger>? log = null) : IUseCaseTagger
{
    public async Task<UseCaseTagOutcome> TagAsync(App app, CancellationToken ct = default)
    {
        if (!options.TaggingEnabled || !options.IsConfigured)
        {
            return Failure("tagging is not configured", retryable: false);
        }

        if (string.IsNullOrEmpty(app.UsageShort) && string.IsNullOrEmpty(app.UsageMarkdown))
        {
            return Failure("no stored usage report", retryable: false);
        }

        var vocabulary = await db.UseCases
            .Where(u => u.IsActive)
            .OrderBy(u => u.Slug)
            .ToListAsync(ct);
        if (vocabulary.Count == 0)
        {
            return Failure("the use case vocabulary is empty", retryable: false);
        }

        var result = await classifier.ClassifyAsync(app, vocabulary, ct);
        if (result.Report is not { } report)
        {
            return new UseCaseTagOutcome(
                false, true, result.Error ?? "classification failed",
                result.InputTokens, result.CachedInputTokens, result.OutputTokens, result.ModelCalls,
                0, 0, false);
        }

        var applied = await ApplyAsync(app, report, vocabulary, ct);
        log?.LogDebug(
            "Classified use cases for {Slug}: {Tags} tags, {Promotions} promotions",
            app.Slug, applied.TagCount, applied.Promotions);
        return new UseCaseTagOutcome(
            true, true, null,
            result.InputTokens, result.CachedInputTokens, result.OutputTokens, result.ModelCalls,
            applied.TagCount, applied.Promotions, applied.Changed);
    }

    private async Task<(int TagCount, int Promotions, bool Changed)> ApplyAsync(
        App app, UseCaseTaggingReport report, IReadOnlyList<UseCase> vocabulary, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var bySlug = vocabulary.ToDictionary(u => u.Slug, StringComparer.Ordinal);
        var desired = report.UseCases
            .Where(bySlug.ContainsKey)
            .Select(slug => bySlug[slug])
            .DistinctBy(u => u.Id)
            .ToList();

        var desiredIds = desired.Select(u => u.Id).ToHashSet();
        var current = await db.AppUseCases
            .Where(x => x.AppId == app.Id)
            .ToListAsync(ct);
        var changed = current.Count != desiredIds.Count || current.Any(x => !desiredIds.Contains(x.UseCaseId));
        if (changed)
        {
            db.AppUseCases.RemoveRange(current);
            foreach (var useCase in desired)
            {
                db.AppUseCases.Add(new AppUseCase { AppId = app.Id, UseCaseId = useCase.Id });
            }

            app.UpdatedAt = now;
        }

        await SyncProposalsAsync(app, report, bySlug, now, ct);

        app.UseCaseTagsAnalyzedAt = now;
        app.UseCaseTagsPromptVersion = options.TagPromptVersion;
        app.UseCaseTagsModel = options.Model;

        var promotions = 0;
        if (options.AutoPromoteMinApps > 0)
        {
            // Candidate ids for freshly proposed slugs only exist after a save,
            // and promotion counts must include rows added in this run.
            await db.SaveChangesAsync(ct);
            promotions = await promoter.PromoteAboveThresholdAsync(options.AutoPromoteMinApps, now, ct);
        }

        return (desired.Count, promotions, changed);
    }

    private async Task SyncProposalsAsync(
        App app, UseCaseTaggingReport report, IReadOnlyDictionary<string, UseCase> vocabulary, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.AppUseCaseProposals
            .Include(p => p.Candidate)
            .Where(p => p.AppId == app.Id)
            .ToListAsync(ct);
        var desiredSlugs = report.Proposals.Select(p => p.Slug).ToHashSet(StringComparer.Ordinal);

        // A resolved candidate is history: drop the row only when it is stale
        // pending noise, never resurrect promoted, merged or dismissed slugs.
        foreach (var row in existing)
        {
            var keep = row.Candidate is { Status: UseCaseCandidateStatus.Pending }
                && desiredSlugs.Contains(row.Candidate.Slug);
            if (!keep)
            {
                db.AppUseCaseProposals.Remove(row);
            }
        }

        var kept = existing
            .Where(p => p.Candidate is { Status: UseCaseCandidateStatus.Pending } && desiredSlugs.Contains(p.Candidate!.Slug))
            .ToDictionary(p => p.Candidate!.Slug, StringComparer.Ordinal);

        foreach (var proposal in report.Proposals)
        {
            // The vocabulary (active or not) owns the slug; a proposal must
            // never duplicate an existing tag.
            if (vocabulary.ContainsKey(proposal.Slug) || await db.UseCases.AnyAsync(u => u.Slug == proposal.Slug, ct))
            {
                continue;
            }

            if (kept.TryGetValue(proposal.Slug, out var row))
            {
                row.Reason = proposal.Reason;
                continue;
            }

            var candidate = await db.UseCaseCandidates.FirstOrDefaultAsync(c => c.Slug == proposal.Slug, ct);
            if (candidate is null)
            {
                candidate = new UseCaseCandidate
                {
                    Slug = proposal.Slug,
                    Name = proposal.Name,
                    Status = UseCaseCandidateStatus.Pending,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.UseCaseCandidates.Add(candidate);
            }
            else if (candidate.Status != UseCaseCandidateStatus.Pending)
            {
                continue;
            }
            else
            {
                candidate.Name = proposal.Name;
                candidate.UpdatedAt = now;
            }

            db.AppUseCaseProposals.Add(new AppUseCaseProposal
            {
                AppId = app.Id,
                Candidate = candidate,
                Reason = proposal.Reason,
                CreatedAt = now,
            });
        }
    }

    private static UseCaseTagOutcome Failure(string error, bool retryable) =>
        new(false, retryable, error, 0, 0, 0, 0, 0, 0, false);
}
