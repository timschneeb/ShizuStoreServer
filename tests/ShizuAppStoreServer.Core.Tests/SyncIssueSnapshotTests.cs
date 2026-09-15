using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.History;
using ShizuAppStoreServer.Core.Sync;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>
/// Snapshot tests: successful passes replace <c>sync_issues</c>, skipped and
/// failed passes leave the previous good snapshot in place.
/// </summary>
public sealed class SyncIssueSnapshotTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private const string ReadmeV1 = """
        # Test list

        ## Apps

        ### Audio

        * [Tuner](https://github.com/acme/tuner) - Tuner description `GPL-3.0`
        * [MicUp](https://github.com/acme/micup) - MicUp description `MIT`
        """;

    private const string ReadmeWithBadLine = ReadmeV1 + "\n* not an entry at all\n";

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "shizu-issues-test-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ShizuDbContext _db;
    private readonly FakeRunner _runner;

    public SyncIssueSnapshotTests()
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

    [Fact]
    public async Task FirstPassWritesQualitySnapshot()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1));

        var result = await Service().RunAsync("scheduled", fullRecheck: false, T0);

        Assert.Null(result.Error);
        var issues = _db.SyncIssues.ToList();
        // The fake runner sets no icon: both apps flag missing_icon.
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(IssueKind.Quality, i.Kind));
        Assert.All(issues, i => Assert.Equal(CatalogHealthCheck.MissingIcon, i.Rule));
        Assert.Equal(2, _db.SyncRuns.Single().IssueCount);
    }

    [Fact]
    public async Task SecondFullPassReplacesInsteadOfAppending()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);
        Assert.Equal(2, _db.SyncIssues.Count());

        await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(16));

        Assert.Equal(2, _db.SyncRuns.Count());
        Assert.Equal(2, _db.SyncIssues.Count());
    }

    [Fact]
    public async Task FailedPassKeepsPreviousSnapshot()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);
        Assert.Equal(2, _db.SyncIssues.Count());

        File.Delete(Path.Combine(_repo, "README.md"));
        _db.SyncRequests.Add(new SyncRequest { RequestedAt = T0, Reason = "hook during outage" });
        await _db.SaveChangesAsync();
        var result = await Service().RunAsync("scheduled", fullRecheck: false, T0.AddMinutes(16));

        Assert.NotNull(result.Error);
        Assert.Equal(2, _db.SyncIssues.Count());
    }

    [Fact]
    public async Task ParseWarningsArePersisted()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeWithBadLine));

        var result = await Service().RunAsync("scheduled", fullRecheck: false, T0);

        Assert.Null(result.Error);
        var parse = _db.SyncIssues.Where(i => i.Kind == IssueKind.Parse).ToList();
        Assert.NotEmpty(parse);
        Assert.All(parse, i => Assert.Equal("parse_warning", i.Rule));
        Assert.All(parse, i => Assert.NotNull(i.Location));
    }

    [Fact]
    public async Task DueOnlyPassPreservesParseRows()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeWithBadLine));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);
        var parseBefore = _db.SyncIssues.Count(i => i.Kind == IssueKind.Parse);
        Assert.True(parseBefore > 0);

        // Same HEAD, one app due: exercises the due-only path (no re-parse).
        var tuner = _db.Apps.Single(a => a.Slug == "tuner");
        tuner.LastCheckedAt = T0.AddHours(-25);
        await _db.SaveChangesAsync();

        var result = await Service().RunAsync("scheduled", fullRecheck: false, T0.AddHours(1));

        Assert.Null(result.Error);
        Assert.False(result.Skipped);
        Assert.Equal(parseBefore, _db.SyncIssues.Count(i => i.Kind == IssueKind.Parse));
    }

    [Fact]
    public async Task RowsWithLastErrorAreReportedAsEnrichIssues()
    {
        if (!InitRepo())
        {
            return;
        }

        Commit("2026-01-05T10:00:00+00:00", ("README.md", ReadmeV1));
        await Service().RunAsync("scheduled", fullRecheck: false, T0);

        var tuner = _db.Apps.Single(a => a.Slug == "tuner");
        tuner.LastError = "Upstream timed out";
        await _db.SaveChangesAsync();

        await Service().RunAsync("nightly", fullRecheck: true, T0.AddMinutes(16));

        var enrich = _db.SyncIssues.Where(i => i.Kind == IssueKind.Enrich).ToList();
        var row = Assert.Single(enrich);
        Assert.Equal("enrich_failed", row.Rule);
        Assert.Equal("tuner", row.Slug);
        Assert.Equal("Upstream timed out", row.Message);
    }

    private SyncService Service() => new(
        _db,
        new CatalogUpserter(_db),
        new GitHistoryService(),
        _runner,
        new NoPoll(),
        new ThrowingRenderer(),
        new SyncOptions { ListPath = _repo },
        new EnrichmentOptions { MaxParallelism = 1 });

    /// <summary>Poll stub: the snapshot tests never exercise the fast-path poll.</summary>
    private sealed class NoPoll : IReleasePoller
    {
        public Task<IReadOnlySet<long>> FindChangedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<long>>(new HashSet<long>());
    }

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

    /// <summary>Stub runner: marks rows checked without setting icons or packages.</summary>
    private sealed class FakeRunner(ShizuDbContext db) : IEnrichmentRunner
    {
        public Task<EnrichResult> EnrichAsync(long appId, bool force, DateTimeOffset now, CancellationToken ct)
        {
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
            Task.FromResult(new PrepareIconResult(EnrichOutcome.UpToDate, null, null));

        public Task<EnrichResult> CommitIconRefreshAsync(long appId, byte[]? png, CancellationToken ct, bool force = false, bool isAdaptive = false) =>
            Task.FromResult(new EnrichResult(EnrichOutcome.Enriched, null));
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
}
