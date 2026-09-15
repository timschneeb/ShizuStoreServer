using System.Diagnostics;
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

    [Fact]
    public async Task FetchAsyncFastForwardsToRemoteHead()
    {
        if (!GitAvailable())
        {
            // Needs the git CLI (early return like the other live-ish tests).
            return;
        }

        var root = TempRoot();
        try
        {
            var (_, clone, other) = await InitTwoClonesAsync(root);
            await File.WriteAllTextAsync(Path.Combine(other, "README.md"), "list v2\n");
            await CommitAllAsync(other, "update list");
            await GitAsync(other, "push origin master");
            var remoteHead = (await GitAsync(other, "rev-parse HEAD")).Trim();

            await new GitHistoryService().FetchAsync(clone);

            Assert.Equal(remoteHead, (await GitAsync(clone, "rev-parse HEAD")).Trim());
            Assert.Equal("list v2\n", await File.ReadAllTextAsync(Path.Combine(clone, "README.md")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task FetchAsyncReclonesWhenHistoryDiverged()
    {
        if (!GitAvailable())
        {
            return;
        }

        var root = TempRoot();
        try
        {
            var (_, clone, other) = await InitTwoClonesAsync(root);

            // Diverge the worker clone, then move the remote from elsewhere:
            // a fast-forward is impossible, so the clone must be replaced.
            await File.WriteAllTextAsync(Path.Combine(clone, "local-only.txt"), "mine\n");
            await CommitAllAsync(clone, "local commit");
            await File.WriteAllTextAsync(Path.Combine(other, "README.md"), "list v2\n");
            await CommitAllAsync(other, "update list");
            await GitAsync(other, "push origin master");
            var remoteHead = (await GitAsync(other, "rev-parse HEAD")).Trim();

            await new GitHistoryService().FetchAsync(clone);

            Assert.Equal(remoteHead, (await GitAsync(clone, "rev-parse HEAD")).Trim());
            Assert.Equal("list v2\n", await File.ReadAllTextAsync(Path.Combine(clone, "README.md")));
            Assert.False(File.Exists(Path.Combine(clone, "local-only.txt")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static bool GitAvailable()
    {
        try
        {
            GitAsync(Path.GetTempPath(), "--version").GetAwaiter().GetResult();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<(string Remote, string Clone, string Other)> InitTwoClonesAsync(string root)
    {
        var remote = Path.Combine(root, "remote.git");
        var clone = Path.Combine(root, "clone");
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(root);
        await GitAsync(root, $"init --bare -b master \"{remote}\"");
        await GitAsync(root, $"clone \"{remote}\" \"{clone}\"");
        await ConfigureAsync(clone);
        await File.WriteAllTextAsync(Path.Combine(clone, "README.md"), "list v1\n");
        await CommitAllAsync(clone, "init");
        await GitAsync(clone, "push -u origin master");
        await GitAsync(root, $"clone \"{remote}\" \"{other}\"");
        await ConfigureAsync(other);
        return (remote, clone, other);
    }

    private static async Task ConfigureAsync(string repo)
    {
        await GitAsync(repo, "config user.email test@example.com");
        await GitAsync(repo, "config user.name Test");
    }

    private static async Task CommitAllAsync(string repo, string message)
    {
        await GitAsync(repo, "add -A");
        await GitAsync(repo, $"commit -m \"{message}\"");
    }

    private static async Task<string> GitAsync(string workingDir, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {error.Trim()}");
        }

        return output;
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "shizu-git-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Best effort: temp dir cleanup must not fail a test.
        }
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
