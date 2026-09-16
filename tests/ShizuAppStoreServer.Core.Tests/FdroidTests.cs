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

/// <summary>Hermetic F-Droid/Izzy tests: inline index.xml samples, stubbed HTTP, real PNG handling.</summary>
public sealed class FdroidTests
{
    private const string IndexXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <fdroid>
          <repo name="F-Droid" timestamp="1717200000000" version="21" maxage="14" url="https://f-droid.org/repo" pubkey="x"/>
          <application id="com.example.app">
            <added>2020-01-01</added>
            <lastupdated>2024-06-01</lastupdated>
            <name>Example</name>
            <summary>An example</summary>
            <desc>An &lt;b&gt;example&lt;/b&gt; long description</desc>
            <icon>com.example.app.png</icon>
            <source>https://github.com/Example/App.git</source>
            <package version="2.0" versioncode="20">
              <apkname>com.example.app_20.apk</apkname>
              <hash type="sha256">aaaabbbbcccc</hash>
              <size>1234567</size>
              <sdkver>26</sdkver>
              <targetSdkVersion>34</targetSdkVersion>
              <sig>deadbeef</sig>
              <nativecode>arm64-v8a</nativecode>
            </package>
            <package version="1.0" versioncode="10">
              <apkname>com.example.app_10.apk</apkname>
              <hash type="sha256">olderhash</hash>
              <size>1000000</size>
              <sdkver>21</sdkver>
            </package>
          </application>
          <application id="com.example.minimal">
            <name>Minimal</name>
            <package version="1" versioncode="1">
              <apkname>com.example.minimal_1.apk</apkname>
            </package>
          </application>
          <application id="com.example.broken">
            <name>Broken</name>
            <package version="1" versioncode="1">
            </package>
          </application>
          <application>
            <name>NoId</name>
          </application>
        </fdroid>
        """;

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

    private static HttpResponseMessage IndexResponse(string? etag = "\"fd-etag\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(IndexXml, Encoding.UTF8, "application/xml"),
        };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    // Only index-v2.json carries screenshots (index.xml has none).
    private const string IndexV2Json = """
        {
          "packages": {
            "com.example.app": {
              "metadata": {
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
        var map = FdroidIndexV2Parser.ParseScreenshots(Encoding.UTF8.GetBytes(IndexV2Json));

        Assert.Equal(
            ["/com.example.app/en-US/phoneScreenshots/00.png", "/com.example.app/en-US/phoneScreenshots/01.png"],
            map["com.example.app"]);
        // No phone shots: the only form factor is used.
        Assert.Equal(["/com.example.tv/en-US/tvScreenshots/00.png"], map["com.example.tv"]);
        Assert.DoesNotContain("com.example.bare", map);
        Assert.DoesNotContain("com.example.nometa", map);
    }

    [Fact]
    public async Task FetchesAndCachesIndexV2Screenshots()
    {
        // The v2 map is fetched once per run and revalidated by ETag in the
        // next run (BeginRun starts one).
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
    public void ParsesNewestPackagePerApp()
    {
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes(IndexXml));
        var index = FdroidIndexParser.Parse(xml);

        var packages = index["com.example.app"];
        Assert.Equal(2, packages.Count);
        Assert.Equal(10, packages[1].VersionCode);
        var app = packages[0];
        Assert.Equal("com.example.app", app.PackageName);
        Assert.Equal(20, app.VersionCode);
        Assert.Equal("2.0", app.VersionName);
        Assert.Equal("com.example.app_20.apk", app.ApkName);
        Assert.Equal("aaaabbbbcccc", app.Sha256);
        Assert.Equal(1234567, app.Size);
        Assert.Equal(26, app.MinSdk);
        Assert.Equal("com.example.app.png", app.IconFile);
        Assert.Equal("deadbeef", app.SigMd5);
        Assert.Equal("https://github.com/Example/App.git", app.SourceUrl);
        Assert.Equal("arm64-v8a", app.Abi);
        Assert.Equal("An <b>example</b> long description", app.LongDescription);
    }

    [Fact]
    public void ParsesRealElementFormatVersionsAndSig()
    {
        // Shape of the live f-droid.org/repo/index.xml (verified 2026-09-12):
        // version/versioncode/sig are child elements, never attributes, and
        // <sig> is a 32-hex (MD5) fingerprint.
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <fdroid>
              <application id="com.terokarvinen.x54ask">
                <name>0x54ask</name>
                <icon>com.terokarvinen.x54ask.1010200.png</icon>
                <package>
                  <version>1.1.2 (fork of Simpletask)</version>
                  <versioncode>1010200</versioncode>
                  <apkname>com.terokarvinen.x54ask_1010200.apk</apkname>
                  <hash type="sha256">f05781226bb84205caa5b5aa6a511afcb8df86de8bc4b53e33b7de34c2940e8a</hash>
                  <size>13037003</size>
                  <sdkver>29</sdkver>
                  <sig>ea783373dbdfedc0a3b22116a6ed4646</sig>
                </package>
              </application>
            </fdroid>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var index = FdroidIndexParser.Parse(stream);

        var app = index["com.terokarvinen.x54ask"][0];
        Assert.Equal(1010200, app.VersionCode);
        Assert.Equal("1.1.2 (fork of Simpletask)", app.VersionName);
        Assert.Equal("ea783373dbdfedc0a3b22116a6ed4646", app.SigMd5);
        Assert.Equal("com.terokarvinen.x54ask.1010200.png", app.IconFile);
    }

    [Fact]
    public void ToleratesMissingOptionalFieldsAndSkipsApklessPackages()
    {
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes(IndexXml));
        var index = FdroidIndexParser.Parse(xml);

        var minimal = index["com.example.minimal"][0];
        Assert.Equal(1, minimal.VersionCode);
        Assert.Equal("1", minimal.VersionName);
        Assert.Null(minimal.Sha256);
        Assert.Null(minimal.Size);
        Assert.Null(minimal.MinSdk);
        Assert.Null(minimal.IconFile);

        Assert.DoesNotContain("com.example.broken", index);
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void MapsRepoBasesAndIconUrls()
    {
        Assert.Equal("https://f-droid.org/repo/", FdroidRepos.BaseFor(SourceKind.FDroid));
        Assert.Equal("https://apt.izzysoft.de/fdroid/repo/", FdroidRepos.BaseFor(SourceKind.Izzy));
        Assert.Equal(
            ["https://f-droid.org/repo/icons-640/a.png", "https://f-droid.org/repo/icons/a.png"],
            FdroidRepos.IconUrls("https://f-droid.org/repo/", "a.png"));
    }

    [Fact]
    public async Task RevalidatesWithCachedEtag()
    {
        // Once-per-run semantics (request-volume fix): the first call of a run
        // seeds from the app's stored ETag and fills the cache; the second call
        // is answered from the run memo. The next run revalidates with the
        // cached ETag and serves the cached parse on 304.
        var stub = new StubHandler(request =>
            request.Headers.IfNoneMatch.ToString().Contains("fd-etag")
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : IndexResponse());
        var provider = new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(stub)));

        var first = (await provider.GetPackageAsync(FdroidRepos.FDroidBase, "com.example.app", "\"old\"")).Value;
        Assert.Equal(1, stub.Calls);
        provider.BeginRun();
        var second = (await provider.GetPackageAsync(FdroidRepos.FDroidBase, "com.example.minimal", "\"old\"")).Value;

        Assert.Equal(2, stub.Calls);
        Assert.Equal(20, first.Package!.VersionCode);
        Assert.Equal("\"fd-etag\"", first.IndexEtag);
        Assert.Equal(1, second.Package!.VersionCode);
        Assert.Equal("\"fd-etag\"", second.IndexEtag);
        Assert.Contains("\"old\"", stub.Requests[0].Headers.IfNoneMatch.ToString());
        Assert.Contains("\"fd-etag\"", stub.Requests[1].Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task ClientReplaysWeakEtag()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var client = new FdroidRepoClient(new HttpClient(stub));

        Assert.Null(await client.GetIndexAsync(FdroidRepos.FDroidBase, "W/\"fd-weak\""));

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
            if (request.Headers.IfNoneMatch.ToString().Contains("fd-etag"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            Interlocked.Increment(ref twoHundreds);
            return IndexResponse();
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
    public async Task ReportsMissingPackageWithNullEntry()
    {
        var stub = new StubHandler(_ => IndexResponse());
        var provider = new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(stub)));

        var result = (await provider.GetPackageAsync(FdroidRepos.FDroidBase, "com.example.gone", null)).Value;
        Assert.Null(result.Package);
        Assert.Equal("\"fd-etag\"", result.IndexEtag);
    }

    [Fact]
    public async Task FindsPackageByNormalizedSourceUrl()
    {
        var stub = new StubHandler(_ => IndexResponse());
        var provider = new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(stub)));

        // Index <source> has mixed case + ".git"; the forge key is normalized.
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
