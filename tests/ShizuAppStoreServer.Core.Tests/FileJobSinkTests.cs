using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Jobs;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Hermetic <see cref="FileJobSink"/> tests: format, run separation, IO tolerance.</summary>
public sealed class FileJobSinkTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "shizu-joblog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Never created.
        }
    }

    [Fact]
    public async Task WritesHeaderAppLinesEventsAndFooter()
    {
        var path = Path.Combine(_dir, "jobs.log");
        var log = new JobLog([new FileJobSink(path)]);
        var start = new DateTimeOffset(2026, 9, 15, 20, 0, 0, TimeSpan.Zero);

        await using (var session = log.Begin(new JobStart(JobKind.Sync, JobTrigger.Nightly, start, ItemCount: 2)))
        {
            session.App("smartspacerplugins", "Smartspacer Plugins",
                new EnrichResult(EnrichOutcome.Enriched, null));
            session.App("droidos", "DroidOS",
                new EnrichResult(EnrichOutcome.UpToDate, null) { Detail = "APK unchanged, analysis skipped" });
            session.Phase("enrich", "enriching 2 apps");
            await session.FinishAsync(new JobFinish(JobStatus.Succeeded, ItemsOk: 1, ItemsSkipped: 1));
        }

        var text = File.ReadAllText(path);
        Assert.Contains("===== job 2026-09-15 20:00:00Z | kind=sync | trigger=nightly | items=2 =====", text);
        Assert.Contains("[ 1/2] smartspacerplugins (Smartspacer Plugins)  OK", text);
        Assert.Contains("[ 2/2] droidos (DroidOS)  skip  APK unchanged, analysis skipped", text);
        Assert.Contains("[info] [enrich] enriching 2 apps", text);
        Assert.Contains("----- job succeeded", text);
        Assert.Contains("| ok=1 skip=1 fail=0 -----", text);
    }

    [Fact]
    public void SkippedRunsWriteASingleHeaderLine()
    {
        var path = Path.Combine(_dir, "jobs.log");
        var log = new JobLog([new FileJobSink(path)]);

        log.Skipped(
            new JobStart(JobKind.Sync, JobTrigger.Scheduled, new DateTimeOffset(2026, 9, 15, 20, 0, 0, TimeSpan.Zero)),
            "nothing due");

        Assert.Equal(
            "===== job 2026-09-15 20:00:00Z | kind=sync | trigger=scheduled | skipped: nothing due ====="
                + Environment.NewLine,
            File.ReadAllText(path));
    }

    [Fact]
    public async Task SeparatesConsecutiveRuns()
    {
        var path = Path.Combine(_dir, "jobs.log");
        var log = new JobLog([new FileJobSink(path)]);
        var start = new DateTimeOffset(2026, 9, 15, 20, 0, 0, TimeSpan.Zero);

        await using (var session = log.Begin(new JobStart(JobKind.Sync, JobTrigger.Scheduled, start, ItemCount: 1)))
        {
            session.App("a", null, new EnrichResult(EnrichOutcome.Enriched, null));
            await session.FinishAsync(new JobFinish(JobStatus.Succeeded, ItemsOk: 1));
        }

        log.Skipped(new JobStart(JobKind.Sync, JobTrigger.Scheduled, start.AddMinutes(15)), "nothing due");

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Count(l => l.StartsWith("===== job", StringComparison.Ordinal)));
        Assert.Contains(string.Empty, lines); // blank line separates the runs
        Assert.EndsWith("nothing due =====", lines[^1]);
    }

    [Fact]
    public async Task RotatesAtMaxBytesKeepingTwoFiles()
    {
        var path = Path.Combine(_dir, "jobs.log");
        var log = new JobLog([new FileJobSink(path, maxBytes: 100)]);
        var start = new DateTimeOffset(2026, 9, 15, 20, 0, 0, TimeSpan.Zero);

        await using (var session = log.Begin(new JobStart(JobKind.Sync, JobTrigger.Scheduled, start, ItemCount: 1)))
        {
            session.App("first", null, new EnrichResult(EnrichOutcome.Enriched, null));
            await session.FinishAsync(new JobFinish(JobStatus.Succeeded, ItemsOk: 1));
        }

        Assert.True(new FileInfo(path).Length >= 100);

        await using (var session = log.Begin(new JobStart(JobKind.Sync, JobTrigger.Scheduled, start.AddMinutes(15), ItemCount: 1)))
        {
            session.App("second", null, new EnrichResult(EnrichOutcome.Enriched, null));
        }

        var previous = path + ".1";
        Assert.True(File.Exists(previous));
        Assert.Contains("first", File.ReadAllText(previous));
        Assert.DoesNotContain("second", File.ReadAllText(previous));
        Assert.Contains("second", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".2"));
    }

    [Fact]
    public async Task UnwritablePathNeverThrows()
    {
        // A regular file used as a directory: directory creation fails.
        var file = Path.Combine(_dir, "blocker");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(file, "x");
        var log = new JobLog([new FileJobSink(Path.Combine(file, "jobs.log"))]);

        await using var session = log.Begin(
            new JobStart(JobKind.Sync, JobTrigger.Nightly, DateTimeOffset.UtcNow, ItemCount: 1));
        session.App("a", null, new EnrichResult(EnrichOutcome.Enriched, null));
        await session.FinishAsync(new JobFinish(JobStatus.Succeeded, ItemsOk: 1));
    }
}
