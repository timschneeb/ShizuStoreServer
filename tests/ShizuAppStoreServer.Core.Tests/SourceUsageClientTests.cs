using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sources;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class SourceUsageClientTests
{
    private static App App(string? url = "https://github.com/example/shizukuapp", string? sourceUrl = null) =>
        new() { Slug = "shizukuapp", Name = "ShizukuApp", Url = url!, SourceUrl = sourceUrl };

    private static RepoTree Tree(params RepoTreeEntry[] entries) => new(entries, false);

    [Fact]
    public async Task NoRepositoryMeansScannedWithoutUsage()
    {
        var github = new FakeGitHub(_ => null);
        var client = new SourceUsageClient(github, new FakeGitLab(_ => null), new EnrichmentOptions());

        var result = await client.ScanAsync(App(url: null, sourceUrl: null));

        Assert.True(result.Scanned);
        Assert.True(result.Usage.IsEmpty);
    }

    [Fact]
    public async Task TreeFailureLeavesTheScanPending()
    {
        var github = new FakeGitHub(_ => null);
        var client = new SourceUsageClient(github, new FakeGitLab(_ => null), new EnrichmentOptions());

        var result = await client.ScanAsync(App());

        Assert.False(result.Scanned);
        Assert.True(result.Usage.IsEmpty);
    }

    [Fact]
    public async Task SelectsManifestAndSkipsTestsAndOversizedFiles()
    {
        var github = new FakeGitHub(_ => Tree(
            new RepoTreeEntry("AndroidManifest.xml", "manifest", 512),
            new RepoTreeEntry("app/src/main/java/com/example/Main.kt", "main", 1024),
            new RepoTreeEntry("app/src/test/java/com/example/MainTest.kt", "test", 1024),
            new RepoTreeEntry("app/src/androidTest/java/com/example/Flow.kt", "androidtest", 1024),
            new RepoTreeEntry("app/build/generated/Generated.kt", "generated", 1024),
            new RepoTreeEntry("assets/big.kt", "big", 10 * 1024 * 1024),
            new RepoTreeEntry("assets/logo.png", "logo", 2048)));
        github.Blobs["manifest"] = "<manifest><uses-permission android:name=\"moe.shizuku.manager.permission.API_V23\"/></manifest>";
        github.Blobs["main"] = "import rikka.shizuku.Shizuku";
        github.Blobs["test"] = "Shizuku.bindUserService()";
        var client = new SourceUsageClient(github, new FakeGitLab(_ => null), new EnrichmentOptions());

        var result = await client.ScanAsync(App());

        Assert.True(result.Scanned);
        Assert.Equal(["shizuku"], result.Usage.Managers);
        Assert.Equal(["manifest", "main"], github.Fetched);
        Assert.DoesNotContain("test", github.Fetched);
        Assert.DoesNotContain("androidtest", github.Fetched);
        Assert.DoesNotContain("generated", github.Fetched);
        Assert.DoesNotContain("big", github.Fetched);
    }

    [Fact]
    public async Task MergesRawBlobContents()
    {
        var github = new FakeGitHub(_ => Tree(
            new RepoTreeEntry("app/src/main/java/com/example/Main.kt", "main", 1024)));
        github.Blobs["main"] = "rikka.shizuku.Shizuku.bindUserService(args)";
        var client = new SourceUsageClient(github, new FakeGitLab(_ => null), new EnrichmentOptions());

        var result = await client.ScanAsync(App());

        Assert.Equal(["shizuku"], result.Usage.Managers);
        Assert.Equal("user_service", result.Usage.ApiForm);
        Assert.Contains(result.Usage.Evidence, e => e.Source == "source" && e.Value == "bindUserService");
    }

    [Fact]
    public async Task GitLabProjectsAreScannedToo()
    {
        var gitlab = new FakeGitLab(_ => Tree(
            new RepoTreeEntry("app/src/main/java/com/example/Main.kt", "main", 1024)));
        gitlab.Blobs["main"] = "libsu: com.topjohnwu.superuser.Shell";
        var client = new SourceUsageClient(new FakeGitHub(_ => null), gitlab, new EnrichmentOptions());

        var result = await client.ScanAsync(App(url: "https://gitlab.com/group/project"));

        Assert.Equal(["root"], result.Usage.Managers);
        Assert.Equal(["main"], gitlab.Fetched);
    }

    [Fact]
    public async Task EntriesWithoutShaAreSkipped()
    {
        var github = new FakeGitHub(_ => Tree(
            new RepoTreeEntry("AndroidManifest.xml", null, 512),
            new RepoTreeEntry("app/src/main/java/com/example/Main.kt", "main", 1024)));
        github.Blobs["main"] = "rikka.shizuku.Shizuku";
        var client = new SourceUsageClient(github, new FakeGitLab(_ => null), new EnrichmentOptions());

        var result = await client.ScanAsync(App());

        Assert.Equal(["main"], github.Fetched);
        Assert.Equal(["shizuku"], result.Usage.Managers);
    }

    private sealed class FakeGitHub(Func<string?, RepoTree?> tree) : IGitHubReleaseClient
    {
        public Dictionary<string, string> Blobs { get; } = [];

        public List<string> Fetched { get; } = [];

        public Task<SourceRelease?> GetLatestReleaseAsync(SourceTarget target, string? etag, CancellationToken ct = default) =>
            Task.FromResult<SourceRelease?>(null);

        public Task<RepoTree?> GetRepoTreeAsync(string owner, string repo, CancellationToken ct = default) =>
            Task.FromResult(tree($"{owner}/{repo}"));

        public Task<string?> GetRawBlobAsync(string owner, string repo, string blobSha, CancellationToken ct = default)
        {
            Fetched.Add(blobSha);
            return Task.FromResult(Blobs.TryGetValue(blobSha, out var text) ? text : null);
        }
    }

    private sealed class FakeGitLab(Func<string?, RepoTree?> tree) : IGitLabReleaseClient
    {
        public Dictionary<string, string> Blobs { get; } = [];

        public List<string> Fetched { get; } = [];

        public Task<SourceRelease?> GetLatestReleaseAsync(SourceTarget target, string? etag, CancellationToken ct = default) =>
            Task.FromResult<SourceRelease?>(null);

        public Task<RepoTree?> GetRepoTreeAsync(string projectPath, CancellationToken ct = default) =>
            Task.FromResult(tree(projectPath));

        public Task<string?> GetRawBlobAsync(string projectPath, string blobSha, CancellationToken ct = default)
        {
            Fetched.Add(blobSha);
            return Task.FromResult(Blobs.TryGetValue(blobSha, out var text) ? text : null);
        }
    }
}
