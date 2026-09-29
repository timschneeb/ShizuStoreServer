using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Jobs;
using ShizuAppStoreServer.Jobs;

namespace ShizuAppStoreServer.Api.Tests;

/// <summary>
/// End-to-end job log behavior through the host: the DB sink writes run rows
/// synchronously and the hosted worker flushes the buffered event stream.
/// </summary>
public sealed class JobLogTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    [Fact]
    public async Task PersistsRunEventsAndCountsThroughTheHostedWorker()
    {
        await factory.ResetAsync(_ => { });
        var log = factory.Services.GetRequiredService<IJobLog>();

        long runId;
        await using (var session = log.Begin(new JobStart(
            JobKind.Sync,
            JobTrigger.Scheduled,
            DateTimeOffset.UtcNow,
            Reference: "abcdef1234567890",
            Metadata: new { fullRecheck = true })))
        {
            runId = session.RunId!.Value;
            session.Phase("list", "parsing upstream list");
            session.Decision("archived: excluded 'foo'", new { slug = "foo" }, slug: "foo");
            session.App("foo", "Foo App", new EnrichResult(EnrichOutcome.Enriched, null));
            await session.FinishAsync(new JobFinish(
                JobStatus.Succeeded,
                Summary: "1 enriched",
                ItemsTotal: 2,
                ItemsOk: 1,
                ItemsSkipped: 1,
                Reference: "abcdef1234567890",
                Metadata: new { added = 1 }));
        }

        var run = await WaitForFinishedRunAsync(runId);
        Assert.Equal(JobKind.Sync, run.Kind);
        Assert.Equal(JobTrigger.Scheduled, run.Trigger);
        Assert.Equal(JobStatus.Succeeded, run.Status);
        Assert.Equal("1 enriched", run.Summary);
        Assert.Equal("abcdef1234567890", run.Reference);
        Assert.Equal(2, run.ItemsTotal);
        Assert.Equal(1, run.ItemsOk);
        Assert.Equal(1, run.ItemsSkipped);
        Assert.Equal(0, run.ItemsFailed);
        Assert.Equal(0, run.EventsDropped);
        Assert.NotNull(run.DurationMs);

        using var metadata = System.Text.Json.JsonDocument.Parse(run.Metadata!);
        Assert.True(metadata.RootElement.GetProperty("fullRecheck").GetBoolean());
        Assert.Equal(1, metadata.RootElement.GetProperty("added").GetInt32());

        var events = await factory.QueryAsync(db => db.JobEvents
            .Where(e => e.JobRunId == runId)
            .OrderBy(e => e.Seq)
            .ToListAsync());
        Assert.Equal(3, run.EventsCount);
        Assert.Equal(3, events.Count);
        Assert.Equal([1, 2, 3], events.Select(e => e.Seq));
        Assert.Equal(JobEventType.Phase, events[0].Type);
        Assert.Equal("list", events[0].Phase);
        Assert.Equal("parsing upstream list", events[0].Message);
        Assert.Equal(JobEventType.Decision, events[1].Type);
        Assert.Equal("foo", events[1].Slug);
        Assert.Equal(JobEventType.App, events[2].Type);
        Assert.Contains("Foo App", events[2].Message);
        Assert.NotEqual("{}", events[1].Data);
    }

    [Fact]
    public async Task SkippedRunsWriteARowImmediately()
    {
        await factory.ResetAsync(_ => { });
        var log = factory.Services.GetRequiredService<IJobLog>();

        log.Skipped(new JobStart(
            JobKind.Sync, JobTrigger.Scheduled, DateTimeOffset.UtcNow,
            Metadata: new { fullRecheck = false }),
            "nothing due");

        var run = await factory.QueryAsync(db => db.JobRuns.SingleAsync());
        Assert.Equal(JobStatus.Skipped, run.Status);
        Assert.Equal("nothing due", run.Summary);
        Assert.NotNull(run.FinishedAt);
        Assert.NotNull(run.DurationMs);
        Assert.Equal(0, run.EventsCount);
    }

    [Fact]
    public async Task RecoverInterruptedMarksOrphanedRuns()
    {
        await factory.ResetAsync(db => db.JobRuns.Add(new JobRun
        {
            Kind = JobKind.Sync,
            Trigger = JobTrigger.Scheduled,
            Status = JobStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            StartedDay = DateOnly.FromDateTime(DateTime.UtcNow),
        }));
        var sink = factory.Services.GetRequiredService<DbJobSink>();

        await sink.RecoverInterruptedAsync(CancellationToken.None);

        var run = await factory.QueryAsync(db => db.JobRuns.SingleAsync());
        Assert.Equal(JobStatus.Interrupted, run.Status);
        Assert.Equal("interrupted by server restart", run.Summary);
        Assert.NotNull(run.FinishedAt);
    }

    private async Task<JobRun> WaitForFinishedRunAsync(long runId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var run = await factory.QueryAsync(db => db.JobRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId));
            if (run is not null && run.FinishedAt is not null)
            {
                return run;
            }

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException($"job run {runId} was not finished in time");
    }
}

/// <summary>
/// Sink behavior on a private SQLite connection, with the flush loop driven by
/// the test instead of the hosted worker.
/// </summary>
public sealed class DbJobSinkTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ShizuDbContext>().UseSqlite(_connection).Options;
        await using (var db = new ShizuDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<ShizuDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task DropsEventsBelowTheMinimumLevel()
    {
        var sink = NewSink(new JobLogOptions { MinLevel = JobEventLevel.Warning });
        var log = new JobLog([sink]);
        using var pumpCts = new CancellationTokenSource();
        var pump = sink.RunAsync(pumpCts.Token);

        await using (var session = log.Begin(new JobStart(
            JobKind.Sync, JobTrigger.Scheduled, DateTimeOffset.UtcNow)))
        {
            session.Event(JobEventLevel.Debug, JobEventType.Decision, "debug detail");
            session.Event(JobEventLevel.Info, JobEventType.Phase, "info phase");
            session.Event(JobEventLevel.Error, JobEventType.Error, "boom");
            await session.FinishAsync(new JobFinish(JobStatus.Failed));
        }

        pumpCts.Cancel();
        await pump;

        var events = await QueryAsync(db => db.JobEvents.OrderBy(e => e.Seq).ToListAsync());
        var evt = Assert.Single(events);
        Assert.Equal(JobEventLevel.Error, evt.Level);
        Assert.Equal("boom", evt.Message);
    }

    [Fact]
    public async Task CountsEventsDroppedWhenTheChannelIsFull()
    {
        // Capacity one and no pump while writing: the first event occupies the
        // channel, the next two are dropped, and the finish marker finds no
        // room so the row update takes the direct fallback path.
        var sink = NewSink(new JobLogOptions { ChannelCapacity = 1 });
        var log = new JobLog([sink]);
        using var pumpCts = new CancellationTokenSource();

        await using (var session = log.Begin(new JobStart(
            JobKind.Sync, JobTrigger.Scheduled, DateTimeOffset.UtcNow)))
        {
            session.Event(JobEventLevel.Info, JobEventType.Phase, "first");
            session.Event(JobEventLevel.Info, JobEventType.Phase, "second");
            session.Event(JobEventLevel.Info, JobEventType.Phase, "third");
            await session.FinishAsync(new JobFinish(JobStatus.Succeeded));
        }

        var pump = sink.RunAsync(pumpCts.Token);
        pumpCts.Cancel();
        await pump;

        var run = await QueryAsync(db => db.JobRuns.SingleAsync());
        Assert.Equal(1, run.EventsCount);
        Assert.Equal(2, run.EventsDropped);

        var evt = await QueryAsync(db => db.JobEvents.SingleAsync());
        Assert.Equal("first", evt.Message);
    }

    private DbJobSink NewSink(JobLogOptions options) => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        options,
        NullLogger<DbJobSink>.Instance);

    private async Task<T> QueryAsync<T>(Func<ShizuDbContext, Task<T>> query)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ShizuDbContext>());
    }
}
