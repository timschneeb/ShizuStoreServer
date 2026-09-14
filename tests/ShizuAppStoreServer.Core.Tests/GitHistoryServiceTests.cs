using ShizuAppStoreServer.Core.History;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class GitHistoryServiceTests
{
    private const string Log = """
        COMMIT:1111111111111111111111111111111111111111|2022-10-09T05:25:39+02:00|Add apps
        diff --git a/README.md b/README.md
        --- a/README.md
        +++ b/README.md
        @@ -1,0 +1,3 @@
        +* [MicUp](https://github.com/papergray/MicUp) - Real-time mic `MIT`
        +* [aShell](https://gitlab.com/sunilpaulmathew/ashell) - ADB shell `GPL-3.0`
        +Some prose mentioning [a link](https://example.com/prose) is ignored.
        COMMIT:2222222222222222222222222222222222222222|2023-05-01T10:00:00+02:00|Modify aShell entry
        diff --git a/README.md b/README.md
        --- a/README.md
        +++ b/README.md
        @@ -1,2 +1,3 @@
        -* [aShell](https://gitlab.com/sunilpaulmathew/ashell) - ADB shell `GPL-3.0`
        +* [aShell](https://gitlab.com/sunilpaulmathew/ashell) - A local ADB shell `GPL-3.0`
        +  * [aShell You](https://github.com/DP-Hridayan/aShellYou) - Redesign `GPL-3.0`
        COMMIT:3333333333333333333333333333333333333333|not-a-date|Add bogus
        +* [Bogus](https://example.com/bogus) - Skipped, bad commit date `MIT`
        """;

    [Fact]
    public void ParseLogTracksFirstSeenAndLastTouchPerUrl()
    {
        var history = GitHistoryService.ParseLog(Log);

        Assert.Equal(3, history.Count);

        var micUp = history["https://github.com/papergray/MicUp"];
        Assert.Equal(DateTimeOffset.Parse("2022-10-09T05:25:39+02:00"), micUp.AddedAt);
        Assert.Equal(micUp.AddedAt, micUp.UpdatedAt);

        var ashell = history["https://gitlab.com/sunilpaulmathew/ashell"];
        Assert.Equal(DateTimeOffset.Parse("2022-10-09T05:25:39+02:00"), ashell.AddedAt);
        Assert.Equal(DateTimeOffset.Parse("2023-05-01T10:00:00+02:00"), ashell.UpdatedAt);

        // Nested bullets are tracked by their own URL …
        Assert.Contains("https://github.com/DP-Hridayan/aShellYou", history.Keys);
        // … while prose links, file headers and bad-date commits are ignored.
        Assert.DoesNotContain("https://example.com/prose", history.Keys);
        Assert.DoesNotContain("https://example.com/bogus", history.Keys);
    }

    [Fact]
    public void ParseLogNormalizesCommitDatesToUtc()
    {
        // Npgsql writes DateTimeOffset to timestamptz only with Offset=0;
        // a preserved committer offset passed SQLite tests and crashed the
        // production save (live failure 2026-09-12).
        var history = GitHistoryService.ParseLog(Log);

        var micUp = history["https://github.com/papergray/MicUp"];
        Assert.Equal(TimeSpan.Zero, micUp.AddedAt.Offset);
        Assert.Equal(TimeSpan.Zero, micUp.UpdatedAt.Offset);
        Assert.Equal(
            DateTimeOffset.Parse("2022-10-09T05:25:39+02:00").UtcDateTime,
            micUp.AddedAt.UtcDateTime);
    }

    [Fact]
    public void ParseLogTakesFirstLinkPerLine()    {
        const string log = """
            COMMIT:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|2024-01-02T03:04:05+01:00|Add RootlessJamesDSP
            +* [RootlessJamesDSP](https://play.google.com/store/apps/details?id=x) - Desc `GPL-3.0` [(Source code)](https://github.com/timschneeb/RootlessJamesDSP)
            """;

        var history = GitHistoryService.ParseLog(log);

        Assert.Contains("https://play.google.com/store/apps/details?id=x", history.Keys);
        Assert.DoesNotContain("https://github.com/timschneeb/RootlessJamesDSP", history.Keys);
    }

    [Fact]
    public void ParseLogSkipsSilentCommits()
    {
        // The published changelog ignores [silent] housekeeping commits, so
        // they must not bump an entry nor introduce a new one.
        const string log = """
            COMMIT:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|2024-01-01T00:00:00+00:00|Add apps
            +* [MicUp](https://github.com/papergray/MicUp) - Real-time mic `MIT`
            COMMIT:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|2024-02-01T00:00:00+00:00|Tidy README [silent]
            +* [MicUp](https://github.com/papergray/MicUp) - Real-time mic `MIT`
            +* [Ghost](https://example.com/ghost) - Silent addition `MIT`
            """;

        var history = GitHistoryService.ParseLog(log);

        var micUp = history["https://github.com/papergray/MicUp"];
        Assert.Equal(DateTimeOffset.Parse("2024-01-01T00:00:00+00:00"), micUp.UpdatedAt);
        Assert.DoesNotContain("https://example.com/ghost", history.Keys);
    }

    [Fact]
    public async Task RealCloneHistoryCoversParsedEntries()
    {
        var repo = FindAwesomeShizukuRepo();
        if (repo is null)
        {
            // Dev-machine only: the awesome-shizuku checkout is a sibling directory.
            return;
        }

        var service = new GitHistoryService();
        var history = await service.GetHistoryAsync(repo, "README.md");

        Assert.NotEmpty(history);
        Assert.All(history, kv =>
        {
            Assert.False(string.IsNullOrWhiteSpace(kv.Key));
            Assert.True(kv.Value.AddedAt <= kv.Value.UpdatedAt);
        });

        var head = await service.GetHeadCommitAsync(repo);
        Assert.NotNull(head);
        Assert.Matches("^[0-9a-f]{40}$", head);
    }

    private static string? FindAwesomeShizukuRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "awesome-shizuku");
            if (Directory.Exists(Path.Combine(candidate, ".git")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
