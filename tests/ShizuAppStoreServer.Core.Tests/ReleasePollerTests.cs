using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sources;
using ShizuAppStoreServer.Core.Sync;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>
/// Hermetic <see cref="ReleasePoller"/> tests: seeded SQLite, stubbed
/// forge clients and stubbed F-Droid HTTP. Throwing stubs prove the
/// poll makes no upstream calls when disabled or for unpollable sources.
/// </summary>
public sealed class ReleasePollerTests : IDisposable
{
    private const string FdroidIndexV2Json = """
        {
          "packages": {
            "com.example.app": {
              "versions": {
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef": {
                  "file": { "name": "/com.example.app_20.apk" },
                  "manifest": { "versionCode": 20 }
                }
              }
            }
          }
        }
        """;

    private const string FdroidApkUrl = "https://f-droid.org/repo/com.example.app_20.apk";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ShizuDbContext _db;

    public ReleasePollerTests()
    {
        _connection.Open();
        _db = new ShizuDbContext(new DbContextOptionsBuilder<ShizuDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Categories.Add(new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class StubGitHub(Func<string, string, string?, Task<SourceRelease?>> fn) : IGitHubReleaseClient
    {
        public Task<SourceRelease?> GetLatestReleaseAsync(SourceTarget target, string? etag, CancellationToken ct = default)
        {
            var (owner, repo) = target.SplitRepoKey();
            return fn(owner, repo, etag);
        }
    }

    private sealed class StubGitLab(Func<string, string?, Task<SourceRelease?>> fn) : IGitLabReleaseClient
    {
        public Task<SourceRelease?> GetLatestReleaseAsync(SourceTarget target, string? etag, CancellationToken ct = default) =>
            fn(target.Key, etag);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handler(request));
    }

    private static SourceRelease GitHubReleaseWith(string apkUrl) => new(
        "v2", null, "\"gh-etag\"",
        [new SourceAsset("app-arm64-v8a.apk", apkUrl, Primary: true, Size: 1000)]);

    private static SourceRelease GitLabReleaseWith(string apkUrl) => new(
        "v2", null, "\"gl-etag\"",
        [new SourceAsset("app.apk", apkUrl, Primary: true)]);

    private static HttpResponseMessage IndexV2Response(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private App SeedApp(string slug, string url, Availability availability = Availability.DirectApk, string? etag = null)
    {
        var app = new App
        {
            Slug = slug,
            Name = slug,
            Url = url,
            Availability = availability,
            EnrichEtag = etag,
            CategoryId = _db.Categories.Single(c => c.Slug == "audio").Id,
        };
        _db.Apps.Add(app);
        _db.SaveChanges();
        return app;
    }

    private App SeedVariant(App root, string apkUrl)
    {
        var variant = new App
        {
            Slug = "tuner-plugin",
            Name = root.Name,
            Url = root.Url,
            Availability = Availability.DirectApk,
            CategoryId = root.CategoryId,
            RootAppId = root.Id,
            PackageName = "com.acme.plugin",
        };
        _db.Apps.Add(variant);
        _db.SaveChanges();
        SeedDownload(variant.Id, apkUrl, SourceKind.GitHub);
        return variant;
    }

    private void SeedDownload(long appId, string apkUrl, SourceKind source, long? versionCode = null, bool primary = true)    {
        _db.Downloads.Add(new AppDownload
        {
            AppId = appId,
            ApkUrl = apkUrl,
            SigKey = "url:" + apkUrl,
            Source = source,
            VersionCode = versionCode,
            IsPrimary = primary,
            ResolvedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    private ReleasePoller Poller(
        IGitHubReleaseClient? github = null,
        IGitLabReleaseClient? gitlab = null,
        string? fdroidIndexV2Json = null,
        string? token = "test-token",
        bool pollEnabled = true) =>
        new(
            _db,
            github ?? new StubGitHub((_, _, _) => throw new InvalidOperationException("GitHub must not be called")),
            gitlab ?? new StubGitLab((_, _) => throw new InvalidOperationException("GitLab must not be called")),
            new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(
                new StubHandler(_ => fdroidIndexV2Json is null
                    ? throw new InvalidOperationException("F-Droid must not be called")
                    : IndexV2Response(fdroidIndexV2Json))))),
            new EnrichmentOptions { GitHubToken = token },
            new SyncOptions { PollEnabled = pollEnabled, PollParallelism = 4 });

    [Fact]
    public async Task EmptyWhenNoToken()
    {
        SeedApp("tuner", "https://github.com/acme/tuner");

        var changed = await Poller().FindChangedAsync();

        Assert.Empty(changed);
    }

    [Fact]
    public async Task EmptyWhenPollDisabled()
    {
        SeedApp("tuner", "https://github.com/acme/tuner");

        var changed = await Poller(pollEnabled: false).FindChangedAsync();

        Assert.Empty(changed);
    }

    [Fact]
    public async Task GitHubChangedUrlDetected()
    {
        const string latest = "https://github.com/acme/tuner/releases/download/v2/app-arm64-v8a.apk";
        var app = SeedApp("tuner", "https://github.com/acme/tuner", etag: "\"old-etag\"");
        SeedDownload(app.Id, "https://github.com/acme/tuner/releases/download/v1/app-arm64-v8a.apk", SourceKind.GitHub);
        string? seenEtag = null;
        var poller = Poller(github: new StubGitHub((owner, repo, etag) =>
        {
            Assert.Equal("acme", owner);
            Assert.Equal("tuner", repo);
            seenEtag = etag;
            return Task.FromResult<SourceRelease?>(GitHubReleaseWith(latest));
        }));

        var changed = await poller.FindChangedAsync();

        Assert.Equal([app.Id], changed);
        Assert.Equal("\"old-etag\"", seenEtag);
    }

    [Fact]
    public async Task GitHubSameUrlSkipped()
    {
        const string url = "https://github.com/acme/tuner/releases/download/v2/app-arm64-v8a.apk";
        var app = SeedApp("tuner", "https://github.com/acme/tuner");
        SeedDownload(app.Id, url, SourceKind.GitHub);
        var poller = Poller(github: new StubGitHub((_, _, _) =>
            Task.FromResult<SourceRelease?>(GitHubReleaseWith(url))));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task GitHubPickOnSiblingRowSkipped()
    {
        // The stable pick is a sibling row of the same release (another ABI),
        // not the primary; a recorded row URL still counts as known.
        const string primaryUrl = "https://github.com/acme/tuner/releases/download/v2/app-arm64-v8a.apk";
        const string pickedUrl = "https://github.com/acme/tuner/releases/download/v2/app-universal-release.apk";
        var app = SeedApp("tuner", "https://github.com/acme/tuner");
        SeedDownload(app.Id, primaryUrl, SourceKind.GitHub);
        SeedDownload(app.Id, pickedUrl, SourceKind.GitHub, primary: false);
        var poller = Poller(github: new StubGitHub((_, _, _) => Task.FromResult<SourceRelease?>(
            new SourceRelease("v2", null, "\"e\"", [
                new SourceAsset("app-arm64-v8a.apk", primaryUrl, Primary: true, Size: 1000),
                new SourceAsset("app-universal-release.apk", pickedUrl, Size: 2000)]))));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task GitHubPickRememberedInAnalyzedArtifactsSkipped()
    {
        // The picked stable asset is a flavor twin the enricher analyzed but
        // kept off the row; only the remembered artifact makes the release
        // known, so the poll must not flip the app on every pass.
        const string servedUrl = "https://github.com/acme/tuner/releases/download/v1.5.3-pr1/app-universal-release.apk";
        const string pickedUrl = "https://github.com/acme/tuner/releases/download/v1.5.2/app-universal-release.apk";
        var app = SeedApp("tuner", "https://github.com/acme/tuner");
        SeedDownload(app.Id, servedUrl, SourceKind.GitHub);
        var row = _db.Downloads.Single(d => d.AppId == app.Id);
        row.AnalyzedArtifacts = [$"deadbeef {pickedUrl}"];
        _db.SaveChanges();
        var poller = Poller(github: new StubGitHub((_, _, _) => Task.FromResult<SourceRelease?>(
            new SourceRelease("v1.5.2", null, "\"e\"",
                [new SourceAsset("app-universal-release.apk", pickedUrl, Primary: true, Size: 1000)]))));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task GitHubNewerPrereleaseDoesNotDirtyStableApp()
    {
        // The feed gained an automatic prerelease above the served stable.
        // Stable preference keeps selecting the stable asset, so no re-enrich.
        const string stableUrl = "https://github.com/acme/tuner/releases/download/v1.0/app.apk";
        var app = SeedApp("tuner", "https://github.com/acme/tuner");
        SeedDownload(app.Id, stableUrl, SourceKind.GitHub);
        const string feed = """
            [
              {"tag_name":"v1.1-beta","draft":false,"prerelease":true,
               "assets":[{"name":"app.apk","browser_download_url":"https://github.com/acme/tuner/releases/download/v1.1-beta/app.apk","size":10}]},
              {"tag_name":"v1.0","draft":false,"prerelease":false,
               "assets":[{"name":"app.apk","browser_download_url":"https://github.com/acme/tuner/releases/download/v1.0/app.apk","size":10}]}
            ]
            """;
        var poller = Poller(github: new GitHubReleaseClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(feed),
            })), "test-token"));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task GitHubNotModifiedSkipped()
    {
        var app = SeedApp("tuner", "https://github.com/acme/tuner");
        SeedDownload(app.Id, "https://github.com/acme/tuner/releases/download/v1/app.apk", SourceKind.GitHub);
        var poller = Poller(github: new StubGitHub((_, _, _) =>
            Task.FromResult<SourceRelease?>(null)));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task GitHubErrorIsSoft()
    {
        SeedApp("tuner", "https://github.com/acme/tuner");
        var poller = Poller(github: new StubGitHub((_, _, _) =>
            throw new GitHubApiException(HttpStatusCode.Forbidden, "rate limited")));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task GitHubReleaseWithoutApkSkipped()
    {
        var app = SeedApp("tuner", "https://github.com/acme/tuner");
        SeedDownload(app.Id, "https://github.com/acme/tuner/releases/download/v1/app.apk", SourceKind.GitHub);
        var poller = Poller(github: new StubGitHub((_, _, _) =>
            Task.FromResult<SourceRelease?>(new SourceRelease(
                "v2", null, null,
                [new SourceAsset("notes.txt", "https://example.com/notes.txt", Size: 10)]))));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task VariantRootUnchangedWhenAllApkUrlsRecorded()
    {
        const string main = "https://github.com/acme/tuner/releases/download/v1/app.apk";
        const string plugin = "https://github.com/acme/tuner/releases/download/v1/plugin.apk";
        var app = SeedApp("tuner", "https://github.com/acme/tuner");
        SeedDownload(app.Id, main, SourceKind.GitHub);
        SeedVariant(app, plugin);
        var poller = Poller(github: new StubGitHub((_, _, _) => Task.FromResult<SourceRelease?>(
            new SourceRelease("v1", null, "\"e\"", [
                new SourceAsset("app.apk", main, Size: 10),
                new SourceAsset("plugin.apk", plugin, Size: 10)]))));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task VariantRootDetectsNewPackageApk()
    {
        const string main = "https://github.com/acme/tuner/releases/download/v1/app.apk";
        const string plugin = "https://github.com/acme/tuner/releases/download/v1/plugin.apk";
        var app = SeedApp("tuner", "https://github.com/acme/tuner");
        SeedDownload(app.Id, main, SourceKind.GitHub);
        SeedVariant(app, plugin);
        var poller = Poller(github: new StubGitHub((_, _, _) => Task.FromResult<SourceRelease?>(
            new SourceRelease("v2", null, "\"e\"", [
                new SourceAsset("app.apk", main, Size: 10),
                new SourceAsset("plugin.apk", plugin.Replace("/v1/", "/v2/"), Size: 11)]))));

        Assert.Equal([app.Id], await poller.FindChangedAsync());
    }

    [Fact]
    public async Task VariantRootSkippedWhenAssetsOnlyRememberedInAnalyzedArtifacts()
    {
        // The release's whole .apk set is known only once the variant's
        // remembered twins count; row URLs alone would keep flagging it.
        const string main = "https://github.com/acme/tuner/releases/download/v2/app.apk";
        const string served = "https://github.com/acme/tuner/releases/download/v2/plugin-arm64.apk";
        const string twin = "https://github.com/acme/tuner/releases/download/v2/plugin-universal.apk";
        var app = SeedApp("tuner", "https://github.com/acme/tuner");
        SeedDownload(app.Id, main, SourceKind.GitHub);
        var variant = SeedVariant(app, served);
        var row = _db.Downloads.Single(d => d.AppId == variant.Id);
        row.AnalyzedArtifacts = [$"feedface {twin}"];
        _db.SaveChanges();
        var poller = Poller(github: new StubGitHub((_, _, _) => Task.FromResult<SourceRelease?>(
            new SourceRelease("v2", null, "\"e\"", [
                new SourceAsset("app.apk", main, Size: 10),
                new SourceAsset("plugin-arm64.apk", served, Size: 11),
                new SourceAsset("plugin-universal.apk", twin, Size: 12)]))));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task SiblingRootRecordedUrlMakesPickKnown()
    {
        // Two list entries can share one repo, each serving one package (for
        // example Lemmy Redirect and Mastodon Redirect). The known set is
        // repo-wide, so the asset recorded by the sibling entry must not flag
        // this entry even though its own rows never carry it.
        const string shared = "https://cdn.example/mastodon-release.apk";
        var sibling = SeedApp("redirect-mastodon", "https://github.com/acme/redirect");
        var app = SeedApp("redirect-lemmy", "https://github.com/acme/redirect");
        SeedDownload(sibling.Id, shared, SourceKind.GitHub);
        SeedDownload(app.Id, "https://cdn.example/lemmy-release.apk", SourceKind.GitHub);
        var poller = Poller(github: new StubGitHub((_, _, _) => Task.FromResult<SourceRelease?>(
            GitHubReleaseWith(shared))));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task SmartspacerRepoIsNotForgePolled()
    {
        // SmartspacerPlugins ships a separate release per plugin, so the
        // latest-release compare cannot see them and polling is skipped.
        SeedApp("smartspacer", "https://github.com/KieronQuinn/SmartspacerPlugins");

        Assert.Empty(await Poller().FindChangedAsync());
    }

    [Fact]
    public async Task LinkSheetPolledThroughNightlyRepo()
    {
        // LinkSheet's source repo stopped publishing releases; the poll must
        // watch LinkSheet/nightly like the enricher does, not the listed repo.
        const string url = "https://github.com/LinkSheet/nightly/releases/download/nightly-2026091203/LinkSheet-foss-nightly.apk";
        var app = SeedApp("linksheet", "https://github.com/LinkSheet/LinkSheet");
        SeedDownload(app.Id, url, SourceKind.GitHub);
        var poller = Poller(github: new StubGitHub((owner, repo, _) =>
        {
            Assert.Equal("LinkSheet", owner);
            Assert.Equal("nightly", repo);
            return Task.FromResult<SourceRelease?>(new SourceRelease(
                "nightly-2026091203", null, "\"e\"",
                [new SourceAsset("LinkSheet-foss-nightly.apk", url, Primary: true, Size: 1000)]));
        }));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task InstafelPolledThroughUpdaterRepo()
    {
        SeedApp("instafel", "https://github.com/mamiiblt/instafel");
        var poller = Poller(github: new StubGitHub((owner, repo, _) =>
        {
            Assert.Equal("instafel", owner);
            Assert.Equal("u-rel", repo);
            return Task.FromResult<SourceRelease?>(null);
        }));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task GitLabChangedUrlDetected()
    {
        const string latest = "https://gitlab.com/group/app/-/releases/v2/downloads/app.apk";
        var app = SeedApp("app", "https://gitlab.com/group/app");
        SeedDownload(app.Id, "https://gitlab.com/group/app/-/releases/v1/downloads/app.apk", SourceKind.GitLab);
        var poller = Poller(gitlab: new StubGitLab((project, _) =>
        {
            Assert.Equal("group/app", project);
            return Task.FromResult<SourceRelease?>(GitLabReleaseWith(latest));
        }));

        var changed = await poller.FindChangedAsync();

        Assert.Equal([app.Id], changed);
    }

    [Fact]
    public async Task FdroidVersionBumpDetected()
    {
        var app = SeedApp("example", "https://f-droid.org/packages/com.example.app");
        SeedDownload(app.Id, "https://f-droid.org/repo/com.example.app_10.apk", SourceKind.FDroid, versionCode: 10);
        var poller = Poller(fdroidIndexV2Json: FdroidIndexV2Json);

        var changed = await poller.FindChangedAsync();

        Assert.Equal([app.Id], changed);
    }

    [Fact]
    public async Task FdroidUnchangedSkipped()
    {
        var app = SeedApp("example", "https://f-droid.org/packages/com.example.app");
        SeedDownload(app.Id, FdroidApkUrl, SourceKind.FDroid, versionCode: 20);
        var poller = Poller(fdroidIndexV2Json: FdroidIndexV2Json);

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task ExcludedAppsIgnored()
    {
        const string latest = "https://github.com/acme/tuner/releases/download/v2/app-arm64-v8a.apk";
        var app = SeedApp("tuner", "https://github.com/acme/tuner", Availability.Excluded);
        SeedDownload(app.Id, "https://github.com/acme/tuner/releases/download/v1/app.apk", SourceKind.GitHub);
        var poller = Poller(github: new StubGitHub((_, _, _) =>
            Task.FromResult<SourceRelease?>(GitHubReleaseWith(latest))));

        Assert.Empty(await poller.FindChangedAsync());
    }

    [Fact]
    public async Task UnpollableSourcesMakeNoCalls()
    {
        SeedApp("play", "https://play.google.com/store/apps/details?id=com.example.app", Availability.LinkOnly);
        SeedApp("site", "https://example.com/app");

        // Every stub throws when called; empty means nothing was called.
        Assert.Empty(await Poller().FindChangedAsync());
    }
}
