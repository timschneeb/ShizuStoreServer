using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sources;
using SixLabors.ImageSharp;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Hermetic F-Droid/Izzy tests: inline index-v2.json samples, stubbed HTTP, real PNG handling.</summary>
public sealed class FdroidTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    // index-v2.json is the only repo index: it carries releases, screenshots
    // and the authoritative signer certificate SHA-256 map.
    private const string IndexV2Json = """
        {
          "packages": {
            "com.example.app": {
              "metadata": {
                "description": {
                  "en-US": "An <b>example</b> long description",
                  "de": "Eine <b>Beispiel</b>-Beschreibung"
                },
                "icon": {
                  "en-US": { "name": "/com.example.app/en-US/icon.png", "sha256": "c", "size": 3 },
                  "de": { "name": "/com.example.app/de/icon.png" }
                },
                "sourceCode": "https://github.com/Example/App.git",
                "screenshots": {
                  "phone": {
                    "en-US": [
                      { "name": "/com.example.app/en-US/phoneScreenshots/00.png", "sha256": "a", "size": 1 },
                      { "name": "/com.example.app/en-US/phoneScreenshots/01.png", "sha256": "b", "size": 2 }
                    ],
                    "de": [
                      { "name": "/com.example.app/de/phoneScreenshots/00.png" }
                    ]
                  },
                  "sevenInch": {
                    "en-US": [ { "name": "/com.example.app/en-US/sevenInchScreenshots/00.png" } ]
                  }
                }
              },
              "versions": {
                "AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111": {
                  "added": 1720872254000,
                  "file": {
                    "name": "/com.example.app_20.apk",
                    "sha256": "aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111",
                    "size": 1234567
                  },
                  "manifest": {
                    "versionCode": 20,
                    "versionName": "2.0",
                    "nativecode": [ "arm64-v8a" ],
                    "usesSdk": { "minSdkVersion": 26 },
                    "usesPermission": [
                      { "name": "android.permission.INTERNET" },
                      { "name": "moe.shizuku.manager.permission.API_V23" }
                    ],
                    "signer": {
                      "sha256": [ "980CEB20FD248B13EB6E224D73B3DFCD722AB120DFA6632AE8528E7BE1CFD6C9" ]
                    }
                  }
                },
                "BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222": {
                  "file": {
                    "name": "/com.example.app_10.apk",
                    "sha256": "bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222",
                    "size": 1000000
                  },
                  "manifest": { "versionCode": 10, "versionName": "1.0" }
                },
                "CCCC3333CCCC3333CCCC3333CCCC3333CCCC3333CCCC3333CCCC3333CCCC3333": {
                  "manifest": { "versionCode": 30, "versionName": "3.0" }
                }
              }
            },
            "com.example.old": {
              "versions": {
                "DDDD4444DDDD4444DDDD4444DDDD4444DDDD4444DDDD4444DDDD4444DDDD4444": {
                  "file": {
                    "name": "/com.example.old_1.apk",
                    "sha256": "dddd4444dddd4444dddd4444dddd4444dddd4444dddd4444dddd4444dddd4444"
                  },
                  "manifest": { "versionCode": 1, "versionName": "0.1" }
                }
              }
            },
            "com.example.tv": {
              "metadata": {
                "screenshots": {
                  "tv": { "en-US": [ { "name": "/com.example.tv/en-US/tvScreenshots/00.png" } ] }
                }
              }
            },
            "com.example.bare": { "metadata": {} },
            "com.example.nometa": {}
          }
        }
        """;

    private static HttpResponseMessage IndexV2Response(string? etag = "\"v2-etag\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(IndexV2Json, Encoding.UTF8, "application/json"),
        };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    [Fact]
    public void ParsesIndexV2ScreenshotsPreferredFormFactorAndLocale()
    {
        var map = FdroidIndexV2Parser.Parse(Encoding.UTF8.GetBytes(IndexV2Json)).Screenshots;

        Assert.Equal(
            ["/com.example.app/en-US/phoneScreenshots/00.png", "/com.example.app/en-US/phoneScreenshots/01.png"],
            map["com.example.app"]);
        // No phone shots: the only form factor is used.
        Assert.Equal(["/com.example.tv/en-US/tvScreenshots/00.png"], map["com.example.tv"]);
        Assert.DoesNotContain("com.example.bare", map);
        Assert.DoesNotContain("com.example.nometa", map);
    }

    [Fact]
    public void ParsesIndexV2PackagesNewestFirst()
    {
        var data = FdroidIndexV2Parser.Parse(Encoding.UTF8.GetBytes(IndexV2Json));

        // The v30 entry has no file name, so it is skipped and v20 leads.
        var packages = data.Packages["com.example.app"];
        Assert.Equal(2, packages.Count);
        Assert.Equal(10, packages[1].VersionCode);
        Assert.Null(packages[1].Added);
        var app = packages[0];
        Assert.Equal("com.example.app", app.PackageName);
        Assert.Equal(20, app.VersionCode);
        Assert.Equal("2.0", app.VersionName);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1720872254000), app.Added);
        Assert.Equal("com.example.app_20.apk", app.ApkName);
        Assert.Equal("aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111", app.Sha256);
        Assert.Equal(1234567, app.Size);
        Assert.Equal(26, app.MinSdk);
        Assert.Equal("/com.example.app/en-US/icon.png", app.IconFile);
        Assert.Equal("https://github.com/Example/App.git", app.SourceUrl);
        Assert.Equal("arm64-v8a", app.Abi);
        Assert.Equal("An <b>example</b> long description", app.LongDescription);
        Assert.Equal("980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9", app.SigSha256);
        // The index manifest carries the declared permissions; the Shizuku
        // gate reads them for index-only rows.
        Assert.Equal(
            ["android.permission.INTERNET", "moe.shizuku.manager.permission.API_V23"],
            app.Permissions);

        Assert.Equal(1, data.Packages["com.example.old"][0].VersionCode);
        Assert.DoesNotContain("com.example.bare", data.Packages);
        Assert.DoesNotContain("com.example.nometa", data.Packages);
    }

    [Fact]
    public async Task FetchesAndCachesIndexV2Screenshots()
    {
        // The repo index is fetched once per run and revalidated by ETag in
        // the next run (BeginRun starts one).
        var stub = new StubHandler(request =>
            request.Headers.IfNoneMatch.ToString().Contains("v2-etag")
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : IndexV2Response());
        var provider = new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(stub)));

        var first = await provider.GetScreenshotsAsync(FdroidRepos.FDroidBase);
        Assert.Equal(1, stub.Calls);
        provider.BeginRun();
        var second = await provider.GetScreenshotsAsync(FdroidRepos.FDroidBase);

        Assert.Equal(2, stub.Calls);
        Assert.Equal(2, first!["com.example.app"].Count);
        Assert.Equal(first["com.example.app"], second!["com.example.app"]);
        Assert.Contains("v2-etag", stub.Requests[1].Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task ReturnsNullForIndexV2On304WithoutCache()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var provider = new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(stub)));

        Assert.Null(await provider.GetScreenshotsAsync(FdroidRepos.FDroidBase));
    }

    [Fact]
    public void ParsesIndexV2SignerCertificates()
    {
        // The map keys are APK file SHA-256 values; the fixture uses uppercase
        // to prove both the key and the certificate digest are normalized.
        var data = FdroidIndexV2Parser.Parse(Encoding.UTF8.GetBytes(IndexV2Json));

        var byFile = data.Signers["com.example.app"];
        Assert.Equal(
            ["980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9"],
            byFile["aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111"]);
        Assert.DoesNotContain("com.example.tv", data.Signers);
        Assert.DoesNotContain("com.example.bare", data.Signers);
        Assert.DoesNotContain("com.example.nometa", data.Signers);
    }

    [Fact]
    public void MapsRepoBasesAndIconUrls()
    {
        Assert.Equal("https://f-droid.org/repo/", FdroidRepos.BaseFor(SourceKind.FDroid));
        Assert.Equal("https://apt.izzysoft.de/fdroid/repo/", FdroidRepos.BaseFor(SourceKind.Izzy));
        Assert.Equal(
            ["https://f-droid.org/repo/com.example.app/en-US/icon.png"],
            FdroidRepos.IconUrls("https://f-droid.org/repo/", "/com.example.app/en-US/icon.png"));
    }

    [Fact]
    public void RewritesMirrorDownloadUrlsToCanonicalUpstream()
    {
        // Mirrors exist for server-side fetches only; some refuse .apk
        // requests from non-F-Droid agents, so clients and browsers get the
        // canonical upstream URL for every configured base.
        const string fauMirror = "https://ftp.fau.de/fdroid/repo/";
        const string izzyMirror = "https://mirror.example/izzy/";
        Assert.Equal(
            "https://f-droid.org/repo/com.example.app_20.apk",
            FdroidRepos.ClientDownloadUrl(fauMirror + "com.example.app_20.apk", fauMirror, null, null));
        Assert.Equal(
            "https://apt.izzysoft.de/fdroid/repo/com.example.app_20.apk",
            FdroidRepos.ClientDownloadUrl(
                izzyMirror + "com.example.app_20.apk", FdroidRepos.DefaultFDroidBase, izzyMirror, null));
        Assert.Equal(
            "https://apt.izzysoft.de/fdroid/repo/com.example.app_20.apk",
            FdroidRepos.ClientDownloadUrl(
                "https://fallback.example/repo/com.example.app_20.apk", null, null, "https://fallback.example/repo/"));
        Assert.Equal(
            "https://github.com/Example/App/releases/download/v2/app.apk",
            FdroidRepos.ClientDownloadUrl(
                "https://github.com/Example/App/releases/download/v2/app.apk", fauMirror, null, null));
        Assert.Equal(
            "https://f-droid.org/repo/com.example.app_20.apk",
            FdroidRepos.ClientDownloadUrl("https://f-droid.org/repo/com.example.app_20.apk"));
    }

    [Fact]
    public async Task RevalidatesWithCachedEtag()
    {
        // Once-per-run semantics (request-volume fix): the first call of a run
        // seeds from the app's stored ETag and fills the cache; the second call
        // is answered from the run memo. The next run revalidates with the
        // cached ETag and serves the cached parse on 304.
        var stub = new StubHandler(request =>
            request.Headers.IfNoneMatch.ToString().Contains("v2-etag")
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : IndexV2Response());
        var provider = new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(stub)));

        var first = (await provider.GetPackageAsync(FdroidRepos.FDroidBase, "com.example.app", "\"old\"")).Value;
        Assert.Equal(1, stub.Calls);
        provider.BeginRun();
        var second = (await provider.GetPackageAsync(FdroidRepos.FDroidBase, "com.example.old", "\"old\"")).Value;

        Assert.Equal(2, stub.Calls);
        Assert.Equal(20, first.Package!.VersionCode);
        Assert.Equal("\"v2-etag\"", first.IndexEtag);
        Assert.Equal(1, second.Package!.VersionCode);
        Assert.Equal("\"v2-etag\"", second.IndexEtag);
        Assert.Contains("\"old\"", stub.Requests[0].Headers.IfNoneMatch.ToString());
        Assert.Contains("\"v2-etag\"", stub.Requests[1].Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task ClientReplaysWeakEtag()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var client = new FdroidRepoClient(new HttpClient(stub));

        Assert.Null(await client.GetIndexV2Async(FdroidRepos.FDroidBase, "W/\"fd-weak\""));

        var request = Assert.Single(stub.Requests);
        Assert.Contains("W/\"fd-weak\"", request.Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task ParallelCallsShareOneParse()
    {
        // The per-repo gate serializes concurrent callers: one 200 + parse,
        // the rest 304 off the fresh cache; no stampede, no torn state.
        var twoHundreds = 0;
        var stub = new StubHandler(request =>
        {
            if (request.Headers.IfNoneMatch.ToString().Contains("v2-etag"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            Interlocked.Increment(ref twoHundreds);
            return IndexV2Response();
        });
        var provider = new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(stub)));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            provider.GetPackageAsync(FdroidRepos.FDroidBase, "com.example.app", null)));

        Assert.All(results, r => Assert.Equal(20, r!.Value.Package!.VersionCode));
        Assert.Equal(1, twoHundreds);
    }

    [Fact]
    public async Task ReturnsNullOn304WithoutCache()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var provider = new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(stub)));

        Assert.Null(await provider.GetPackageAsync(FdroidRepos.FDroidBase, "com.example.app", "\"old\""));
    }

    [Fact]
    public async Task RefetchesIndexWhen304HasNoCache()
    {
        // A stored seed ETag can only be revalidated against a cached body;
        // without one the 304 carries no package data, so the provider falls
        // back to one unconditional read instead of reporting the package absent.
        var stub = new StubHandler(request =>
            request.Headers.IfNoneMatch.ToString().Length > 0
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : IndexV2Response());
        var provider = new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(stub)));

        var result = (await provider.GetPackageAsync(FdroidRepos.FDroidBase, "com.example.app", "\"old\"")).Value;

        Assert.Equal(20, result.Package!.VersionCode);
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task ReportsMissingPackageWithNullEntry()
    {
        var stub = new StubHandler(_ => IndexV2Response());
        var provider = new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(stub)));

        var result = (await provider.GetPackageAsync(FdroidRepos.FDroidBase, "com.example.gone", null)).Value;
        Assert.Null(result.Package);
        Assert.Equal("\"v2-etag\"", result.IndexEtag);
    }

    [Fact]
    public async Task FindsPackageByNormalizedSourceUrl()
    {
        var stub = new StubHandler(_ => IndexV2Response());
        var provider = new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(stub)));

        // metadata.sourceCode has mixed case + ".git"; the forge key is normalized.
        var match = await provider.FindPackageBySourceAsync(FdroidRepos.FDroidBase, "github:example/app");

        Assert.Equal("com.example.app", match!.PackageName);
        Assert.Null(await provider.FindPackageBySourceAsync(FdroidRepos.FDroidBase, "github:nobody/nothing"));
    }

    [Fact]
    public async Task ThrowsOnIndexHttpError()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var provider = new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(stub)));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.GetPackageAsync(FdroidRepos.FDroidBase, "com.example.app", null, CancellationToken.None));
    }

    [Fact]
    public void NormalizesRawIconBytes()
    {
        var icon = IconProcessor.ProcessRawImage(TestAssets.SolidPng(512, 512, Color.Green))!;

        using var image = Image.Load(icon.Png);
        Assert.Equal(192, image.Width);
        Assert.Equal(192, image.Height);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(icon.Png)), icon.Sha256);
    }

    [Fact]
    public void ReturnsNullForGarbageBytes() =>
        Assert.Null(IconProcessor.ProcessRawImage("not an image"u8.ToArray()));
}
