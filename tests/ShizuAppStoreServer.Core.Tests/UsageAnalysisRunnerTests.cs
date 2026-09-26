using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class UsageAnalysisRunnerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ShizuDbContext _db;
    private readonly List<string> _tempDirs = [];
    private readonly UsageAnalysisOptions _options = new()
    {
        Enabled = true,
        MaxRunsPerDay = 100,
        MonthlyBudgetUsd = 20m,
    };

    public UsageAnalysisRunnerTests()
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
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private App NewApp(string slug = "runner")
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
        };
        _db.Apps.Add(app);
        _db.SaveChanges();
        return app;
    }

    private UsageAnalysisRun NewRun(App app, DateTimeOffset? nextAttemptAt = null)
    {
        var run = new UsageAnalysisRun
        {
            AppId = app.Id,
            Status = UsageAnalysisStatus.Pending,
            PromptVersion = _options.PromptVersion,
            CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
            NextAttemptAt = nextAttemptAt ?? DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
        };
        _db.UsageAnalysisRuns.Add(run);
        _db.SaveChanges();
        return run;
    }

    private sealed class FakeSnapshots(RepoSnapshot? snapshot) : IRepoSnapshotProvider
    {
        public int Calls;
        public string? LastReleaseTag;
        public string? LastArtifactUrl;

        public Task<RepoSnapshot?> CreateAsync(
            App app, string? versionName, string? releaseTag = null, string? artifactUrl = null,
            CancellationToken ct = default)
        {
            Calls++;
            LastReleaseTag = releaseTag;
            LastArtifactUrl = artifactUrl;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakeAgent(UsageAgentResult result) : IUsageAnalysisAgent
    {
        public int Calls;

        public Task<UsageAgentResult> AnalyzeAsync(
            App app, UsageContext context, RepoSearch search, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private RepoSnapshot NewSnapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shizu-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "app"));
        File.WriteAllLines(Path.Combine(dir, "app", "Installer.kt"),
        [
            "package com.example",
            "fun install(pm: PackageManager) {",
            "    pm.installPackage(\"pkg\")",
            "}",
        ]);
        _tempDirs.Add(dir);
        return new RepoSnapshot(RepoForge.GitHub, "example", "repo", "https://github.com/example/repo", "abc1234", "v1.0", dir);
    }

    private static UsageAgentResult Report(string shortText = "Can install apps using PackageManager.") => new(
        new UsageReport(
            shortText,
            "Installs via `PackageManager` (`app/Installer.kt:3`).",
            "- `IPackageManager.installPackage`",
            "A fallback uses the system installer."),
        1000,
        200,
        300,
        4,
        2);

    private UsageAnalysisRunner NewRunner(IRepoSnapshotProvider snapshots, IUsageAnalysisAgent agent) =>
        new(_db, _options, snapshots, agent, NullLogger<UsageAnalysisRunner>.Instance);

    [Fact]
    public async Task SuccessStoresReportAndCostAndBumpsUpdatedAt()
    {
        var app = NewApp();
        var run = NewRun(app);
        var originalUpdatedAt = app.UpdatedAt;
        var runner = NewRunner(new FakeSnapshots(NewSnapshot()), new FakeAgent(Report()));

        Assert.True(await runner.RunNextAsync());

        var stored = _db.Apps.Single(a => a.Id == app.Id);
        Assert.Equal("Can install apps using PackageManager.", stored.UsageShort);
        Assert.Contains("Installer.kt:3", stored.UsageMarkdown);
        Assert.Contains("## Android APIs or commands used", stored.UsageMarkdown);
        Assert.Contains("## Notable details", stored.UsageMarkdown);
        Assert.Equal("Installs via `PackageManager` (`app/Installer.kt:3`).", stored.UsageMarkdownUsage);
        Assert.Equal("- `IPackageManager.installPackage`", stored.UsageMarkdownApiUsage);
        Assert.Equal("A fallback uses the system installer.", stored.UsageMarkdownNotableDetails);
        Assert.NotNull(stored.UsageAnalyzedAt);
        Assert.Equal(_options.Model, stored.UsageModel);
        Assert.Equal("abc1234", stored.UsageCommit);
        Assert.Equal("v1.0", stored.UsageReleaseRef);
        Assert.Equal(_options.PromptVersion, stored.UsagePromptVersion);
        Assert.Equal(_options.AnalysisVersion, stored.UsageAnalysisVersion);
        Assert.True(stored.UpdatedAt > originalUpdatedAt);

        var finished = _db.UsageAnalysisRuns.Single(r => r.Id == run.Id);
        Assert.Equal(UsageAnalysisStatus.Succeeded, finished.Status);
        Assert.Equal(1, finished.Attempts);
        Assert.Equal(1000, finished.InputTokens);
        Assert.Equal(200, finished.CachedInputTokens);
        Assert.Equal(300, finished.OutputTokens);
        Assert.Equal(4, finished.ToolCalls);
        Assert.Equal("github", finished.RepoForge);
        Assert.NotNull(finished.StartedAt);
        Assert.NotNull(finished.FinishedAt);
        Assert.True(finished.CostUsd > 0);
    }

    [Fact]
    public async Task PassesTheRecordedReleaseTagAndArtifactUrlToTheSnapshotProvider()
    {
        var app = NewApp("tagged");
        _db.Downloads.Add(new AppDownload
        {
            App = app,
            SigKey = "sig",
            ApkUrl = "https://github.com/example/repo/releases/download/v2.0/app.apk",
            ReleaseTag = "v2.0",
            VersionName = "1.9",
            IsPrimary = true,
            ResolvedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
        NewRun(app);
        var snapshots = new FakeSnapshots(NewSnapshot());
        var runner = NewRunner(snapshots, new FakeAgent(Report()));

        Assert.True(await runner.RunNextAsync());

        Assert.Equal("v2.0", snapshots.LastReleaseTag);
        Assert.Equal("https://github.com/example/repo/releases/download/v2.0/app.apk", snapshots.LastArtifactUrl);
    }

    [Fact]
    public async Task WritesTheConversationLogAndRecordsItsFileName()
    {
        var app = NewApp("logged");
        var run = NewRun(app);
        var logDir = Path.Combine(Path.GetTempPath(), $"shizu-runner-logs-{Guid.NewGuid():N}");
        _tempDirs.Add(logDir);
        _options.LogPath = logDir;
        var transcript = new UsageTranscript();
        transcript.AddMessage(new ChatMessage(ChatRole.User, "hello"), 20_000);
        var result = Report() with { Transcript = transcript };
        var runner = new UsageAnalysisRunner(
            _db, _options, new FakeSnapshots(NewSnapshot()), new FakeAgent(result),
            NullLogger<UsageAnalysisRunner>.Instance,
            new UsageAnalysisLogWriter(_options, NullLogger<UsageAnalysisLogWriter>.Instance));

        Assert.True(await runner.RunNextAsync());

        var stored = _db.UsageAnalysisRuns.Single(r => r.Id == run.Id);
        Assert.NotNull(stored.LogFile);
        Assert.EndsWith(".html", stored.LogFile);
        Assert.True(File.Exists(Path.Combine(logDir, stored.LogFile)));
    }

    [Fact]
    public async Task FailureRetriesWithBackoffThenParks()
    {
        var app = NewApp("retry");
        var run = NewRun(app);
        var runner = NewRunner(new FakeSnapshots(NewSnapshot()), new FakeAgent(new UsageAgentResult(null, 10, 0, 5, 0, 1)));

        for (var attempt = 1; attempt <= _options.RetryMaxAttempts; attempt++)
        {
            var before = DateTimeOffset.UtcNow;
            Assert.True(await runner.RunNextAsync());
            var stored = _db.UsageAnalysisRuns.Single(r => r.Id == run.Id);
            Assert.Equal(attempt, stored.Attempts);
            Assert.NotNull(stored.Error);
            Assert.Equal(attempt, stored.InputTokens / 10);
            if (attempt < _options.RetryMaxAttempts)
            {
                Assert.Equal(UsageAnalysisStatus.Pending, stored.Status);
                Assert.True(stored.NextAttemptAt > before);
                stored.NextAttemptAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
                await _db.SaveChangesAsync();
            }
            else
            {
                Assert.Equal(UsageAnalysisStatus.Failed, stored.Status);
                Assert.NotNull(stored.FinishedAt);
            }
        }

        // Parks: nothing due any more.
        Assert.False(await runner.RunNextAsync());
    }

    [Fact]
    public async Task MissingRepoFailsTheRunWithoutTouchingTheApp()
    {
        var app = NewApp("norepo");
        var run = NewRun(app);
        var runner = NewRunner(new FakeSnapshots(null), new FakeAgent(Report()));

        Assert.True(await runner.RunNextAsync());

        var stored = _db.Apps.Single(a => a.Id == app.Id);
        Assert.Null(stored.UsageShort);
        var finished = _db.UsageAnalysisRuns.Single(r => r.Id == run.Id);
        Assert.Contains("repository", finished.Error);
        Assert.Equal(UsageAnalysisStatus.Pending, finished.Status);
    }

    [Fact]
    public async Task RecoveryRequeuesInterruptedRunsAndParksExhaustedOnes()
    {
        var app = NewApp("interrupted");
        var run = NewRun(app);
        run.Status = UsageAnalysisStatus.Running;
        run.Attempts = 1;
        run.StartedAt = DateTimeOffset.UtcNow;
        var parkedApp = NewApp("exhausted");
        var parked = NewRun(parkedApp);
        parked.Status = UsageAnalysisStatus.Running;
        parked.Attempts = _options.RetryMaxAttempts;
        parked.StartedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        var runner = NewRunner(new FakeSnapshots(null), new FakeAgent(Report()));

        Assert.Equal(2, await runner.RecoverInterruptedAsync());

        var requeued = _db.UsageAnalysisRuns.Single(r => r.Id == run.Id);
        Assert.Equal(UsageAnalysisStatus.Pending, requeued.Status);
        Assert.Null(requeued.StartedAt);
        var failed = _db.UsageAnalysisRuns.Single(r => r.Id == parked.Id);
        Assert.Equal(UsageAnalysisStatus.Failed, failed.Status);
        Assert.Contains("interrupted", failed.Error);
        Assert.NotNull(failed.FinishedAt);
    }

    [Fact]
    public async Task DailyRunCapStopsClaiming()
    {
        var app = NewApp("capped");
        _options.MaxRunsPerDay = 1;
        _db.UsageAnalysisRuns.Add(new UsageAnalysisRun
        {
            AppId = app.Id,
            Status = UsageAnalysisStatus.Succeeded,
            Attempts = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow,
        });
        NewRun(app);
        var runner = NewRunner(new FakeSnapshots(NewSnapshot()), new FakeAgent(Report()));

        Assert.False(await runner.RunNextAsync());
    }

    [Fact]
    public void CostUsesCachedRateForCachedInputTokens()
    {
        var cost = UsageAnalysisRunner.ComputeCost(1_000_000, 250_000, 100_000, _options);

        // 750k uncached * 0.14 + 250k cached * 0.0028 + 100k output * 0.28, per million.
        Assert.Equal(0.1337m, cost);
    }
}
