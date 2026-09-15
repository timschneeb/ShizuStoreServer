using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.History;
using ShizuAppStoreServer.Core.Parsing;
using ShizuAppStoreServer.Core.Sync;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>
/// Hermetic sync-loop tests: a temp <c>git</c> repo (fixed commit dates, no
/// remote, which also proves the best-effort fetch tolerates missing
/// remotes) + SQLite + a stub <see cref="IEnrichmentRunner"/> that marks
/// rows checked. Needs the <c>git</c> CLI (early return otherwise, like
/// <c>RealAapt2SmokeTests</c>).
/// </summary>
public sealed class SyncServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private const string ReadmeV1 = """
        # Test list

        ## Apps

        ### Audio

        * [Tuner](https://github.com/acme/tuner) - Tuner description `GPL-3.0`
        * [MicUp](https://github.com/acme/micup) - MicUp description `MIT`
        """;

    private const string ClosedV1 = """
        # Closed

        ## Closed-source apps

        ### Tools

        * [Widget](https://example.com/widget) - Widget description `Proprietary`
        """;

    private const string ArchivedTuner = """
        # Archived

        ## Apps

        ### Audio

        * [Tuner](https://github.com/acme/tuner) - Old tuner `GPL-3.0`
        """;

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "shizu-sync-test-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ShizuDbContext _db;
    private readonly FakeRunner _runner;

    public SyncServiceTests()
    {
        _connection.Open();
        _db = new ShizuDbContext(new DbContextOptionsBuilder<ShizuDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _runner = new FakeRunner(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_repo))
        {
            try
            {
                Directory.Delete(_repo, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Creates the temp repo; false when <c>git</c> is unavailable (test returns early).</summary>
    private bool InitRepo()
    {
        if (!GitAvailable())
        {
            return false;
        }

        Directory.CreateDirectory(_repo);
        Git("init");
        return true;
    }

    [Fact]
    public async Task FirstPassUpsertsEnrichesAndRecordsRun()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        var head = Head();

        var result = await Service().RunAsync("scheduled", fullRecheck: false, T0);

        Assert.Null(result.Error);
        Assert.False(result.Skipped);
        Assert.Equal("scheduled", result.Trigger);
        Assert.Equal(head, result.HeadCommit);
        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Equal(2, result.Enriched);
        Assert.Equal(0, result.DrainedRequests);
        Assert.Equal(2, _runner.Calls.Count);
        Assert.All(_runner.Calls, c => Assert.False(c.Force));

        var run = Assert.Single(_db.SyncRuns.ToList());
        Assert.Equal("scheduled", run.Trigger);
        Assert.Equal(head, run.HeadCommit);
        Assert.Equal(2, run.Added);
        Assert.NotNull(run.FinishedAt);

        // Git history backfill: entry timestamps come from the commit, not "now".
        var tuner = _db.Apps.Single(a => a.Slug == "tuner");
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero), tuner.AddedAt);
        Assert.Equal(2, _db.Apps.Count());
        // CLOSED_SOURCE.md is committed but ignored: no Widget row.
        Assert.DoesNotContain(_db.Apps, a => a.Slug == "widget");
    }

    [Fact]
    public async Task SecondPassSameHeadSkipsEntirely()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));

        var first = await Service().RunAsync("scheduled", fullRecheck: false, T0);
        Assert.False(first.Skipped);

        var second = await Service().RunAsync("scheduled", fullRecheck: false, T0.AddMinutes(16));

        Assert.True(second.Skipped);
        Assert.Null(second.Error);
        Assert.Single(_db.SyncRuns.ToList()); // skipped passes write nothing
        Assert.Equal(2, _runner.Calls.Count); // nothing due (fake marks rows checked)
    }

    [Fact]
    public async Task WebhookRequestForcesPassAndIsMarkedProcessed()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);

        _db.SyncRequests.Add(new SyncRequest { RequestedAt = T0, Reason = "test hook" });
        await _db.SaveChangesAsync();

        var result = await Service().RunAsync("scheduled", fullRecheck: false, T0.AddMinutes(16));

        Assert.Equal("webhook", result.Trigger);
        Assert.False(result.Skipped);
        Assert.Equal(1, result.DrainedRequests);
        var request = Assert.Single(_db.SyncRequests.ToList());
        Assert.True(request.Processed);
        Assert.NotNull(request.ProcessedAt);
        Assert.Equal(2, _db.SyncRuns.Count());
    }

    [Fact]
    public async Task ListChangeEnrichesOnlyNewApps()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);

        Commit("2026-01-10T10:00:00+00:00",
            ("README.md", ReadmeV1 + "\n* [Drum](https://github.com/acme/drum) - Drum description `Apache-2.0`\n"));

        // +1h: within the 24h success window, so only the new app is due.
        var result = await Service().RunAsync("scheduled", fullRecheck: false, T0.AddHours(1));

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.Equal(3, _runner.Calls.Count); // only the new app was due
        var drum = _db.Apps.Single(a => a.Slug == "drum");
        Assert.Equal(new DateTimeOffset(2026, 1, 10, 10, 0, 0, TimeSpan.Zero), drum.AddedAt);
    }

    [Fact]
    public async Task ArchivedEntryIsExcludedAndResurrectionClears()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00",
            ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1), ("pages/ARCHIVED.md", ArchivedTuner));

        var archived = await Service().RunAsync("scheduled", fullRecheck: false, T0);

        Assert.Equal(1, archived.ArchivedChanged);
        var tuner = _db.Apps.Single(a => a.Slug == "tuner");
        Assert.Equal(Availability.Excluded, tuner.Availability);
        Assert.Equal(SyncService.ArchivedReason, tuner.ExcludedReason);

        Commit("2026-01-10T10:00:00+00:00", ("pages/ARCHIVED.md", "# Archived\n\nEmpty.\n"));
        var callsBefore = _runner.Calls.Count;
        var resurrected = await Service().RunAsync("scheduled", fullRecheck: false, T0.AddHours(1));

        Assert.Equal(1, resurrected.ArchivedChanged);
        _db.ChangeTracker.Clear();
        tuner = _db.Apps.Single(a => a.Slug == "tuner");
        Assert.Null(tuner.ExcludedReason);
        // Clearing nulled LastCheckedAt mid-pass, forcing re-enrichment (the
        // fake stamps it again, proof the app was re-run, not skipped).
        Assert.Contains(_runner.Calls.Skip(callsBefore), c => c.AppId == tuner.Id);
    }

    [Fact]
    public async Task OnlyDueAppsAreEnriched()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);

        // Everything fresh except tuner (stale success window: 24h).
        var tuner = _db.Apps.Single(a => a.Slug == "tuner");
        tuner.LastCheckedAt = T0.AddHours(-25);
        await _db.SaveChangesAsync();
        var callsBefore = _runner.Calls.Count;

        var result = await Service().RunAsync("scheduled", fullRecheck: false, T0);

        Assert.False(result.Skipped);
        Assert.Equal(1, _runner.Calls.Count - callsBefore);
        Assert.Equal(tuner.Id, _runner.Calls[^1].AppId);
    }

    [Fact]
    public async Task NightlyFullRecheckForcesAllApps()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);
        var callsBefore = _runner.Calls.Count;

        var result = await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(16));

        Assert.False(result.Skipped);
        Assert.Equal("nightly", result.Trigger);
        Assert.Equal(2, _runner.Calls.Count - callsBefore);
        Assert.All(_runner.Calls.Skip(callsBefore), c => Assert.True(c.Force));
    }

    [Fact]
    public async Task PollChangedAppIsForceEnrichedInsideRecheckWindow()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);

        // +1h: within the 24h success window, so nothing is due; the poll
        // flags tuner and the pass force-enriches it instead of skipping.
        var tuner = _db.Apps.Single(a => a.Slug == "tuner");
        var poll = new FakePoller(new HashSet<long> { tuner.Id });
        var callsBefore = _runner.Calls.Count;
        var result = await Service(poll: poll).RunAsync("scheduled", fullRecheck: false, T0.AddHours(1));

        Assert.False(result.Skipped);
        Assert.Equal(1, poll.Calls);
        var call = Assert.Single(_runner.Calls.Skip(callsBefore));
        Assert.Equal(tuner.Id, call.AppId);
        Assert.True(call.Force);
        Assert.Equal(1, result.Enriched);
    }

    [Fact]
    public async Task VariantsAreNeverSelectedForEnrichment()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);

        var tuner = _db.Apps.Single(a => a.Slug == "tuner");
        var variant = new App
        {
            Slug = "com-acme-plugin",
            Name = tuner.Name,
            Url = tuner.Url,
            Listing = tuner.Listing,
            Type = tuner.Type,
            CategoryId = tuner.CategoryId,
            AddedAt = T0,
            UpdatedAt = T0,
            RootAppId = tuner.Id,
            PackageName = "com.acme.plugin",
            LastCheckedAt = T0.AddHours(-100),
        };
        _db.Apps.Add(variant);
        await _db.SaveChangesAsync();

        // The variant looks stale, but variants only ride their root's pass.
        var due = await Service().RunAsync("scheduled", fullRecheck: false, T0);
        Assert.True(due.Skipped);
        Assert.DoesNotContain(_runner.Calls, c => c.AppId == variant.Id);

        var callsBefore = _runner.Calls.Count;
        await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(16));
        Assert.Equal(2, _runner.Calls.Count - callsBefore);
        Assert.DoesNotContain(_runner.Calls.Skip(callsBefore), c => c.AppId == variant.Id);
    }

    [Fact]
    public async Task ShizukuGateExcludesDirectApkWithoutPermissionAndAuditsIt()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        var tuner = await SeedDirectApkAsync();

        await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(16));

        Assert.Equal(Availability.Excluded, tuner.Availability);
        Assert.Equal(ShizukuPermission.Reason, tuner.ExcludedReason);
        Assert.Contains(_db.SyncIssues.ToList(), i => i.Rule == ShizukuPermission.Rule && i.Slug == "tuner");
    }

    [Fact]
    public async Task ShizukuGateKeepsAllowedPackageAndSuppressesItsIssue()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        var tuner = await SeedDirectApkAsync();
        _db.PackageExceptions.Add(new PackageException
        {
            PackageName = "com.acme.tuner",
            Action = PackageExceptionAction.Allow,
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        await _db.SaveChangesAsync();

        await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(16));

        Assert.Equal(Availability.DirectApk, tuner.Availability);
        Assert.Null(tuner.ExcludedReason);
        Assert.DoesNotContain(_db.SyncIssues.ToList(), i => i.Rule == ShizukuPermission.Rule);
    }

    [Fact]
    public async Task ShizukuGateDontAuditExcludesWithoutAnIssue()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        var tuner = await SeedDirectApkAsync();
        _db.PackageExceptions.Add(new PackageException
        {
            PackageName = "com.acme.tuner",
            Action = PackageExceptionAction.DontAudit,
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        await _db.SaveChangesAsync();

        await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(16));

        Assert.Equal(Availability.Excluded, tuner.Availability);
        Assert.Equal(ShizukuPermission.Reason, tuner.ExcludedReason);
        Assert.DoesNotContain(_db.SyncIssues.ToList(), i => i.Rule == ShizukuPermission.Rule);
    }

    [Fact]
    public async Task ShizukuGateExcludeOverrideActsAsAllow()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        var tuner = await SeedDirectApkAsync();
        tuner.ExcludeOverride = true;
        await _db.SaveChangesAsync();

        await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(16));

        Assert.Equal(Availability.DirectApk, tuner.Availability);
        Assert.Null(tuner.ExcludedReason);
    }

    [Fact]
    public async Task ShizukuGateAutoHealsWhenAnApkDeclaresThePermission()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        var tuner = await SeedDirectApkAsync();
        await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(16));
        Assert.Equal(Availability.Excluded, tuner.Availability);

        tuner.Permissions = ["moe.shizuku.manager.permission.API_V23"];
        tuner.LastCheckedAt = null;
        await _db.SaveChangesAsync();
        await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(32));

        Assert.Equal(Availability.DirectApk, tuner.Availability);
        Assert.Null(tuner.ExcludedReason);
        Assert.DoesNotContain(_db.SyncIssues.ToList(), i => i.Rule == ShizukuPermission.Rule);
    }

    [Fact]
    public async Task ShizukuGateNeverClobbersAnotherExclusionReason()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        var tuner = await SeedDirectApkAsync();
        tuner.Availability = Availability.Excluded;
        tuner.ExcludedReason = "Play Store is the only source; listed for transparency.";
        await _db.SaveChangesAsync();

        await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(16));

        Assert.Equal("Play Store is the only source; listed for transparency.", tuner.ExcludedReason);
        Assert.DoesNotContain(_db.SyncIssues.ToList(), i => i.Rule == ShizukuPermission.Rule);
    }

    [Fact]
    public async Task ShizukuExcludedRowsStaySelectedForEnrichment()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        var tuner = await SeedDirectApkAsync();
        await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(16));
        Assert.Equal(Availability.Excluded, tuner.Availability);

        var callsBefore = _runner.Calls.Count;
        await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(32));

        Assert.Contains(_runner.Calls.Skip(callsBefore), c => c.AppId == tuner.Id);
    }

    /// <summary>Runs a first pass, then turns the tuner row into an analyzed DirectApk without Shizuku.</summary>
    private async Task<App> SeedDirectApkAsync()
    {
        await Service().RunAsync("scheduled", fullRecheck: false, T0);
        var app = _db.Apps.Single(a => a.Slug == "tuner");
        app.Availability = Availability.DirectApk;
        app.PackageName = "com.acme.tuner";
        app.Permissions = ["android.permission.INTERNET"];
        await _db.SaveChangesAsync();
        return app;
    }

    [Fact]
    public async Task MissingListPathYieldsErrorRun()
    {
        if (!InitRepo())
        {
            return;
        }

        var result = await Service(listPath: Path.Combine(_repo, "nope"))
            .RunAsync("scheduled", fullRecheck: false, T0);

        Assert.False(result.Skipped);
        Assert.NotNull(result.Error);
        var run = Assert.Single(_db.SyncRuns.ToList());
        Assert.NotNull(run.Error);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task FailedPassLeavesRequestsUnprocessed()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);

        File.Delete(Path.Combine(_repo, "README.md"));
        _db.SyncRequests.Add(new SyncRequest { RequestedAt = T0, Reason = "hook during outage" });
        await _db.SaveChangesAsync();

        var result = await Service().RunAsync("scheduled", fullRecheck: false, T0.AddMinutes(16));

        Assert.NotNull(result.Error);
        var request = Assert.Single(_db.SyncRequests.ToList());
        Assert.False(request.Processed); // retried next loop
    }

    [Fact]
    public async Task RunLogRecordsOneLinePerAppAndTheIssuesSnapshot()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        var log = new RecordingRunLog();
        await Service(runLog: log).RunAsync("scheduled", fullRecheck: false, T0);

        var begin = Assert.Single(log.Begins);
        Assert.Equal("scheduled", begin.Trigger);
        Assert.False(begin.FullRecheck);
        Assert.Equal(2, begin.AppCount);
        Assert.Equal(2, log.Apps.Count);
        Assert.Contains(log.Apps, a => a.Slug == "tuner");
        Assert.Contains(log.Apps, a => a.Result.Outcome == EnrichOutcome.Enriched);
        var end = Assert.Single(log.Ends);
        Assert.Equal(2, end.Enriched);
        Assert.Equal(0, end.Failed);
        var issues = Assert.Single(log.IssueSnapshots);
        Assert.Equal(1, issues.RunId);
    }

    [Fact]
    public async Task RunLogRecordsSkippedPass()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);

        var log = new RecordingRunLog();
        await Service(runLog: log).RunAsync("scheduled", fullRecheck: false, T0.AddMinutes(16));

        Assert.Empty(log.Begins);
        Assert.Equal("nothing due", Assert.Single(log.Skips).Reason);
    }

    private SyncService Service(string? listPath = null, IReleasePoller? poll = null, IRunLog? runLog = null) => new(
        _db,
        new CatalogUpserter(_db),
        new GitHistoryService(),
        _runner,
        poll ?? new FakePoller(),
        new ThrowingRenderer(),
        new SyncOptions { ListPath = listPath ?? _repo },
        new EnrichmentOptions { MaxParallelism = 1 },
        runLog);

    [Fact]
    public async Task RunnerCrashSurfacesMessageInResult()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        var service = new SyncService(
            _db,
            new CatalogUpserter(_db),
            new GitHistoryService(),
            new ThrowingRunner(),
            new FakePoller(),
            new ThrowingRenderer(),
            new SyncOptions { ListPath = _repo },
            new EnrichmentOptions { MaxParallelism = 1 });

        var result = await service.RunAsync("scheduled", fullRecheck: false, T0);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Failed);
        Assert.Equal(2, result.FailedMessages.Count);
        Assert.All(result.FailedMessages, m => Assert.Contains("boom", m));
    }

    /// <summary>Stub poller: returns a canned changed set, records calls.</summary>
    private sealed class FakePoller(IReadOnlySet<long>? changed = null) : IReleasePoller
    {
        public int Calls;
        public Task<IReadOnlySet<long>> FindChangedAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlySet<long>>(changed ?? new HashSet<long>());
        }
    }

    private sealed class ThrowingRunner : IEnrichmentRunner
    {
        public Task<EnrichResult> EnrichAsync(long appId, bool force, DateTimeOffset now, CancellationToken ct) =>
            throw new InvalidOperationException("boom");

        public Task<PrepareIconResult> PrepareIconRefreshAsync(
            long appId, string batchWorkDir, string prefix, CancellationToken ct, bool force = false) =>
            throw new InvalidOperationException("boom");

        public Task<EnrichResult> CommitIconRefreshAsync(long appId, byte[]? png, CancellationToken ct, bool force = false, bool isAdaptive = false) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class ThrowingRenderer : IPaparazziRenderer
    {
        public Task<byte[]> RenderAsync(
            string stagedResDir, string drawableName, int sizePx, CancellationToken ct = default) =>
            throw new InvalidOperationException("renderer must stay untouched");

        public Task<byte[]?[]> RenderBatchAsync(
            string stagedResDir, IReadOnlyList<BatchRenderRequest> batch, int sizePx, CancellationToken ct = default) =>
            throw new InvalidOperationException("renderer must stay untouched");
    }

    /// <summary>Records run-log calls; the run log itself is file IO, tested separately.</summary>
    private sealed class RecordingRunLog : IRunLog
    {
        private readonly object _gate = new();

        public List<(string Trigger, bool FullRecheck, DateTimeOffset Now, int AppCount)> Begins { get; } = [];
        public List<(string Slug, string? DisplayName, EnrichResult Result)> Apps { get; } = [];
        public List<(string Trigger, DateTimeOffset Now, string Reason)> Skips { get; } = [];
        public List<(DateTimeOffset Now, int Enriched, int UpToDate, int Failed)> Ends { get; } = [];
        public List<(long? RunId, string? Head, IReadOnlyList<SyncIssue> Issues)> IssueSnapshots { get; } = [];

        public void Begin(string trigger, bool fullRecheck, DateTimeOffset now, int appCount)
        {
            lock (_gate)
            {
                Begins.Add((trigger, fullRecheck, now, appCount));
            }
        }

        public void App(string slug, string? displayName, EnrichResult result)
        {
            lock (_gate)
            {
                Apps.Add((slug, displayName, result));
            }
        }

        public void Skip(string trigger, DateTimeOffset now, string reason)
        {
            lock (_gate)
            {
                Skips.Add((trigger, now, reason));
            }
        }

        public void End(DateTimeOffset now, int enriched, int upToDate, int failed)
        {
            lock (_gate)
            {
                Ends.Add((now, enriched, upToDate, failed));
            }
        }

        public void Issues(long? runId, string? head, IReadOnlyList<SyncIssue> issues)
        {
            lock (_gate)
            {
                IssueSnapshots.Add((runId, head, issues));
            }
        }
    }

    /// <summary>Stub runner: records calls and marks rows checked (serial; shares the pass DbContext).</summary>
    private sealed class FakeRunner(ShizuDbContext db) : IEnrichmentRunner
    {
        public List<(long AppId, bool Force)> Calls { get; } = [];

        public Func<long, PrepareIconResult>? PrepareHook;
        public List<(long AppId, byte[]? Png)> Commits { get; } = [];

        public Task<EnrichResult> EnrichAsync(long appId, bool force, DateTimeOffset now, CancellationToken ct)
        {
            Calls.Add((appId, force));
            var app = db.Apps.Find(appId);
            if (app is not null)
            {
                app.LastCheckedAt = now;
                db.SaveChanges();
            }

            return Task.FromResult(new EnrichResult(EnrichOutcome.Enriched, null));
        }

        public Task<PrepareIconResult> PrepareIconRefreshAsync(
            long appId, string batchWorkDir, string prefix, CancellationToken ct, bool force = false) =>
            Task.FromResult(PrepareHook?.Invoke(appId)
                ?? new PrepareIconResult(EnrichOutcome.UpToDate, null, null));

        public Task<EnrichResult> CommitIconRefreshAsync(long appId, byte[]? png, CancellationToken ct, bool force = false, bool isAdaptive = false)
        {
            Commits.Add((appId, png));
            return Task.FromResult(new EnrichResult(EnrichOutcome.Enriched, null));
        }
    }

    private sealed class CannedBatchRenderer(byte[] png, bool throwAll = false) : IPaparazziRenderer
    {
        public List<IReadOnlyList<BatchRenderRequest>> Calls { get; } = [];

        public Task<byte[]> RenderAsync(
            string stagedResDir, string drawableName, int sizePx, CancellationToken ct = default) =>
            Task.FromResult(png);

        public Task<byte[]?[]> RenderBatchAsync(
            string stagedResDir, IReadOnlyList<BatchRenderRequest> batch, int sizePx, CancellationToken ct = default)
        {
            Calls.Add(batch);
            if (throwAll)
            {
                throw new PaparazziException("tool exploded");
            }

            return Task.FromResult(batch.Select(_ => (byte[]?)png).ToArray());
        }
    }

    private void MarkDirectApk()
    {
        foreach (var app in _db.Apps)
        {
            app.Availability = Availability.DirectApk;
            app.IconHash = "old";
            _db.Downloads.Add(new AppDownload
            {
                AppId = app.Id,
                Source = SourceKind.GitHub,
                ApkUrl = "https://example.com/" + app.Slug + ".apk",
                VersionCode = 1,
                SigKey = "url:https://example.com/" + app.Slug + ".apk",
                IsPrimary = true,
                ResolvedAt = T0,
            });
        }

        _db.SaveChanges();
    }

    private SyncService RefreshService(IEnrichmentRunner runner, IPaparazziRenderer renderer) => new(
        _db,
        new CatalogUpserter(_db),
        new GitHistoryService(),
        runner,
        new FakePoller(),
        renderer,
        new SyncOptions { ListPath = _repo },
        new EnrichmentOptions { MaxParallelism = 2 });

    [Fact]
    public async Task RefreshIconsBatchesXmlRendersIntoOneGradleCall()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);
        MarkDirectApk();
        var staged = _db.Apps.OrderBy(a => a.Id).Take(2).ToList();

        _runner.PrepareHook = id => staged.Any(a => a.Id == id)
            ? new PrepareIconResult(EnrichOutcome.UpToDate, null, new PendingBatchIcon($"b{id}_0", null))
            : new PrepareIconResult(EnrichOutcome.UpToDate, null, null);
        var renderer = new CannedBatchRenderer([7]);
        var runsBefore = _db.SyncRuns.Count();
        var result = await RefreshService(_runner, renderer).RefreshIconsAsync();

        Assert.Equal(2, result.Checked);
        Assert.Equal(2, result.Refreshed);
        Assert.Equal(0, result.AlreadyCurrent);
        Assert.Equal(0, result.Failed);
        var batch = Assert.Single(renderer.Calls); // one Gradle invocation
        Assert.Equal(2, batch.Count);
        foreach (var app in staged)
        {
            Assert.Contains(batch, r => r.DrawableName == $"b{app.Id}_0");
        }

        Assert.Equal(2, _runner.Commits.Count);
        Assert.All(_runner.Commits, c => Assert.Equal([7], c.Png));
        Assert.Equal(runsBefore, _db.SyncRuns.Count()); // no run row written
    }

    [Fact]
    public async Task RefreshIconsKeepsFilesWhenBatchRenderExplodes()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1), ("pages/CLOSED_SOURCE.md", ClosedV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);
        MarkDirectApk();

        _runner.PrepareHook = id =>
            new PrepareIconResult(EnrichOutcome.UpToDate, null, new PendingBatchIcon($"b{id}_0", null));
        var result = await RefreshService(_runner, new CannedBatchRenderer([7], throwAll: true))
            .RefreshIconsAsync();

        Assert.Equal(2, result.Checked);
        Assert.Equal(0, result.Refreshed);
        Assert.Equal(2, result.Failed);
        Assert.All(result.Errors, e => Assert.Contains("batch render failed", e));
        Assert.Empty(_runner.Commits); // phase C never runs
    }

    private void Commit(string date, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(_repo, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        Git("add -A");
        Git("commit -m test", date);
    }

    private string Head()
    {
        using var process = GitProcess("rev-parse HEAD", null);
        return process.StandardOutput.ReadToEnd().Trim();
    }

    private void Git(string args, string? date = null)
    {
        using var process = GitProcess(args, date);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {args} failed: {error}");
        }
    }

    private Process GitProcess(string args, string? date)
    {
        var psi = new ProcessStartInfo("git", $"-c user.email=test@test -c user.name=test -c commit.gpgsign=false {args}")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (date is not null)
        {
            psi.Environment["GIT_AUTHOR_DATE"] = date;
            psi.Environment["GIT_COMMITTER_DATE"] = date;
        }

        return Process.Start(psi)!;
    }

    private static bool GitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
