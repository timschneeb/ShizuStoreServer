using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class UseCaseTaggerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ShizuDbContext _db;
    private readonly UsageAnalysisOptions _options = new() { Enabled = true };

    public UseCaseTaggerTests()
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
    public async Task AppliesTagsProposalsAndStampsTheApp()
    {
        AddVocabulary("install-apps", "file-access");
        var app = NewApp("tagged");
        app.UsageShort = "Can install apps.";
        await _db.SaveChangesAsync();
        var classifier = new FakeClassifier
        {
            Result = new UseCaseTagResult(
                new UseCaseTaggingReport(
                    ["install-apps"],
                    [new UseCaseProposal("wireless-debugging", "Wireless Debugging", "Toggles ADB.")]),
                10, 2, 20, 1),
        };
        var tagger = new UseCaseTagger(_db, _options, classifier, new FakePromoter());

        var outcome = await tagger.TagAsync(app);
        await _db.SaveChangesAsync();

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.TagCount);
        Assert.True(outcome.TagsChanged);
        Assert.Equal(10, outcome.InputTokens);
        Assert.Single(_db.AppUseCases.Where(x => x.AppId == app.Id));
        Assert.Equal("install-apps", _db.AppUseCases.Single(x => x.AppId == app.Id).UseCase!.Slug);
        var candidate = Assert.Single(_db.UseCaseCandidates.Where(c => c.Slug == "wireless-debugging"));
        Assert.Equal(UseCaseCandidateStatus.Pending, candidate.Status);
        var proposal = Assert.Single(_db.AppUseCaseProposals.Where(p => p.AppId == app.Id));
        Assert.Equal("Toggles ADB.", proposal.Reason);
        Assert.NotNull(app.UseCaseTagsAnalyzedAt);
        Assert.Equal(_options.TagPromptVersion, app.UseCaseTagsPromptVersion);
        Assert.Equal(_options.Model, app.UseCaseTagsModel);
    }

    [Fact]
    public async Task ReplacesTagsAndDropsCapabilitiesThatDisappeared()
    {
        AddVocabulary("install-apps", "file-access");
        var app = NewApp("tagged");
        app.UsageShort = "Can install apps.";
        await _db.SaveChangesAsync();
        _db.AppUseCases.Add(new AppUseCase { AppId = app.Id, UseCaseId = _db.UseCases.Single(u => u.Slug == "install-apps").Id });
        _db.AppUseCases.Add(new AppUseCase { AppId = app.Id, UseCaseId = _db.UseCases.Single(u => u.Slug == "file-access").Id });
        await _db.SaveChangesAsync();
        var classifier = new FakeClassifier
        {
            Result = new UseCaseTagResult(new UseCaseTaggingReport(["file-access"], []), 1, 0, 1, 1),
        };
        var tagger = new UseCaseTagger(_db, _options, classifier, new FakePromoter());

        var outcome = await tagger.TagAsync(app);
        await _db.SaveChangesAsync();

        Assert.True(outcome.TagsChanged);
        var assignment = Assert.Single(_db.AppUseCases.Where(x => x.AppId == app.Id));
        Assert.Equal("file-access", assignment.UseCase!.Slug);
    }

    [Fact]
    public async Task KeepsUpdatedAtWhenTheTagSetIsUnchanged()
    {
        AddVocabulary("install-apps");
        var app = NewApp("tagged");
        app.UsageShort = "Can install apps.";
        await _db.SaveChangesAsync();
        _db.AppUseCases.Add(new AppUseCase { AppId = app.Id, UseCaseId = _db.UseCases.Single().Id });
        await _db.SaveChangesAsync();
        var before = app.UpdatedAt;
        var classifier = new FakeClassifier
        {
            Result = new UseCaseTagResult(new UseCaseTaggingReport(["install-apps"], []), 1, 0, 1, 1),
        };
        var tagger = new UseCaseTagger(_db, _options, classifier, new FakePromoter());

        var outcome = await tagger.TagAsync(app);
        await _db.SaveChangesAsync();

        Assert.False(outcome.TagsChanged);
        Assert.Equal(before, app.UpdatedAt);
    }

    [Fact]
    public async Task ResolvedCandidatesAreNeverResurrected()
    {
        AddVocabulary("install-apps");
        var app = NewApp("tagged");
        app.UsageShort = "Can install apps.";
        var candidate = new UseCaseCandidate
        {
            Slug = "wireless-debugging",
            Name = "Wireless Debugging",
            Status = UseCaseCandidateStatus.Dismissed,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.UseCaseCandidates.Add(candidate);
        await _db.SaveChangesAsync();
        var classifier = new FakeClassifier
        {
            Result = new UseCaseTagResult(
                new UseCaseTaggingReport([], [new UseCaseProposal("wireless-debugging", "Wireless Debugging", null)]),
                1, 0, 1, 1),
        };
        var tagger = new UseCaseTagger(_db, _options, classifier, new FakePromoter());

        var outcome = await tagger.TagAsync(app);
        await _db.SaveChangesAsync();

        Assert.True(outcome.Succeeded);
        Assert.Equal(UseCaseCandidateStatus.Dismissed, candidate.Status);
        Assert.Empty(_db.AppUseCaseProposals.Where(p => p.AppId == app.Id));
    }

    [Fact]
    public async Task FailsTerminallyWhenTheAppHasNoStoredReport()
    {
        AddVocabulary("install-apps");
        var app = NewApp("empty");
        await _db.SaveChangesAsync();
        var classifier = new FakeClassifier();
        var tagger = new UseCaseTagger(_db, _options, classifier, new FakePromoter());

        var outcome = await tagger.TagAsync(app);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Retryable);
        Assert.Equal(0, classifier.Calls);
        Assert.Empty(_db.AppUseCases.Where(x => x.AppId == app.Id));
    }

    [Fact]
    public async Task FailsTerminallyWhenTheVocabularyIsEmpty()
    {
        var app = NewApp("empty");
        app.UsageShort = "Can install apps.";
        await _db.SaveChangesAsync();
        var classifier = new FakeClassifier();
        var tagger = new UseCaseTagger(_db, _options, classifier, new FakePromoter());

        var outcome = await tagger.TagAsync(app);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Retryable);
        Assert.Equal(0, classifier.Calls);
    }

    [Fact]
    public async Task ClassifierFailureKeepsTokensAndIsRetryable()
    {
        AddVocabulary("install-apps");
        var app = NewApp("tagged");
        app.UsageShort = "Can install apps.";
        await _db.SaveChangesAsync();
        var classifier = new FakeClassifier
        {
            Result = new UseCaseTagResult(null, 11, 3, 22, 1, "the response was empty"),
        };
        var tagger = new UseCaseTagger(_db, _options, classifier, new FakePromoter());

        var outcome = await tagger.TagAsync(app);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.Retryable);
        Assert.Contains("the response was empty", outcome.Error!);
        Assert.Equal(11, outcome.InputTokens);
        Assert.Equal(3, outcome.CachedInputTokens);
        Assert.Equal(22, outcome.OutputTokens);
        Assert.Empty(_db.AppUseCases.Where(x => x.AppId == app.Id));
    }

    [Fact]
    public async Task DropsSlugsThatAreNotInTheActiveVocabulary()
    {
        AddVocabulary("install-apps");
        var app = NewApp("tagged");
        app.UsageShort = "Can install apps.";
        await _db.SaveChangesAsync();
        var classifier = new FakeClassifier
        {
            Result = new UseCaseTagResult(new UseCaseTaggingReport(["retired-tag"], []), 1, 0, 1, 1),
        };
        var tagger = new UseCaseTagger(_db, _options, classifier, new FakePromoter());

        var outcome = await tagger.TagAsync(app);

        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.TagCount);
        Assert.False(outcome.TagsChanged);
    }

    [Fact]
    public async Task AutoPromotionRunsAfterTheTagsArePersisted()
    {
        _options.AutoPromoteMinApps = 3;
        AddVocabulary("install-apps");
        var app = NewApp("tagged");
        app.UsageShort = "Can install apps.";
        await _db.SaveChangesAsync();
        var classifier = new FakeClassifier
        {
            Result = new UseCaseTagResult(new UseCaseTaggingReport(["install-apps"], []), 1, 0, 1, 1),
        };
        var promoter = new FakePromoter { Promotions = 2 };
        var tagger = new UseCaseTagger(_db, _options, classifier, promoter);

        var outcome = await tagger.TagAsync(app);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, promoter.ThresholdCalls);
        Assert.Equal(2, outcome.Promotions);
        Assert.Equal(3, promoter.LastMinApps);
    }

    private void AddVocabulary(params string[] slugs)
    {
        foreach (var slug in slugs)
        {
            _db.UseCases.Add(new UseCase
            {
                Slug = slug,
                Name = slug,
                Definition = $"{slug} definition",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
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

    private sealed class FakeClassifier : IUseCaseClassifier
    {
        public UseCaseTagResult Result { get; set; } = new(null, 0, 0, 0, 0, "none");

        public int Calls { get; private set; }

        public Task<UseCaseTagResult> ClassifyAsync(
            App app, IReadOnlyList<UseCase> vocabulary, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakePromoter : IUseCasePromoter
    {
        public int ThresholdCalls { get; private set; }

        public int Promotions { get; set; }

        public int LastMinApps { get; private set; }

        public Task<int> PromoteAboveThresholdAsync(int minApps, DateTimeOffset now, CancellationToken ct = default)
        {
            ThresholdCalls++;
            LastMinApps = minApps;
            return Task.FromResult(Promotions);
        }

        public Task<bool> PromoteAsync(
            UseCaseCandidate candidate, string? name, string? definition, DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<UseCase?> MergeAsync(
            UseCaseCandidate candidate, string targetSlug, DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<UseCase?>(null);
    }
}
