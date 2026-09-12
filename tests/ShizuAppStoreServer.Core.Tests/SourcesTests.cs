using System.Net;
using System.Text.Json.Nodes;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sources;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>All resolver tests are hermetic: stubbed <c>HttpMessageHandler</c>, no network.</summary>
public sealed class SourcesTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo", SourceKind.GitHub)]
    [InlineData("https://www.github.com/owner/repo/", SourceKind.GitHub)]
    [InlineData("https://gitlab.com/owner/repo", SourceKind.GitLab)]
    [InlineData("https://codeberg.org/owner/repo", SourceKind.Codeberg)]
    [InlineData("https://f-droid.org/packages/com.example", SourceKind.FDroid)]
    [InlineData("https://apt.izzysoft.de/fdroid/index/apk/com.example", SourceKind.Izzy)]
    [InlineData("https://izzyondroid.com/search?query=foo", SourceKind.Izzy)]
    [InlineData("https://play.google.com/store/apps/details?id=com.example", SourceKind.Play)]
    [InlineData("https://llamalab.com/automate/", SourceKind.Other)]
    [InlineData("not a url", SourceKind.Other)]
    [InlineData(null, SourceKind.Other)]
    public void ClassifiesSourceKinds(string? url, SourceKind expected) =>
        Assert.Equal(expected, SourceClassifier.Classify(url));

    [Theory]
    [InlineData("https://github.com/papergray/MicUp", "papergray", "MicUp")]
    [InlineData("https://github.com/papergray/MicUp/", "papergray", "MicUp")]
    [InlineData("https://github.com/papergray/MicUp.git", "papergray", "MicUp")]
    [InlineData("https://github.com/owner/repo/tree/main", "owner", "repo")]
    [InlineData("https://github.com/owner/repo/releases/tag/v1.0", "owner", "repo")]
    [InlineData("https://www.github.com/owner/repo", "owner", "repo")]
    public void ParsesGitHubRepos(string url, string owner, string repo)
    {
        Assert.True(SourceClassifier.TryParseGitHubRepo(url, out var o, out var r));
        Assert.Equal(owner, o);
        Assert.Equal(repo, r);
    }

    [Theory]
    [InlineData("https://gist.github.com/owner/abc123")]
    [InlineData("https://github.com/owner")]
    [InlineData("https://github.com/")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void RejectsNonRepoUrls(string? url) =>
        Assert.False(SourceClassifier.TryParseGitHubRepo(url, out _, out _));

    [Fact]
    public void PrefersReleaseApkOverLargerSplit()
    {
        var assets = new[]
        {
            new GitHubAsset("app-arm64-v8a.apk", "https://x/arm64", 50_000_000, "application/vnd.android.package-archive"),
            new GitHubAsset("app-release.apk", "https://x/rel", 30_000_000, "application/vnd.android.package-archive"),
            new GitHubAsset("app-sources.jar", "https://x/jar", 90_000_000, "application/java-archive"),
        };
        Assert.Equal("https://x/rel", ApkAssetSelector.PickApk(assets)!.BrowserDownloadUrl);
    }

    [Fact]
    public void FallsBackToLargestApk()
    {
        var assets = new[]
        {
            new GitHubAsset("app-arm64.apk", "https://x/small", 10_000_000, null),
            new GitHubAsset("app-universal.APK", "https://x/big", 40_000_000, null),
        };
        Assert.Equal("https://x/big", ApkAssetSelector.PickApk(assets)!.BrowserDownloadUrl);
    }

    [Fact]
    public void ReturnsNullWithoutApks() =>
        Assert.Null(ApkAssetSelector.PickApk([new GitHubAsset("notes.txt", "https://x/n", 5, null)]));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    private static string ReleasesJson() => new JsonArray(
        new JsonObject
        {
            ["tag_name"] = "v2.0-beta",
            ["draft"] = false,
            ["prerelease"] = true,
            ["published_at"] = "2024-05-01T00:00:00Z",
            ["assets"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "app-release.apk",
                    ["browser_download_url"] = "https://github.com/o/r/releases/download/v2.0-beta/app.apk",
                    ["size"] = 12345678,
                    ["content_type"] = "application/vnd.android.package-archive",
                }),
        },
        new JsonObject
        {
            ["tag_name"] = "v2.0-draft",
            ["draft"] = true,
            ["prerelease"] = false,
            ["published_at"] = "2024-04-01T00:00:00Z",
            ["assets"] = new JsonArray(),
        },
        new JsonObject
        {
            ["tag_name"] = "v1.0",
            ["draft"] = false,
            ["prerelease"] = false,
            ["published_at"] = "2024-01-15T12:00:00Z",
            ["assets"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "app-release.apk",
                    ["browser_download_url"] = "https://github.com/o/r/releases/download/v1.0/app.apk",
                    ["size"] = 12345678,
                    ["content_type"] = "application/vnd.android.package-archive",
                }),
        }).ToJsonString();

    private static GitHubReleaseClient Client(StubHandler stub, string? token = "test-token") =>
        new(new HttpClient(stub), token);

    [Fact]
    public async Task PicksNewestNonDraftRelease()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleasesJson()),
        });
        var release = (await Client(stub).GetLatestReleaseAsync("o", "r", null))!;

        // Prereleases count: many Shizuku apps ship only prereleases.
        Assert.Equal("v2.0-beta", release.TagName);
        var asset = Assert.Single(release.Assets);
        Assert.Equal("app-release.apk", asset.Name);
        Assert.Equal(12345678, asset.Size);
        Assert.Equal("application/vnd.android.package-archive", asset.ContentType);
    }

    [Fact]
    public async Task SendsAuthVersionAndEtagHeaders()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleasesJson()),
        });
        stub.Requests.Clear();
        await Client(stub).GetLatestReleaseAsync("o", "r", "\"etag-1\"");

        var request = Assert.Single(stub.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
        Assert.Contains("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version"));
        Assert.Contains("\"etag-1\"", request.Headers.IfNoneMatch.ToString());
        Assert.StartsWith("https://api.github.com/repos/o/r/releases", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ReturnsNullOnNotModified()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        Assert.Null(await Client(stub).GetLatestReleaseAsync("o", "r", "\"etag-1\""));
    }

    [Fact]
    public async Task ThrowsOnUnknownRepo()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}"""),
        });
        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => Client(stub).GetLatestReleaseAsync("o", "nope", null));
        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task PicksPrereleaseWhenNoStableRelease()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonArray(new JsonObject
            {
                ["tag_name"] = "v9-beta",
                ["draft"] = false,
                ["prerelease"] = true,
                ["assets"] = new JsonArray(),
            }).ToJsonString()),
        });
        var release = (await Client(stub).GetLatestReleaseAsync("o", "r", null))!;

        Assert.Equal("v9-beta", release.TagName);
    }

    [Fact]
    public async Task SkipsDraftsAndThrowsWhenOnlyDrafts()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonArray(
                new JsonObject
                {
                    ["tag_name"] = "v9-draft",
                    ["draft"] = true,
                    ["prerelease"] = false,
                    ["assets"] = new JsonArray(),
                },
                new JsonObject
                {
                    ["tag_name"] = "v1.0",
                    ["draft"] = false,
                    ["prerelease"] = false,
                    ["assets"] = new JsonArray(),
                }).ToJsonString()),
        });
        var release = (await Client(stub).GetLatestReleaseAsync("o", "r", null))!;
        Assert.Equal("v1.0", release.TagName);

        var onlyDrafts = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonArray(new JsonObject
            {
                ["tag_name"] = "v9-draft",
                ["draft"] = true,
                ["prerelease"] = false,
                ["assets"] = new JsonArray(),
            }).ToJsonString()),
        });
        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => Client(onlyDrafts).GetLatestReleaseAsync("o", "r", null));
        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
    }

    [Theory]
    [InlineData("https://gitlab.com/sunilpaulmathew/izzyondroid", "sunilpaulmathew/izzyondroid")]
    [InlineData("https://gitlab.com/sunilpaulmathew/izzyondroid/", "sunilpaulmathew/izzyondroid")]
    [InlineData("https://gitlab.com/AuroraOSS/AuroraStore.git", "AuroraOSS/AuroraStore")]
    [InlineData("https://gitlab.com/group/sub/project", "group/sub/project")]
    [InlineData("https://gitlab.com/group/sub/project/-/releases", "group/sub/project")]
    [InlineData("https://gitlab.com/owner/repo/-/", "owner/repo")]
    [InlineData("https://www.gitlab.com/group/sub/project/-/pipelines", "group/sub/project")]
    public void ParsesGitLabRepos(string url, string projectPath)
    {
        Assert.True(SourceClassifier.TryParseGitLabRepo(url, out var path));
        Assert.Equal(projectPath, path);
    }

    [Theory]
    [InlineData("https://gitlab.com/owner")]
    [InlineData("https://gitlab.com/")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("https://gitlab.freedesktop.org/gstreamer/gstreamer")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void RejectsNonGitLabRepos(string? url) =>
        Assert.False(SourceClassifier.TryParseGitLabRepo(url, out _));

    [Theory]
    [InlineData("https://f-droid.org/packages/com.example", "com.example")]
    [InlineData("https://f-droid.org/packages/com.example/", "com.example")]
    [InlineData("https://f-droid.org/en/packages/com.example.sidebar.panel/", "com.example.sidebar.panel")]
    [InlineData("https://www.f-droid.org/packages/com.example", "com.example")]
    [InlineData("https://apt.izzysoft.de/fdroid/index/apk/com.example", "com.example")]
    [InlineData("https://apt.izzysoft.de/fdroid/index/apk/com.example/", "com.example")]
    public void ParsesFdroidPackages(string url, string packageId)
    {
        Assert.True(SourceClassifier.TryParseFdroidPackage(url, out var id));
        Assert.Equal(packageId, id);
    }

    [Theory]
    [InlineData("https://f-droid.org/repo/index.xml")]
    [InlineData("https://f-droid.org/packages/")]
    [InlineData("https://apt.izzysoft.de/fdroid/")]
    [InlineData("https://apt.izzysoft.de/fdroid/index/apk/")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void RejectsNonPackageUrls(string? url) =>
        Assert.False(SourceClassifier.TryParseFdroidPackage(url, out _));

    [Theory]
    [InlineData("https://play.google.com/store/apps/details?id=dev.zwander.cellreader", "dev.zwander.cellreader")]
    [InlineData("https://play.google.com/store/apps/details?id=show.taps&hl=en", "show.taps")]
    [InlineData("https://play.google.de/store/apps/details?hl=de&id=com.example.app", "com.example.app")]
    public void ParsesPlayPackages(string url, string packageId)
    {
        Assert.True(SourceClassifier.TryParsePlayPackage(url, out var id));
        Assert.Equal(packageId, id);
    }

    [Theory]
    [InlineData("https://play.google.com/store/apps")]
    [InlineData("https://play.google.com/store/apps/details?hl=en")]
    [InlineData("https://example.com/store/apps/details?id=com.example")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void RejectsNonPlayUrls(string? url) =>
        Assert.False(SourceClassifier.TryParsePlayPackage(url, out _));

    [Fact]
    public void PicksGitLabApkWithoutSizes()
    {
        var links = new[]
        {
            new GitLabAssetLink("app-arm64.apk", "https://a/arm64"),
            new GitLabAssetLink("app-release.apk", "https://a/rel"),
            new GitLabAssetLink("notes.txt", "https://a/notes"),
        };
        Assert.Equal("https://a/rel", ApkAssetSelector.PickApk(links, l => l.Name, _ => 0L)!.Url);
    }

    [Fact]
    public void KeepsApiOrderOnSizeTies()
    {
        var links = new[]
        {
            new GitLabAssetLink("app-a.apk", "https://a/first"),
            new GitLabAssetLink("app-b.apk", "https://a/second"),
        };
        Assert.Equal("https://a/first", ApkAssetSelector.PickApk(links, l => l.Name, _ => 0L)!.Url);
    }

    [Fact]
    public void PicksGitLabLinkByUrlWhenNameIsGeneric()
    {
        var links = new[]
        {
            new GitLabAssetLink("APK", "https://gitlab.com/narektor/other-releases/-/raw/main/batt/Batt-1.3.apk"),
            new GitLabAssetLink("Source", "https://gitlab.com/narektor/batt/-/archive/1.3/batt-1.3.zip"),
        };

        Assert.Equal(
            "https://gitlab.com/narektor/other-releases/-/raw/main/batt/Batt-1.3.apk",
            ApkAssetSelector.PickApk(links, l => l.Name, _ => 0L, l => l.Url)!.Url);
    }

    [Fact]
    public void GenericNamedNonApkLinksAreStillSkipped()
    {
        var links = new[]
        {
            new GitLabAssetLink("APK", "https://gitlab.com/o/r/-/archive/1.0/r-1.0.zip"),
            new GitLabAssetLink("Package", "https://gitlab.com/o/r/-/releases/1.0"),
        };

        Assert.Null(ApkAssetSelector.PickApk(links, l => l.Name, _ => 0L, l => l.Url));
    }

    [Fact]
    public void PicksReleaseZipForArchiveFallback()
    {
        var assets = new[]
        {
            (Name: "sources.zip", Size: 900L),
            (Name: "AppControlX-release-generated-signed-3.0.0.zip", Size: 1546134L),
            (Name: "app-release.apk", Size: 100L),
        };

        var zip = ApkAssetSelector.PickZip(assets, a => a.Name, a => a.Size);

        Assert.Equal("AppControlX-release-generated-signed-3.0.0.zip", zip.Name);
    }

    [Fact]
    public void PickZipReturnsNullWithoutArchives()
    {
        var assets = new[] { (Name: "app-release.apk", Size: 100L) };

        var zip = ApkAssetSelector.PickZip(assets, a => a.Name, a => a.Size);

        Assert.Null(zip.Name);
    }

    private static string GitLabReleasesJson() => new JsonArray(
        new JsonObject
        {
            ["tag_name"] = "v2.0-upcoming",
            ["upcoming_release"] = true,
            ["released_at"] = "2024-05-01T00:00:00Z",
            ["assets"] = new JsonObject { ["links"] = new JsonArray() },
        },
        new JsonObject
        {
            ["tag_name"] = "v1.0",
            ["upcoming_release"] = false,
            ["released_at"] = "2024-01-15T12:00:00Z",
            ["assets"] = new JsonObject
            {
                ["links"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "app-release.apk",
                        ["url"] = "https://gitlab.com/o/r/-/releases/v1.0/downloads/app.apk",
                        ["direct_asset_url"] = "https://cdn.example/app.apk",
                    }),
            },
        }).ToJsonString();

    private static GitLabReleaseClient GitLabClient(StubHandler stub, string? token = "gl-token") =>
        new(new HttpClient(stub), token);

    private static string GitLabReleasesJsonWithDescription() => new JsonArray(
        new JsonObject
        {
            ["tag_name"] = "v2.0-upcoming",
            ["upcoming_release"] = true,
            ["released_at"] = "2024-05-01T00:00:00Z",
            ["assets"] = new JsonObject { ["links"] = new JsonArray() },
        },
        new JsonObject
        {
            ["tag_name"] = "v1.0",
            ["upcoming_release"] = false,
            ["released_at"] = "2024-01-15T12:00:00Z",
            ["description"] =
                "Downloads:\n"
                + "[App standard](/uploads/sec1/App-1.0.apk)\n"
                + "[Archived](/archive.zip)\n"
                + "[App absolute](https://cdn.example/App-1.0.apk)\n"
                + "[Duplicate](https://cdn.example/app.apk)\n"
                + "[](/uploads/sec2/NoName-2.0.apk)",
            ["assets"] = new JsonObject
            {
                ["links"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "app-release.apk",
                        ["url"] = "https://gitlab.com/o/r/-/releases/v1.0/downloads/app.apk",
                        ["direct_asset_url"] = "https://cdn.example/app.apk",
                    }),
            },
        }).ToJsonString();

    [Fact]
    public async Task AddsDescriptionApkLinksAfterApiLinks()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(GitLabReleasesJsonWithDescription()),
        });
        var release = (await GitLabClient(stub).GetLatestReleaseAsync("o/r", null))!;

        Assert.Equal(4, release.Assets.Count);
        Assert.Equal("app-release.apk", release.Assets[0].Name);
        Assert.Equal("https://cdn.example/app.apk", release.Assets[0].Url);
        Assert.Equal("App standard", release.Assets[1].Name);
        Assert.Equal("https://gitlab.com/api/v4/projects/o%2Fr/uploads/sec1/App-1.0.apk", release.Assets[1].Url);
        Assert.Equal("App absolute", release.Assets[2].Name);
        Assert.Equal("https://cdn.example/App-1.0.apk", release.Assets[2].Url);
        Assert.Equal("NoName-2.0.apk", release.Assets[3].Name);
        Assert.Equal("https://gitlab.com/api/v4/projects/o%2Fr/uploads/sec2/NoName-2.0.apk", release.Assets[3].Url);
    }

    [Fact]
    public async Task PicksLatestNonUpcomingGitLabRelease()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(GitLabReleasesJson()),
        });
        var release = (await GitLabClient(stub).GetLatestReleaseAsync("o/r", null))!;

        Assert.Equal("v1.0", release.TagName);
        var link = Assert.Single(release.Assets);
        Assert.Equal("app-release.apk", link.Name);
        Assert.Equal("https://cdn.example/app.apk", link.Url);
    }

    [Fact]
    public async Task SendsTokenAndEtagToGitLab()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(GitLabReleasesJson()),
        });
        await GitLabClient(stub).GetLatestReleaseAsync("group/sub", "\"etag-1\"");

        var request = Assert.Single(stub.Requests);
        Assert.Contains("gl-token", request.Headers.GetValues("PRIVATE-TOKEN"));
        Assert.Contains("\"etag-1\"", request.Headers.IfNoneMatch.ToString());
        Assert.StartsWith("https://gitlab.com/api/v4/projects/group%2Fsub/releases", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ReturnsNullOnGitLabNotModified()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        Assert.Null(await GitLabClient(stub).GetLatestReleaseAsync("o/r", "\"etag-1\""));
    }

    [Fact]
    public async Task ThrowsOnUnknownGitLabProject()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"404 Project Not Found"}"""),
        });
        var ex = await Assert.ThrowsAsync<GitLabApiException>(
            () => GitLabClient(stub).GetLatestReleaseAsync("o/nope", null));
        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task ThrowsWithoutGitLabReleases()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonArray().ToJsonString()),
        });
        var ex = await Assert.ThrowsAsync<GitLabApiException>(
            () => GitLabClient(stub).GetLatestReleaseAsync("o/r", null));
        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
    }

    // ---- Special-case sources: instafel API + GitCode mirror ----

    [Fact]
    public async Task ParsesInstafelUncloneFile()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"fileInfos":{"unclone":{"fileUrl":"https://cdn.mamii.dev/u.apk","fileHash":"h-uc"},
                              "clone":{"fileUrl":"https://cdn.mamii.dev/c.apk","fileHash":"h-c"}}}
                """),
        });
        var release = await InstafelClient(stub).GetLatestAsync();

        Assert.Equal("https://cdn.mamii.dev/u.apk", release.ApkUrl);
        Assert.Equal("h-uc", release.FileHash);
    }

    [Fact]
    public async Task FallsBackToInstafelCloneFile()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"fileInfos":{"clone":{"fileUrl":"https://cdn.mamii.dev/c.apk","fileHash":"h-c"}}}
                """),
        });
        var release = await InstafelClient(stub).GetLatestAsync();

        Assert.Equal("https://cdn.mamii.dev/c.apk", release.ApkUrl);
        Assert.Equal("h-c", release.FileHash);
    }

    [Fact]
    public async Task ThrowsWithoutInstafelFileInfo()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"fileInfos":{}}"""),
        });
        await Assert.ThrowsAsync<InstafelApiException>(() => InstafelClient(stub).GetLatestAsync());
    }

    [Fact]
    public async Task ThrowsOnInstafelApiError()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<InstafelApiException>(() => InstafelClient(stub).GetLatestAsync());
    }

    [Fact]
    public async Task PicksNewestGitCodeReleaseAssets()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"gc-etag\"") },
            Content = new StringContent(GitCodeReleasesJson()),
        });
        var release = (await GitCodeClient(stub).GetLatestReleaseAsync("bigmolihuan", "hlbmerge_flutter", null))!;

        Assert.Equal("v2.0.5", release.TagName);
        Assert.Equal("\"gc-etag\"", release.Etag);
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal("app-arm64-v8a-release.apk", release.Assets[0].Name);
        Assert.Equal("https://gitcode.com/bigmolihuan/hlbmerge_flutter/releases/download/v2.0.5/app-arm64-v8a-release.apk", release.Assets[0].Url);
        var request = Assert.Single(stub.Requests);
        Assert.StartsWith("https://api.gitcode.com/api/v5/repos/bigmolihuan/hlbmerge_flutter/releases", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ReturnsNullOnGitCodeNotModified()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        Assert.Null(await GitCodeClient(stub).GetLatestReleaseAsync("o", "r", "\"gc-etag\""));
    }

    [Fact]
    public async Task ThrowsOnGitCodeApiError()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"404 Not Found"}"""),
        });
        var ex = await Assert.ThrowsAsync<GitCodeApiException>(
            () => GitCodeClient(stub).GetLatestReleaseAsync("o", "nope", null));
        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task ThrowsWithoutGitCodeReleases()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonArray().ToJsonString()),
        });
        var ex = await Assert.ThrowsAsync<GitCodeApiException>(
            () => GitCodeClient(stub).GetLatestReleaseAsync("o", "r", null));
        Assert.Equal(HttpStatusCode.NotFound, ex.Status);
    }

    [Fact]
    public async Task PlayClientParsesOgImage()
    {
        const string html = """<html><head><meta property="og:image" content="https://play-lh.googleusercontent.com/icon=s0-br30"><meta name="x" content="y"></head></html>""";
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html),
        });

        var iconUrl = await PlayClient(stub).GetIconUrlAsync("com.example.app");

        Assert.Equal("https://play-lh.googleusercontent.com/icon=s0-br30", iconUrl);
        Assert.StartsWith(
            "https://play.google.com/store/apps/details?id=com.example.app",
            stub.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task PlayClientReturnsNullWithoutIcon()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><head></head></html>"),
        });

        Assert.Null(await PlayClient(stub).GetIconUrlAsync("com.example.app"));
    }

    [Fact]
    public async Task PlayClientThrowsOnError()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<PlayStoreException>(
            () => PlayClient(stub).GetIconUrlAsync("com.example.app"));
        Assert.Contains("404", ex.Message);
    }

    private static string GitCodeReleasesJson() => new JsonArray(
        new JsonObject
        {
            ["tag_name"] = "v2.0.5",
            ["assets"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "app-arm64-v8a-release.apk",
                    ["browser_download_url"] = "https://gitcode.com/bigmolihuan/hlbmerge_flutter/releases/download/v2.0.5/app-arm64-v8a-release.apk",
                    ["type"] = "attach",
                },
                new JsonObject
                {
                    ["name"] = "app-armeabi-v7a-release.apk",
                    ["browser_download_url"] = "https://gitcode.com/bigmolihuan/hlbmerge_flutter/releases/download/v2.0.5/app-armeabi-v7a-release.apk",
                    ["type"] = "attach",
                },
                new JsonObject
                {
                    ["name"] = "source.zip",
                    ["browser_download_url"] = "",
                    ["type"] = "source",
                }),
        },
        new JsonObject
        {
            ["tag_name"] = "v2.0.4",
            ["assets"] = new JsonArray(),
        }).ToJsonString();

    private static InstafelReleaseClient InstafelClient(StubHandler stub) => new(new HttpClient(stub));

    private static GitCodeReleaseClient GitCodeClient(StubHandler stub) => new(new HttpClient(stub));

    private static PlayStoreClient PlayClient(StubHandler stub) => new(new HttpClient(stub));
}
