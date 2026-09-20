using ShizuAppStoreServer.Core.Enrichment;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class RepoScreenshotResolverTests
{
    private sealed class FakeGit(Func<IReadOnlyList<string>, GitResult> handler) : IGitRunner
    {
        public int Calls;
        public List<IReadOnlyList<string>> Seen = [];

        public Task<GitResult> RunAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
        {
            Calls++;
            Seen.Add(args);
            return Task.FromResult(handler(args));
        }
    }

    private static RepoRef GitHub(string owner, string repo) => new(RepoForge.GitHub, owner, repo);

    private static RepoRef GitLab(string project) => new(RepoForge.GitLab, string.Empty, project);

    [Theory]
    [InlineData("fastlane/metadata/android/en-US/images/phoneScreenshots/1.jpg", true)]
    [InlineData("docs/screenshots/shot.png", true)]
    [InlineData("docs/Screenshots/shot.webp", true)]
    [InlineData("screenshot-home.png", true)]
    [InlineData("Screenshot_2026.jpg", true)]
    [InlineData("app/src/main/java/com/x/screenshot/Foo.kt", false)]
    [InlineData("app/src/main/java/com/x/screenshot/R.java", false)]
    [InlineData("app/src/main/res/drawable/ic_launcher.png", false)]
    [InlineData("README.md", false)]
    [InlineData("screenshot.png/not-an-image.txt", false)]
    public void ScreenshotCriterion(string path, bool expected) =>
        Assert.Equal(expected, RepoScreenshotResolver.IsScreenshotImage(path));

    [Fact]
    public void SelectsAndBuildsGithubRawUrls()
    {
        string[] tree =
        [
            "fastlane/metadata/android/en-US/images/phoneScreenshots/1.jpg",
            "fastlane/metadata/android/en-US/images/phoneScreenshots/2.jpg",
            "docs/screenshots/extra.png",
            "app/src/main/res/drawable/ic_launcher.png",
        ];

        var urls = RepoScreenshotResolver.SelectScreenshots(tree, GitHub("o", "app"), "abc123", 12);

        Assert.Equal(
            [
                "https://raw.githubusercontent.com/o/app/abc123/docs/screenshots/extra.png",
                "https://raw.githubusercontent.com/o/app/abc123/fastlane/metadata/android/en-US/images/phoneScreenshots/1.jpg",
                "https://raw.githubusercontent.com/o/app/abc123/fastlane/metadata/android/en-US/images/phoneScreenshots/2.jpg",
            ],
            urls);
    }

    [Fact]
    public void BuildsGitlabRawUrlWithSubgroups()
    {
        var url = RepoScreenshotResolver.BuildRawUrl(
            GitLab("group/sub/app"), "sha1", "fastlane/metadata/android/en-US/images/phoneScreenshots/ss001.png");

        Assert.Equal(
            "https://gitlab.com/group/sub/app/-/raw/sha1/fastlane/metadata/android/en-US/images/phoneScreenshots/ss001.png",
            url);
    }

    [Fact]
    public void PercentEncodesPathSegments()
    {
        var url = RepoScreenshotResolver.BuildRawUrl(GitHub("o", "app"), "sha", "docs/screenshots/my shot 1.png");

        Assert.Equal("https://raw.githubusercontent.com/o/app/sha/docs/screenshots/my%20shot%201.png", url);
    }

    [Fact]
    public void CapsSelectedScreenshots()
    {
        var tree = Enumerable.Range(1, 20)
            .Select(i => $"fastlane/metadata/android/en-US/images/phoneScreenshots/{i:00}.png")
            .ToArray();

        var urls = RepoScreenshotResolver.SelectScreenshots(tree, GitHub("o", "app"), "sha", 12);

        Assert.Equal(12, urls.Count);
    }

    [Theory]
    [InlineData("https://github.com/o/app", null, RepoForge.GitHub, "o", "app")]
    [InlineData("https://github.com/Horizen5/Appslim/blob/master/docs/README_en.md", null, RepoForge.GitHub, "Horizen5", "Appslim")]
    [InlineData("https://f-droid.org/packages/x", "https://github.com/kmod-midori/CatShare", RepoForge.GitHub, "kmod-midori", "CatShare")]
    [InlineData("https://gitlab.com/group/sub/app", null, RepoForge.GitLab, "", "group/sub/app")]
    public void ParsesRepo(string url, string? sourceUrl, RepoForge forge, string owner, string project)
    {
        Assert.True(RepoScreenshotResolver.TryParseRepo(url, sourceUrl, out var repo));
        Assert.Equal(forge, repo.Forge);
        Assert.Equal(owner, repo.Owner);
        Assert.Equal(project, repo.ProjectPath);
    }

    [Fact]
    public void RejectsNonRepoUrls()
    {
        Assert.False(RepoScreenshotResolver.TryParseRepo("https://f-droid.org/packages/x", null, out _));
        Assert.False(RepoScreenshotResolver.TryParseRepo("https://play.google.com/store/apps/details?id=x", null, out _));
    }

    [Fact]
    public async Task ResolvesScreenshotsFromCloneTree()
    {
        string[] tree =
        [
            "fastlane/metadata/android/en-US/images/phoneScreenshots/1.jpg",
            "README.md",
        ];
        var git = new FakeGit(args => args[0] switch
        {
            "clone" => new GitResult(0, string.Empty, string.Empty),
            _ when args.Contains("rev-parse") => new GitResult(0, "deadbeef\n", string.Empty),
            _ when args.Contains("ls-tree") => new GitResult(0, string.Join('\0', tree) + "\0", string.Empty),
            _ => new GitResult(1, string.Empty, "unexpected"),
        });
        var resolver = new RepoScreenshotResolver(git, new EnrichmentOptions());

        var result = await resolver.ResolveAsync("https://github.com/o/app", null);

        Assert.True(result.Reached);
        Assert.Equal(
            ["https://raw.githubusercontent.com/o/app/deadbeef/fastlane/metadata/android/en-US/images/phoneScreenshots/1.jpg"],
            result.Urls);
        Assert.Equal(3, git.Calls);
        Assert.Contains("--filter=blob:none", git.Seen[0]);
        Assert.Contains("--no-checkout", git.Seen[0]);
    }

    [Fact]
    public async Task EmptyTreeIsReachedWithNoScreenshots()
    {
        // A listed tree without screenshot images is a verified "none": the
        // caller may clear stored repo URLs. Only failures are unreached.
        var git = new FakeGit(args => args[0] switch
        {
            "clone" => new GitResult(0, string.Empty, string.Empty),
            _ when args.Contains("rev-parse") => new GitResult(0, "deadbeef\n", string.Empty),
            _ when args.Contains("ls-tree") => new GitResult(0, "README.md\0app/src/main/App.kt\0", string.Empty),
            _ => new GitResult(1, string.Empty, "unexpected"),
        });
        var resolver = new RepoScreenshotResolver(git, new EnrichmentOptions());

        var result = await resolver.ResolveAsync("https://github.com/o/app", null);

        Assert.True(result.Reached);
        Assert.Empty(result.Urls);
    }

    [Fact]
    public async Task CloneFailureYieldsNoScreenshots()
    {
        var git = new FakeGit(args => args[0] == "clone"
            ? new GitResult(1, string.Empty, "repository not found")
            : new GitResult(1, string.Empty, "unexpected"));
        var resolver = new RepoScreenshotResolver(git, new EnrichmentOptions());

        var result = await resolver.ResolveAsync("https://github.com/o/missing", null);

        Assert.False(result.Reached);
        Assert.Empty(result.Urls);
        Assert.Equal(1, git.Calls); // no rev-parse/ls-tree after a failed clone
    }

    [Fact]
    public async Task DisabledSwitchSkipsGit()
    {
        var git = new FakeGit(_ => new GitResult(0, string.Empty, string.Empty));
        var resolver = new RepoScreenshotResolver(
            git, new EnrichmentOptions { RepoScreenshotsEnabled = false });

        var result = await resolver.ResolveAsync("https://github.com/o/app", null);

        Assert.False(result.Reached);
        Assert.Empty(result.Urls);
        Assert.Equal(0, git.Calls);
    }

    [Fact]
    public async Task LiveResolvesRealRepoWhenProvided()
    {
        // Env-gated live test, e.g. SHIZU_REAL_REPO=https://github.com/10miaomiao/bili-down-out
        // (RealAapt2 precedent). Needs network and the git binary.
        var repo = Environment.GetEnvironmentVariable("SHIZU_REAL_REPO");
        if (string.IsNullOrEmpty(repo))
        {
            return; // Not a failure: env-gated by design.
        }

        var gitPath = Environment.GetEnvironmentVariable("SHIZU_GIT") ?? "git";
        var resolver = new RepoScreenshotResolver(
            new GitProcessRunner(gitPath),
            new EnrichmentOptions { RepoScreenshotsTimeout = TimeSpan.FromMinutes(3) });

        var urls = (await resolver.ResolveAsync(repo, null)).Urls;

        Assert.NotEmpty(urls);
        Assert.All(urls, url => Assert.Contains("screenshot", url, StringComparison.OrdinalIgnoreCase));
    }
}
