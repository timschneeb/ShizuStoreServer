using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sync;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Hermetic <see cref="FileRunLog"/> tests: format, run separation, IO tolerance.</summary>
public sealed class RunLogTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "shizu-runlog-" + Guid.NewGuid().ToString("N"));

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
    public void WritesHeaderAppLinesFooterAndIssues()
    {
        var path = Path.Combine(_dir, "runs.log");
        var log = new FileRunLog(path);
        var start = new DateTimeOffset(2026, 9, 15, 20, 0, 0, TimeSpan.Zero);

        log.Begin("nightly", fullRecheck: true, start, appCount: 2);
        log.App("smartspacerplugins", "Smartspacer Plugins",
            new EnrichResult(EnrichOutcome.Enriched, null));
        log.App("droidos", "DroidOS",
            new EnrichResult(EnrichOutcome.UpToDate, null) { Detail = "APK unchanged, analysis skipped" });
        log.End(start.AddMinutes(12), enriched: 1, upToDate: 1, failed: 0);
        log.Issues(7, "abcdef1234", [
            new SyncIssue { Kind = IssueKind.Parse, Rule = "parse_warning", Message = "bad bullet", Location = "Audio" },
            new SyncIssue { Kind = IssueKind.Enrich, Rule = "enrich_failed", Message = "404", Slug = "brokenapp" },
        ]);

        var text = File.ReadAllText(path);
        Assert.Contains("===== run 2026-09-15 20:00:00Z | trigger=nightly | mode=full | apps=2 =====", text);
        Assert.Contains("[ 1/2] smartspacerplugins (Smartspacer Plugins)  OK", text);
        Assert.Contains("[ 2/2] droidos (DroidOS)  skip", text);
        Assert.Contains("APK unchanged, analysis skipped", text);
        Assert.Contains("----- run finished 2026-09-15 20:12:00Z | 0:12:00 | ok=1 skip=1 fail=0 -----", text);
        Assert.Contains("----- issues | run=7 | head=abcdef12 | parse=1 enrich=1 quality=0 total=2 -----", text);
        Assert.Contains("enrich   enrich_failed      brokenapp", text);
        Assert.Contains("parse    parse_warning      Audio", text);
    }

    [Fact]
    public void DetailLinesCarryTheMessageAndATimestamp()
    {
        var path = Path.Combine(_dir, "runs.log");
        var log = new FileRunLog(path);

        log.Begin("manual", fullRecheck: true, DateTimeOffset.UtcNow, appCount: 1);
        log.Detail("download done 1234B in 567ms");
        log.End(DateTimeOffset.UtcNow, 1, 0, 0);

        var lines = File.ReadAllLines(path);
        var detail = Assert.Single(lines, l =>
            l.Contains("download done 1234B in 567ms", StringComparison.Ordinal));
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z download done 1234B in 567ms$",
            detail);
    }

    [Fact]
    public void SeparatesConsecutiveRuns()
    {
        var path = Path.Combine(_dir, "runs.log");
        var log = new FileRunLog(path);
        var start = new DateTimeOffset(2026, 9, 15, 20, 0, 0, TimeSpan.Zero);

        log.Begin("scheduled", fullRecheck: false, start, appCount: 1);
        log.App("a", null, new EnrichResult(EnrichOutcome.Enriched, null));
        log.End(start, 1, 0, 0);
        log.Issues(1, null, []);
        log.Skip("scheduled", start.AddMinutes(15), "nothing due");

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Count(l => l.StartsWith("===== run", StringComparison.Ordinal)));
        Assert.Contains(string.Empty, lines); // blank line separates the runs
        Assert.EndsWith("nothing due =====", lines[^1]);
    }

    [Fact]
    public void RotatesAtMaxBytesKeepingTwoFiles()
    {
        var path = Path.Combine(_dir, "runs.log");
        var log = new FileRunLog(path, maxBytes: 100);
        var start = new DateTimeOffset(2026, 9, 15, 20, 0, 0, TimeSpan.Zero);

        log.Begin("scheduled", fullRecheck: false, start, appCount: 1);
        log.App("first", null, new EnrichResult(EnrichOutcome.Enriched, null));
        log.End(start, 1, 0, 0);
        Assert.True(new FileInfo(path).Length >= 100);

        log.Begin("scheduled", fullRecheck: false, start.AddMinutes(15), appCount: 1);
        log.App("second", null, new EnrichResult(EnrichOutcome.Enriched, null));

        var previous = path + ".1";
        Assert.True(File.Exists(previous));
        Assert.Contains("first", File.ReadAllText(previous));
        Assert.DoesNotContain("second", File.ReadAllText(previous));
        Assert.Contains("second", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".2"));
    }

    [Fact]
    public void UnwritablePathNeverThrows()
    {
        // A regular file used as a directory: directory creation fails.
        var file = Path.Combine(_dir, "blocker");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(file, "x");
        var log = new FileRunLog(Path.Combine(file, "runs.log"));

        log.Begin("nightly", true, DateTimeOffset.UtcNow, 1);
        log.App("a", null, new EnrichResult(EnrichOutcome.Enriched, null));
        log.End(DateTimeOffset.UtcNow, 1, 0, 0);
        log.Issues(1, null, []);
    }
}
