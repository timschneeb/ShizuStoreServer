using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Jobs;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class UsageAnalysisQueueTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ShizuDbContext _db;
    private readonly UsageAnalysisOptions _options = new()
    {
        Enabled = true,
        MaxRunsPerDay = 100,
    };

    public UsageAnalysisQueueTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new ShizuDbContext(new DbContextOptionsBuilder<ShizuDbContext>()
            .UseSqlite(_connection)
            .Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private App NewApp(string slug, string url = "https://github.com/example/repo")
    {
        var category = new Category { Name = "Apps", Slug = $"apps-{Guid.NewGuid():N}", Section = CategorySection.Apps };
        _db.Categories.Add(category);
        var app = new App
        {
            Slug = slug,
            Name = slug,
            Url = url,
            Category = category,
            Availability = Availability.DirectApk,
            AddedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow,
        };
        _db.Apps.Add(app);
        _db.SaveChanges();
        return app;
    }

    [Fact]
    public async Task EnqueueFirstAnalysisAddsPendingRun()
    {
        var app = NewApp("first");
        var queue = new UsageAnalysisQueue(_db, _options);

        var queued = await queue.EnqueueAsync(app, artifactChanged: false, firstAnalysis: true);
        await _db.SaveChangesAsync();

        Assert.True(queued);
        var run = Assert.Single(_db.UsageAnalysisRuns);
        Assert.Equal(app.Id, run.AppId);
        Assert.Equal(UsageAnalysisStatus.Pending, run.Status);
        Assert.Equal(0, run.Attempts);
    }

    [Fact]
    public async Task EnqueueSkipsUnchangedArtifact()
    {
        var app = NewApp("unchanged");
        var queue = new UsageAnalysisQueue(_db, _options);

        Assert.False(await queue.EnqueueAsync(app, artifactChanged: false, firstAnalysis: false));
        Assert.Empty(_db.UsageAnalysisRuns);
    }

    [Fact]
    public async Task EnqueueSkipsAppsWithoutAnalyzableRepo()
    {
        var app = NewApp("norepo", "https://example.com/download");
        var queue = new UsageAnalysisQueue(_db, _options);

        Assert.False(await queue.EnqueueAsync(app, artifactChanged: true, firstAnalysis: false));
        Assert.Empty(_db.UsageAnalysisRuns);
    }

    [Fact]
    public async Task EnqueueSkipsExcludedRowsButAllowsVariants()
    {
        var excluded = NewApp("excluded");
        excluded.Availability = Availability.Excluded;
        var root = NewApp("root");
        var variant = NewApp("variant");
        variant.RootAppId = root.Id;
        _db.SaveChanges();
        var queue = new UsageAnalysisQueue(_db, _options);

        Assert.False(await queue.EnqueueAsync(excluded, artifactChanged: true, firstAnalysis: false));
        Assert.True(await queue.EnqueueAsync(variant, artifactChanged: true, firstAnalysis: false));
        await _db.SaveChangesAsync();
        var run = Assert.Single(_db.UsageAnalysisRuns);
        Assert.Equal(variant.Id, run.AppId);
        Assert.Equal(JobTrigger.Auto, run.Trigger);
    }

    [Fact]
    public async Task EnqueueSkipsVariantsWithoutAnalyzableRepo()
    {
        var root = NewApp("root");
        var variant = NewApp("variant", "https://example.com/download");
        variant.RootAppId = root.Id;
        _db.SaveChanges();
        var queue = new UsageAnalysisQueue(_db, _options);

        Assert.False(await queue.EnqueueAsync(variant, artifactChanged: true, firstAnalysis: false));
        Assert.Empty(_db.UsageAnalysisRuns);
    }

    [Fact]
    public async Task BackfillQueuesMissingVariants()
    {
        var root = NewApp("root");
        root.UsageAnalyzedAt = DateTimeOffset.UtcNow;
        root.UsagePromptVersion = _options.PromptVersion;
        root.UsageAnalysisVersion = _options.AnalysisVersion;
        var variant = NewApp("variant");
        variant.RootAppId = root.Id;
        _db.SaveChanges();
        var queue = new UsageAnalysisQueue(_db, _options);

        var added = await queue.BackfillAsync(onlyMissing: true, includeStale: false, force: false, slug: null, limit: null);

        Assert.Equal(1, added);
        var run = Assert.Single(_db.UsageAnalysisRuns);
        Assert.Equal(variant.Id, run.AppId);
        Assert.Equal(JobTrigger.Backfill, run.Trigger);
    }

    [Fact]
    public async Task EnqueueSkipsPlayRedirectAndLinkOnlyApps()
    {
        var redirect = NewApp("redirect");
        redirect.Availability = Availability.PlayRedirect;
        var linkOnly = NewApp("linkonly");
        linkOnly.Availability = Availability.LinkOnly;
        _db.SaveChanges();
        var queue = new UsageAnalysisQueue(_db, _options);

        Assert.False(await queue.EnqueueAsync(redirect, artifactChanged: true, firstAnalysis: false));
        Assert.False(await queue.EnqueueAsync(linkOnly, artifactChanged: true, firstAnalysis: false));
        Assert.Equal(0, await queue.BackfillAsync(onlyMissing: true, includeStale: false, force: false, slug: null, limit: null));
        Assert.Empty(_db.UsageAnalysisRuns);
    }

    [Fact]
    public async Task EnqueueSkipsWhenAnActiveRunExists()
    {
        var app = NewApp("active");
        _db.UsageAnalysisRuns.Add(new UsageAnalysisRun
        {
            AppId = app.Id,
            Status = UsageAnalysisStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        var queue = new UsageAnalysisQueue(_db, _options);

        Assert.False(await queue.EnqueueAsync(app, artifactChanged: true, firstAnalysis: false));
    }

    [Fact]
    public async Task BackfillQueuesMissingAndSkipsParkedFailuresUnlessForced()
    {
        var missing = NewApp("missing");
        var parked = NewApp("parked");
        var analyzed = NewApp("analyzed");
        analyzed.UsageAnalyzedAt = DateTimeOffset.UtcNow;
        analyzed.UsagePromptVersion = _options.PromptVersion;
        analyzed.UsageAnalysisVersion = _options.AnalysisVersion;
        _db.UsageAnalysisRuns.Add(new UsageAnalysisRun
        {
            AppId = parked.Id,
            Status = UsageAnalysisStatus.Failed,
            Attempts = _options.RetryMaxAttempts,
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        var queue = new UsageAnalysisQueue(_db, _options);

        var added = await queue.BackfillAsync(onlyMissing: true, includeStale: false, force: false, slug: null, limit: null);

        Assert.Equal(1, added);
        var run = Assert.Single(_db.UsageAnalysisRuns.Where(r => r.AppId == missing.Id));
        Assert.Equal(UsageAnalysisStatus.Pending, run.Status);
        Assert.DoesNotContain(_db.UsageAnalysisRuns, r => r.AppId == parked.Id && r.Status == UsageAnalysisStatus.Pending);
        Assert.DoesNotContain(_db.UsageAnalysisRuns, r => r.AppId == analyzed.Id);

        var forced = await queue.BackfillAsync(onlyMissing: true, includeStale: false, force: true, slug: "parked", limit: null);
        Assert.Equal(1, forced);
    }

    [Fact]
    public async Task BackfillStaleQueuesRowsBehindTheCurrentGeneration()
    {
        var stale = NewApp("stale");
        stale.UsageAnalyzedAt = DateTimeOffset.UtcNow;
        stale.UsagePromptVersion = _options.PromptVersion - 1;
        stale.UsageAnalysisVersion = _options.AnalysisVersion;
        await _db.SaveChangesAsync();
        var queue = new UsageAnalysisQueue(_db, _options);

        Assert.Equal(0, await queue.BackfillAsync(onlyMissing: true, includeStale: false, force: false, slug: null, limit: null));
        Assert.Equal(1, await queue.BackfillAsync(onlyMissing: false, includeStale: true, force: false, slug: null, limit: null));
        Assert.Single(_db.UsageAnalysisRuns.Where(r => r.AppId == stale.Id));
    }
}
