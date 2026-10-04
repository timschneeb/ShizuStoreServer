using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class UseCasePromoterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ShizuDbContext _db;

    public UseCasePromoterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new ShizuDbContext(new DbContextOptionsBuilder<ShizuDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task PromoteAboveThresholdCreatesTheTagAndAssignsProposers()
    {
        var candidate = NewCandidate("wireless-debugging");
        var apps = new[] { NewApp("one"), NewApp("two"), NewApp("three") };
        await _db.SaveChangesAsync();
        foreach (var app in apps)
        {
            _db.AppUseCaseProposals.Add(new AppUseCaseProposal
            {
                AppId = app.Id,
                Candidate = candidate,
                Reason = "Toggles wireless debugging.",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await _db.SaveChangesAsync();
        var queue = new FakeQueue();
        var promoter = new UseCasePromoter(_db, queue);
        var now = DateTimeOffset.UtcNow;

        var promoted = await promoter.PromoteAboveThresholdAsync(3, now);

        Assert.Equal(1, promoted);
        var useCase = Assert.Single(_db.UseCases.Where(u => u.Slug == "wireless-debugging"));
        Assert.True(useCase.IsActive);
        Assert.Equal("wireless-debugging", useCase.Name);
        Assert.Contains("Toggles wireless debugging.", useCase.Definition);
        Assert.Equal(UseCaseCandidateStatus.Promoted, candidate.Status);
        Assert.Equal(3, _db.AppUseCases.Count(x => x.UseCaseId == useCase.Id));
        Assert.All(apps, app => Assert.Equal(now, _db.Apps.Single(a => a.Id == app.Id).UpdatedAt));
        var backfill = Assert.Single(queue.TaggingBackfills);
        Assert.True(backfill.All);
    }

    [Fact]
    public async Task PromoteAboveThresholdSkipsCandidatesBelowTheThreshold()
    {
        var candidate = NewCandidate("wireless-debugging");
        var apps = new[] { NewApp("one"), NewApp("two") };
        await _db.SaveChangesAsync();
        foreach (var app in apps)
        {
            _db.AppUseCaseProposals.Add(new AppUseCaseProposal
            {
                AppId = app.Id,
                Candidate = candidate,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await _db.SaveChangesAsync();
        var queue = new FakeQueue();
        var promoter = new UseCasePromoter(_db, queue);

        var promoted = await promoter.PromoteAboveThresholdAsync(3, DateTimeOffset.UtcNow);

        Assert.Equal(0, promoted);
        Assert.Equal(UseCaseCandidateStatus.Pending, candidate.Status);
        Assert.Empty(_db.UseCases);
        Assert.Empty(queue.TaggingBackfills);
    }

    [Fact]
    public async Task PromotingAnExistingSlugMergesInsteadOfCreatingATag()
    {
        var existing = new UseCase
        {
            Slug = "install-apps",
            Name = "Install and uninstall apps",
            Definition = "Installs apps.",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.UseCases.Add(existing);
        var candidate = NewCandidate("install-apps");
        var app = NewApp("one");
        await _db.SaveChangesAsync();
        _db.AppUseCaseProposals.Add(new AppUseCaseProposal
        {
            AppId = app.Id,
            Candidate = candidate,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        var queue = new FakeQueue();
        var promoter = new UseCasePromoter(_db, queue);
        var now = DateTimeOffset.UtcNow;

        var created = await promoter.PromoteAsync(candidate, null, null, now);

        Assert.False(created);
        Assert.Equal(UseCaseCandidateStatus.Merged, candidate.Status);
        Assert.Equal(existing.Id, candidate.MergedIntoUseCaseId);
        Assert.Single(_db.AppUseCases.Where(x => x.UseCaseId == existing.Id));
        Assert.Empty(queue.TaggingBackfills);
    }

    [Fact]
    public async Task MergeAssignsProposersToTheTargetTag()
    {
        var target = new UseCase
        {
            Slug = "file-access",
            Name = "Access protected files",
            Definition = "Reads files.",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.UseCases.Add(target);
        var candidate = NewCandidate("android-data-browser");
        var app = NewApp("one");
        await _db.SaveChangesAsync();
        _db.AppUseCaseProposals.Add(new AppUseCaseProposal
        {
            AppId = app.Id,
            Candidate = candidate,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        var promoter = new UseCasePromoter(_db, new FakeQueue());
        var now = DateTimeOffset.UtcNow;

        var merged = await promoter.MergeAsync(candidate, "file-access", now);

        Assert.NotNull(merged);
        Assert.Equal(target.Id, merged!.Id);
        Assert.Equal(UseCaseCandidateStatus.Merged, candidate.Status);
        Assert.Equal(target.Id, candidate.MergedIntoUseCaseId);
        Assert.Single(_db.AppUseCases.Where(x => x.UseCaseId == target.Id));
    }

    [Fact]
    public async Task MergeReturnsNullForAnUnknownTargetSlug()
    {
        var candidate = NewCandidate("android-data-browser");
        await _db.SaveChangesAsync();
        var promoter = new UseCasePromoter(_db, new FakeQueue());

        Assert.Null(await promoter.MergeAsync(candidate, "nope", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task PromoteHonorsNameAndDefinitionOverrides()
    {
        var candidate = NewCandidate("wireless-debugging");
        await _db.SaveChangesAsync();
        var promoter = new UseCasePromoter(_db, new FakeQueue());
        var now = DateTimeOffset.UtcNow;

        var created = await promoter.PromoteAsync(candidate, "Wireless debugging", "Toggles wireless ADB.", now);

        Assert.True(created);
        var useCase = Assert.Single(_db.UseCases.Where(u => u.Slug == "wireless-debugging"));
        Assert.Equal("Wireless debugging", useCase.Name);
        Assert.Equal("Toggles wireless ADB.", useCase.Definition);
    }

    [Fact]
    public async Task PromoteInfersTheDefinitionFromTheLatestReason()
    {
        var candidate = NewCandidate("wireless-debugging");
        var apps = new[] { NewApp("one"), NewApp("two") };
        await _db.SaveChangesAsync();
        _db.AppUseCaseProposals.Add(new AppUseCaseProposal
        {
            AppId = apps[0].Id,
            Candidate = candidate,
            Reason = "old reason",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        _db.AppUseCaseProposals.Add(new AppUseCaseProposal
        {
            AppId = apps[1].Id,
            Candidate = candidate,
            Reason = "new reason",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        var promoter = new UseCasePromoter(_db, new FakeQueue());

        await promoter.PromoteAsync(candidate, null, null, DateTimeOffset.UtcNow);

        Assert.Equal("new reason", Assert.Single(_db.UseCases).Definition);
    }

    private UseCaseCandidate NewCandidate(string slug)
    {
        var candidate = new UseCaseCandidate
        {
            Slug = slug,
            Name = slug,
            Status = UseCaseCandidateStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.UseCaseCandidates.Add(candidate);
        return candidate;
    }

    private App NewApp(string slug)
    {
        var category = new Category { Name = "Apps", Slug = $"apps-{Guid.NewGuid():N}", Section = CategorySection.Apps };
        _db.Categories.Add(category);
        var app = new App
        {
            Slug = slug,
            Name = slug,
            Url = "https://github.com/example/repo",
            Category = category,
            Availability = Availability.DirectApk,
            AddedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow,
        };
        _db.Apps.Add(app);
        return app;
    }

    private sealed class FakeQueue : IUsageAnalysisQueue
    {
        public List<(bool OnlyMissing, bool IncludeStale, bool Force, bool All, string? Slug, int? Limit)> TaggingBackfills { get; } = [];

        public Task<bool> EnqueueAsync(
            App app, bool artifactChanged, bool firstAnalysis, string? releaseRef = null, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> EnqueueForAnalyzedArtifactAsync(
            App app, bool artifactChanged, bool firstAnalysis, string? releaseRef = null, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<int> BackfillAsync(
            bool onlyMissing, bool includeStale, bool force, string? slug, int? limit, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<int> TaggingBackfillAsync(
            bool onlyMissing, bool includeStale, bool force, bool all, string? slug, int? limit,
            CancellationToken ct = default)
        {
            TaggingBackfills.Add((onlyMissing, includeStale, force, all, slug, limit));
            return Task.FromResult(0);
        }
    }
}
