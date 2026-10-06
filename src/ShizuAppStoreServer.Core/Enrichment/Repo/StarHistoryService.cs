using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment.Repo;

internal sealed class StarHistoryService(
    IGitHubReleaseClient github, IGitLabReleaseClient gitlab, ShizuDbContext db, ILogger? log)
{
    // Matches the history endpoint's longest window (days=365), so the client
    // can always draw a full year without asking GitHub again.
    private const int StarHistoryLookbackDays = 365;

    // Incremental refreshes rewrite this many days behind the newest stored
    // row to absorb small drift between the live star count and the feed.
    private const int StarHistoryRewriteOverlapDays = 7;

    /// <summary>
    /// Best-effort repo metadata: stars plus the repo owner as developer
    /// identity. Popularity and developer details are refreshed even on a 304,
    /// so they stay current without a new release. Never throws (except on
    /// cancellation): a failing stats call leaves previous values alone and
    /// the parsed owner stays the developer fallback.
    /// </summary>
    internal async Task RefreshGitHubStatsAsync(App app, string owner, string repo, DateTimeOffset now, CancellationToken ct)
    {
        GitHubRepoStats? stats = null;
        try
        {
            stats = await github.GetRepoStatsAsync(owner, repo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Repo stats fetch failed for {Owner}/{Repo}.", owner, repo);
        }

        if (stats?.Stars is { } stars)
        {
            app.Stars = stars;
        }

        // The repo owner is a stable developer identity; fall back to the
        // parsed owner when the stats call failed.
        var ownerLogin = stats?.OwnerLogin ?? owner;
        app.AuthorName = ownerLogin;
        app.AuthorUrl = stats?.OwnerUrl ?? $"https://github.com/{ownerLogin}";
        app.AuthorKey = $"github:{ownerLogin.ToLowerInvariant()}";

        // The star history rides along on every pass (including release 304s)
        // with an incremental refetch, and the first enrichment backfills a
        // year of daily levels.
        await RefreshStarHistoryAsync(app, owner, repo, now, ct);
    }

    /// <summary>
    /// Best-effort star backfill: GitHub's stargazers/history feed returns the
    /// stars GAINED each week (its totals sum to the repo's stargazer count)
    /// with a per-day breakdown, while <c>app_star_days</c> stores cumulative
    /// levels beside the daily snapshot, so the gains are expanded to days and
    /// walked backward from the current star count (a day's level is the next
    /// day's level minus that day's gain, clamped at 0). Days of gap weeks are
    /// treated as gaining nothing, and a failed fetch or a missing star count
    /// leaves the table untouched.
    ///
    /// The feed is not refetched wholesale every pass: coverage that already
    /// reaches the lookback without holes, with today's row at the live star
    /// count, skips the feed entirely; anything else reads only the newest
    /// page (30 weeks) and rewrites only a few days behind the newest row,
    /// because older levels cannot move while their gains are unchanged. The
    /// full multi-page walk runs only for coverage that is missing, holed or
    /// stops short of the window, which is exactly when old rows need repair.
    /// </summary>
    private async Task RefreshStarHistoryAsync(
        App app, string owner, string repo, DateTimeOffset now, CancellationToken ct)
    {
        if (app.Stars is not { } stars)
        {
            // Levels anchor on the live star count; without it nothing can be
            // derived, so don't spend requests on the feed either.
            return;
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var windowStart = today.AddDays(-StarHistoryLookbackDays);
        var coverage = await db.AppStarDays
            .Where(d => d.AppId == app.Id && d.Day >= windowStart)
            .GroupBy(d => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Oldest = g.Min(d => d.Day),
                Newest = g.Max(d => d.Day),
            })
            .FirstOrDefaultAsync(ct);

        // Rows are only trustworthy when they run from their first day up to
        // today with no holes; anything else (fresh install, the weekly-era
        // writer's Sunday rows, a stale tail) needs the full multi-page
        // backfill, which is also the only path that repairs old rows.
        var storedSpan = coverage is null
            ? 0
            : (today.ToDateTime(TimeOnly.MinValue) - coverage.Oldest.ToDateTime(TimeOnly.MinValue)).Days + 1;
        var needsBackfill = coverage is null
            || coverage.Count != storedSpan
            || coverage.Oldest > windowStart;
        if (!needsBackfill)
        {
            // Steady state: today's row already matches the live count (the
            // runner's daily snapshot or an earlier pass today), so the feed
            // cannot change any stored level.
            var todayStars = await db.AppStarDays
                .Where(d => d.AppId == app.Id && d.Day == today)
                .Select(d => (int?)d.Stars)
                .FirstOrDefaultAsync(ct);
            if (todayStars == stars)
            {
                return;
            }
        }

        IReadOnlyList<GitHubStarWeek>? weeks;
        try
        {
            weeks = await github.GetStarHistoryAsync(
                owner, repo, StarHistoryLookbackDays, needsBackfill ? int.MaxValue : 1, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Star history fetch failed for {Owner}/{Repo}.", owner, repo);
            return;
        }

        if (weeks is not { Count: > 0 })
        {
            return;
        }

        var gains = new Dictionary<DateOnly, int>();
        var minDay = today;
        foreach (var week in weeks)
        {
            if (week.WeekStart < minDay)
            {
                minDay = week.WeekStart;
            }

            if (week.Days is { Count: > 0 })
            {
                // Days run Sunday-first; a partial current week has no gains
                // for days that have not happened yet.
                for (var i = 0; i < week.Days.Count && i < 7; i++)
                {
                    var day = week.WeekStart.AddDays(i);
                    if (day <= today)
                    {
                        gains[day] = week.Days[i];
                    }
                }
            }
            else if (week.WeekStart <= today)
            {
                gains[week.WeekStart] = week.Total;
            }
        }

        // Backfills walk the whole fetched range; incremental passes only
        // refresh a few days behind the newest stored row. Page one reaches
        // much further, but unchanged gains keep older levels fixed.
        var rewriteFrom = minDay;
        if (!needsBackfill && coverage is not null)
        {
            var tailStart = coverage.Newest.AddDays(-StarHistoryRewriteOverlapDays);
            if (tailStart > rewriteFrom)
            {
                rewriteFrom = tailStart;
            }
        }

        var existing = await db.AppStarDays
            .Where(d => d.AppId == app.Id && d.Day >= rewriteFrom && d.Day <= today)
            .ToDictionaryAsync(d => d.Day, ct);

        // Walk backward: today ends at the live star count, every earlier day
        // loses that day's gain. Rows below rewriteFrom keep whatever earlier
        // passes wrote.
        var level = stars;
        for (var day = today; day >= rewriteFrom; day = day.AddDays(-1))
        {
            level = Math.Max(0, level);
            if (existing.TryGetValue(day, out var row))
            {
                row.Stars = level;
            }
            else
            {
                db.AppStarDays.Add(new AppStarDay
                {
                    AppId = app.Id,
                    Day = day,
                    Stars = level,
                });
            }

            level -= gains.GetValueOrDefault(day);
        }
    }

    /// <summary>
    /// Best-effort GitLab popularity: star count from the project metadata.
    /// Developer identity stays path-derived (<see cref="ApplyGitLabAuthor"/>);
    /// stats refresh even when the release list fails, so they stay current
    /// without a new release. Never throws (except on cancellation).
    /// </summary>
    internal async Task RefreshGitLabStatsAsync(App app, string projectPath, CancellationToken ct)
    {
        GitLabProjectStats? stats = null;
        try
        {
            stats = await gitlab.GetProjectStatsAsync(projectPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Project stats fetch failed for {Project}.", projectPath);
        }

        if (stats?.Stars is { } stars)
        {
            app.Stars = stars;
        }
    }

    /// <summary>
    /// The top-level GitLab namespace (group or user) is a stable developer
    /// identity, so it can be read from the project path without a call.
    /// </summary>
    internal void ApplyGitLabAuthor(App app, string projectPath)
    {
        var group = projectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(group))
        {
            return;
        }

        app.AuthorName = group;
        app.AuthorUrl = $"https://gitlab.com/{group}";
        app.AuthorKey = $"gitlab:{group.ToLowerInvariant()}";
    }
}
