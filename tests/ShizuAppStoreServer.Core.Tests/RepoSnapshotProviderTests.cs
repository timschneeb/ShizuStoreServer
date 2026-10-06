using Microsoft.Extensions.Logging.Abstractions;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Enrichment.Repo;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class RepoSnapshotProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"shizu-snapshot-{Guid.NewGuid():N}");
    private readonly UsageAnalysisOptions _options = new() { SnapshotRoot = null };

    public RepoSnapshotProviderTests()
    {
        _options.SnapshotRoot = _root;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("https://github.com/example/app/releases/download/v1.0.6/app-release.apk", "v1.0.6")]
    [InlineData("https://github.com/example/app/releases/download/release-2.0/app.apk", "release-2.0")]
    [InlineData("https://gitlab.com/group/app/-/releases/v3.1/downloads/app.apk", "v3.1")]
    [InlineData("https://gitlab.com/group/app/-/archive/v4.2/app-v4.2.zip", "v4.2")]
    [InlineData("https://github.com/example/app/releases/download/v1.0%2Bhotfix/app.apk", "v1.0+hotfix")]
    [InlineData("https://github.com/example/app/releases/download/v1.0/app.apk?download=1", "v1.0")]
    public void ParsesTheReleaseTagFromArtifactUrls(string url, string expected)
    {
        Assert.Equal(expected, ReleaseTagParser.FromArtifactUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://f-droid.org/repo/com.example_10.apk")]
    [InlineData("https://github.com/example/app")]
    public void ReturnsNullWhenTheUrlPinsNoRelease(string? url)
    {
        Assert.Null(ReleaseTagParser.FromArtifactUrl(url));
    }

    [Fact]
    public async Task PinsTheUrlReleaseTagOverAStaleVersionName()
    {
        var git = new FakeGit(["refs/tags/v1.0.3", "refs/tags/v1.0.6"]);
        var provider = NewProvider(git);

        using var snapshot = await provider.CreateAsync(
            NewApp(), "1.0.3", releaseTag: null,
            artifactUrl: "https://github.com/example/app/releases/download/v1.0.6/app-release.apk");

        Assert.NotNull(snapshot);
        Assert.Equal("v1.0.6", snapshot!.Ref);
        Assert.Contains("--branch", git.LastCloneArgs);
        Assert.Contains("v1.0.6", git.LastCloneArgs);
    }

    [Fact]
    public async Task UsesTheRecordedTagWhenTheUrlCarriesNone()
    {
        var git = new FakeGit(["refs/tags/v1.0.6"]);
        var provider = NewProvider(git);

        using var snapshot = await provider.CreateAsync(
            NewApp(), "1.0.3", releaseTag: "v1.0.6",
            artifactUrl: "https://example.com/app.apk");

        Assert.NotNull(snapshot);
        Assert.Equal("v1.0.6", snapshot!.Ref);
    }

    [Fact]
    public async Task FallsBackToTheVersionNameWhenTheReleaseTagIsGone()
    {
        var git = new FakeGit(["refs/tags/v1.0.3"]);
        var provider = NewProvider(git);

        using var snapshot = await provider.CreateAsync(
            NewApp(), "1.0.3", releaseTag: "v1.0.6",
            artifactUrl: "https://github.com/example/app/releases/download/v1.0.6/app.apk");

        Assert.NotNull(snapshot);
        Assert.Equal("v1.0.3", snapshot!.Ref);
    }

    [Fact]
    public async Task ClonesTheDefaultBranchWhenTheArtifactComesFromAnotherRepo()
    {
        // The nightly release repo versions independently; its version name
        // must not pin a tag in the analysis repo.
        var git = new FakeGit(["refs/tags/v1.0.3"]);
        var provider = NewProvider(git);

        using var snapshot = await provider.CreateAsync(
            NewApp(), "1.0.3", releaseTag: null,
            artifactUrl: "https://github.com/other/nightly/releases/download/nightly-1/app.apk");

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Ref);
        Assert.DoesNotContain("--branch", git.LastCloneArgs);
    }

    [Fact]
    public async Task PinsTheRecordedTagWhenTheArtifactComesFromAnotherRepo()
    {
        // The external repo's recorded tag still wins when the analysis repo
        // carries it; only the version-name guess is skipped.
        var git = new FakeGit(["refs/tags/nightly-1"]);
        var provider = NewProvider(git);

        using var snapshot = await provider.CreateAsync(
            NewApp(), "1.0.3", releaseTag: "nightly-1",
            artifactUrl: "https://github.com/other/nightly/releases/download/nightly-1/app.apk");

        Assert.NotNull(snapshot);
        Assert.Equal("nightly-1", snapshot!.Ref);
    }

    [Fact]
    public async Task ClonesTheDefaultBranchWhenNoTagMatches()
    {
        var git = new FakeGit(["refs/tags/other"]);
        var provider = NewProvider(git);

        using var snapshot = await provider.CreateAsync(
            NewApp(), "9.9", releaseTag: null,
            artifactUrl: "https://github.com/example/app/releases/download/v9.9/app.apk");

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Ref);
        Assert.DoesNotContain("--branch", git.LastCloneArgs);
    }

    private GitRepoSnapshotProvider NewProvider(IGitRunner git) =>
        new(git, _options, NullLogger<GitRepoSnapshotProvider>.Instance);

    private static App NewApp() => new()
    {
        Slug = "app",
        Name = "App",
        Url = "https://github.com/example/app",
    };

    private sealed class FakeGit(IReadOnlyList<string> tags) : IGitRunner
    {
        public IReadOnlyList<string> LastCloneArgs { get; private set; } = [];

        public Task<GitResult> RunAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
        {
            if (args.Count > 0 && args[0] == "ls-remote")
            {
                var stdout = string.Join('\n', tags.Select(t => $"abc\trefs/tags/{t}"));
                return Task.FromResult(new GitResult(0, stdout, string.Empty));
            }

            if (args.Count > 0 && args[0] == "clone")
            {
                LastCloneArgs = args;
                var dir = args[^1];
                Directory.CreateDirectory(dir);
                return Task.FromResult(new GitResult(0, string.Empty, string.Empty));
            }

            if (args.Count > 2 && args[2] == "rev-parse")
            {
                return Task.FromResult(new GitResult(0, "abc1234", string.Empty));
            }

            return Task.FromResult(new GitResult(0, string.Empty, string.Empty));
        }
    }
}
