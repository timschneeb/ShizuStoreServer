using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sources;
using SixLabors.ImageSharp;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Hermetic enricher tests: stubbed GitHub/download HTTP, fake aapt2, real ZIP/PNG handling.</summary>
public sealed class AppEnricherTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ShizuDbContext _db;
    private readonly string _iconDir;
    private readonly EnrichmentOptions _options;

    public AppEnricherTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new ShizuDbContext(new DbContextOptionsBuilder<ShizuDbContext>()
            .UseSqlite(_connection)
            .Options);
        _db.Database.EnsureCreated();
        _iconDir = Path.Combine(Path.GetTempPath(), $"shizu-icons-{Guid.NewGuid():N}");
        _options = new EnrichmentOptions { IconStorePath = _iconDir };
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_iconDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public int Calls;
        public List<string> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Uris.Add(request.RequestUri!.ToString());
            return Task.FromResult(handler(request));
        }
    }

    private sealed class FakeAapt2Runner(Func<string, string> handler) : IAapt2Runner
    {
        public int Calls;
        public Task<string> DumpBadgingAsync(string apkPath, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(handler(apkPath));
        }
    }

    private sealed class FakeSignerRunner(Func<string, string> handler) : IApkSignerRunner
    {
        public int Calls;
        public Task<string> PrintCertsAsync(string apkPath, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(handler(apkPath));
        }
    }

    private sealed class FakeGitCodeClient(Func<SourceRelease> handler) : IGitCodeReleaseClient
    {
        public int Calls;
        public Task<SourceRelease?> GetLatestReleaseAsync(
            SourceTarget target, string? etag = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<SourceRelease?>(handler());
        }
    }

    private sealed class FakePlayClient(
        Func<string, string?> handler,
        Func<string, PlayAppDetails?>? details = null) : IPlayStoreClient
    {
        public int Calls;
        public int DetailCalls;

        public Task<string?> GetIconUrlAsync(string packageId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(handler(packageId));
        }

        public Task<PlayAppDetails?> GetAppDetailsAsync(string packageId, CancellationToken ct = default)
        {
            DetailCalls++;
            return Task.FromResult(details?.Invoke(packageId));
        }
    }

    /// <summary>
    /// Verbatim <c>apksigner verify --print-certs</c> output for a test key
    /// (captured from build-tools 35.0.0; the MD5 line is what matches the
    /// F-Droid index <c>&lt;sig&gt;</c>).
    /// </summary>
    private const string SignerOutputA = """
        Signer #1 certificate DN: CN=SigTest, OU=Test, O=Test, C=DE
        Signer #1 certificate SHA-256 digest: 980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9
        Signer #1 certificate SHA-1 digest: 086d90932033f759f6be9b3a76db127910822809
        Signer #1 certificate MD5 digest: c7b19fa46b32caa0fc9a49b6c8789253
        """;

    private const string SignerOutputB = """
        Signer #1 certificate DN: CN=FdroidTest, OU=Test, O=Test, C=DE
        Signer #1 certificate SHA-256 digest: 1111111111111111111111111111111111111111111111111111111111111111
        Signer #1 certificate SHA-1 digest: 2222222222222222222222222222222222222222
        Signer #1 certificate MD5 digest: b10a8db164e0754105b7a99be72e3fe5
        """;

    private static string ReleaseJson(
        string tag, string assetName, string assetUrl, long size,
        string? digest = null, string publishedAt = "2024-06-01T00:00:00Z", string? body = null)
    {
        var asset = new JsonObject
        {
            ["name"] = assetName,
            ["browser_download_url"] = assetUrl,
            ["size"] = size,
            ["content_type"] = "application/vnd.android.package-archive",
        };
        if (digest is not null)
        {
            asset["digest"] = digest;
        }

        var release = new JsonObject
        {
            ["tag_name"] = tag,
            ["draft"] = false,
            ["prerelease"] = false,
            ["published_at"] = publishedAt,
            ["assets"] = new JsonArray(asset),
        };
        if (body is not null)
        {
            release["body"] = body;
        }

        return new JsonArray(release).ToJsonString();
    }

    private static string ReleaseJsonMultiAssets(params (string Name, string Url, long Size)[] assets)
    {
        var array = new JsonArray();
        foreach (var asset in assets)
        {
            array.Add(new JsonObject
            {
                ["name"] = asset.Name,
                ["browser_download_url"] = asset.Url,
                ["size"] = asset.Size,
                ["content_type"] = "application/vnd.android.package-archive",
            });
        }

        return new JsonArray(new JsonObject
        {
            ["tag_name"] = "v1.0",
            ["draft"] = false,
            ["prerelease"] = false,
            ["published_at"] = "2024-06-01T00:00:00Z",
            ["assets"] = array,
        }).ToJsonString();
    }

    private static HttpResponseMessage JsonReleases(string json, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    private App NewApp(string slug, string name, string url, string? sourceUrl = null, bool excludeOverride = false)
    {
        var category = new Category
        {
            Name = "Audio",
            Slug = $"audio-{Guid.NewGuid():N}",
            Section = CategorySection.Apps,
        };
        _db.Categories.Add(category);
        var app = new App
        {
            Slug = slug,
            Name = name,
            Url = url,
            SourceUrl = sourceUrl,
            ExcludeOverride = excludeOverride,
            Listing = Listing.Main,
            Type = AppType.App,
            Category = category,
            AddedAt = T0,
            UpdatedAt = T0,
        };
        _db.Apps.Add(app);
        _db.SaveChanges();
        _db.Entry(app).State = EntityState.Detached;
        return _db.Apps.Include(a => a.Versions).Single(a => a.Slug == slug);
    }

    private static readonly string EmptyIndexXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <fdroid>
        </fdroid>
        """;

    // index-v2.json carries the screenshots index.xml lacks. Most tests only
    // need an empty screenshot map so the lookup stays a fast no-op.
    private static readonly string EmptyIndexV2Json = """{"packages":{}}""";

    private AppEnricher BuildEnricher(
        StubHandler github,
        StubHandler downloads,
        FakeAapt2Runner aapt2,
        StubHandler? gitlab = null,
        StubHandler? fdroid = null,
        FakeSignerRunner? signer = null,
        ILauncherIconService? launcherIcons = null,
        IGitCodeReleaseClient? gitcode = null,
        IPlayStoreClient? play = null,
        IzzyStatsProvider? izzyStats = null) =>
        new(new GitHubReleaseClient(new HttpClient(github), "tok"),
            new GitLabReleaseClient(new HttpClient(gitlab ?? new StubHandler(_ =>
                throw new InvalidOperationException("must not call GitLab")))),
            new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(fdroid ?? new StubHandler(request =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        request.RequestUri!.AbsolutePath.EndsWith("index-v2.json")
                            ? EmptyIndexV2Json
                            : EmptyIndexXml),
                })))),
            aapt2,
            signer ?? new FakeSignerRunner(_ => throw new ApkSignerException("must not run apksigner")),
            launcherIcons ?? new LauncherIconService(),
            new HttpClient(downloads), _options, _db, gitcode, play, izzyStats);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Canonical happy-path wiring: stable release → APK download → badging → icon.</summary>
    private (AppEnricher Enricher, StubHandler Github, StubHandler Downloads, FakeAapt2Runner Aapt2, byte[] Zip)
        HappyPath(string tag = "v1.0", string versionCode = "42", Color? iconColor = null, string? etag = "\"rel-etag\"", FakeSignerRunner? signer = null, string? changelog = null, string assetUrl = "https://cdn.example/app.apk")
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, iconColor ?? Color.Blue)));
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson(tag, "app-release.apk", assetUrl, zip.Length, body: changelog), etag));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: versionCode));
        return (BuildEnricher(github, downloads, aapt2, signer: signer), github, downloads, aapt2, zip);
    }

    private void Age(App app) => app.LastCheckedAt = T0 - TimeSpan.FromDays(2);

    /// <summary>Primary download candidate: the build clients install by default.</summary>
    private AppDownload Primary(App app) =>
        _db.Downloads.Local.Single(d => d.AppId == app.Id && d.IsPrimary);

    private bool HasDownloads(App app) => _db.Downloads.Local.Any(d => d.AppId == app.Id);

    private AppDownload AddDownload(
        App app,
        SourceKind source,
        string url,
        long? versionCode = null,
        string? sigSha256 = null,
        string? sigMd5 = null,
        string? sha256 = null,
        long? size = null,
        string? sourceRef = null,
        bool primary = true)
    {
        var row = new AppDownload
        {
            AppId = app.Id,
            Source = source,
            SourceRef = sourceRef,
            ApkUrl = url,
            VersionCode = versionCode,
            SigSha256 = sigSha256,
            SigMd5 = sigMd5,
            Sha256 = sha256,
            SizeBytes = size,
            SigKey = SigKeyFor(sigSha256, sigMd5, url),
            IsPrimary = primary,
            ResolvedAt = T0,
        };
        _db.Downloads.Add(row);
        _db.SaveChanges();
        return row;
    }

    private static string SigKeyFor(string? sigSha256, string? sigMd5, string url)
    {
        var sha = sigSha256?.Split(' ')[0].ToLowerInvariant();
        if (!string.IsNullOrEmpty(sha))
        {
            return sha;
        }

        var md5 = sigMd5?.Split(' ')[0].ToLowerInvariant();
        return !string.IsNullOrEmpty(md5)
            ? md5
            : "url:" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)));
    }

    [Fact]
    public async Task EnrichesGitHubAppEndToEnd()
    {
        var (enricher, _, downloads, aapt2, zip) = HappyPath();
        var app = NewApp("micup", "MicUp", "https://github.com/papergray/MicUp");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(1, downloads.Calls);
        Assert.Equal(1, aapt2.Calls);
        Assert.Equal(Availability.DirectApk, app.Availability);
        Assert.Equal(SourceKind.GitHub, app.SourceKind);
        Assert.Equal("com.example.app", app.PackageName);
        Assert.Equal("\"rel-etag\"", app.EnrichEtag);
        Assert.Equal(T0, app.LastCheckedAt);
        Assert.Null(app.LastError);

        // "Recently updated" tracks the release date, not the enrich pass.
        Assert.Equal(DateTimeOffset.Parse("2024-06-01T00:00:00Z"), app.VersionUpdatedAt);

        var primary = Primary(app);
        Assert.Equal(SourceKind.GitHub, primary.Source);
        Assert.Null(primary.SourceRef);
        Assert.Equal(42, primary.VersionCode);
        Assert.Equal("1.2.3", primary.VersionName);
        Assert.Equal(24, primary.MinSdk);
        Assert.Equal("https://cdn.example/app.apk", primary.ApkUrl);
        Assert.Equal(zip.Length, primary.SizeBytes);
        Assert.Equal(Sha256(zip), primary.Sha256);

        var iconPath = Path.Combine(_iconDir, $"{app.IconHash}.png");
        Assert.True(File.Exists(iconPath));
        using var icon = Image.Load(iconPath);
        Assert.Equal(192, icon.Width);

        var version = Assert.Single(app.Versions);
        Assert.Equal(42, version.VersionCode);
        Assert.Equal("1.2.3", version.VersionName);
        await _db.SaveChangesAsync();
        Assert.Equal(1, await _db.AppVersions.CountAsync());
    }

    [Fact]
    public async Task GitHubReleaseBodyBecomesChangelog()
    {
        var (enricher, _, _, _, _) = HappyPath(changelog: "## 1.0\n- First release");
        var app = NewApp("micup", "MicUp", "https://github.com/papergray/MicUp");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal("## 1.0\n- First release", app.Changelog);
    }

    [Fact]
    public async Task EnrichesEveryArchApkAndPrefersArm64AsPrimary()
    {
        // BiliDownOut-style release: one APK per architecture and no universal
        // build. The old selector analyzed only the largest asset (x86 here).
        var arm64 = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(128, 128, Color.Blue)));
        var armeabi = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Green)));
        var x86 = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Red)));
        var abiByLength = new Dictionary<long, string>
        {
            [arm64.Length] = "arm64-v8a",
            [armeabi.Length] = "armeabi-v7a",
            [x86.Length] = "x86",
        };
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("app-arm64-v8a-release.apk", "https://cdn.example/app-arm64-v8a-release.apk", arm64.Length),
            ("app-armeabi-v7a-release.apk", "https://cdn.example/app-armeabi-v7a-release.apk", armeabi.Length),
            ("app-x86-release.apk", "https://cdn.example/app-x86-release.apk", x86.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var bytes = path.Contains("arm64", StringComparison.Ordinal) ? arm64
                : path.Contains("armeabi", StringComparison.Ordinal) ? armeabi
                : x86;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(apkPath =>
            TestAssets.CannedBadging(versionCode: "1004", abi: abiByLength[new FileInfo(apkPath).Length]));
        var signer = new FakeSignerRunner(_ => SignerOutputA);
        var enricher = BuildEnricher(github, downloads, aapt2, signer: signer);
        var app = NewApp("bilidownout", "BiliDownOut", "https://github.com/10miaomiao/bili-down-out");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(3, downloads.Calls);
        Assert.Equal(3, aapt2.Calls);

        var rows = _db.Downloads.Local.Where(d => d.AppId == app.Id).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(1004, row.VersionCode));
        Assert.Equal(
            ["arm64-v8a", "armeabi-v7a", "x86"],
            rows.Select(row => row.Abi!).OrderBy(abi => abi, StringComparer.Ordinal).ToArray());

        // ARM64 wins regardless of asset size, so the default install fits most devices.
        var primary = Primary(app);
        Assert.Equal("arm64-v8a", primary.Abi);
        Assert.Equal("https://cdn.example/app-arm64-v8a-release.apk", primary.ApkUrl);

        // Shared app fields still come from a sibling ABI of the same release.
        Assert.Equal("com.example.app", app.PackageName);
        Assert.Equal(["android.permission.INTERNET"], app.Permissions);
        Assert.Equal(Availability.DirectApk, app.Availability);
    }

    [Fact]
    public async Task SignedMultiAbiReleaseAddsOneVersionRow()
    {
        // All ABIs share a signing key, so each sibling passes the same-variant
        // guard and reaches AddVersionRowAsync. The pending row is invisible to
        // AnyAsync, which used to insert a duplicate and roll the enrich back on
        // IX_app_versions_app_id_version_code (EnrichmentRunner.SaveChanges).
        var arm64 = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(128, 128, Color.Blue)));
        var armeabi = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Green)));
        var x86 = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Red)));
        var abiByHash = new Dictionary<string, string>
        {
            [Sha256(arm64)] = "arm64-v8a",
            [Sha256(armeabi)] = "armeabi-v7a",
            [Sha256(x86)] = "x86",
        };
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("app-arm64-v8a-release.apk", "https://cdn.example/app-arm64-v8a-release.apk", arm64.Length),
            ("app-armeabi-v7a-release.apk", "https://cdn.example/app-armeabi-v7a-release.apk", armeabi.Length),
            ("app-x86-release.apk", "https://cdn.example/app-x86-release.apk", x86.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var bytes = path.Contains("arm64", StringComparison.Ordinal) ? arm64
                : path.Contains("armeabi", StringComparison.Ordinal) ? armeabi
                : x86;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(apkPath =>
            TestAssets.CannedBadging(versionCode: "1004", abi: abiByHash[Sha256(File.ReadAllBytes(apkPath))]));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("bilidownout", "BiliDownOut", "https://github.com/10miaomiao/bili-down-out");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        await _db.SaveChangesAsync();
        Assert.Single(_db.AppVersions.Where(v => v.AppId == app.Id).ToList());
    }

    [Fact]
    public async Task MultiAbiReleaseResolvesTheLauncherIconOncePerPackage()
    {
        // Same package in every ABI, so one launcher icon. Per-variant
        // analysis used to run one Gradle render per artifact, which stalled
        // a production backfill for 15 minutes per extra ABI.
        var arm64 = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(128, 128, Color.Blue)));
        var armeabi = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Green)));
        var x86 = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Red)));
        var abiByLength = new Dictionary<long, string>
        {
            [arm64.Length] = "arm64-v8a",
            [armeabi.Length] = "armeabi-v7a",
            [x86.Length] = "x86",
        };
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("app-arm64-v8a-release.apk", "https://cdn.example/app-arm64-v8a-release.apk", arm64.Length),
            ("app-armeabi-v7a-release.apk", "https://cdn.example/app-armeabi-v7a-release.apk", armeabi.Length),
            ("app-x86-release.apk", "https://cdn.example/app-x86-release.apk", x86.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var bytes = path.Contains("arm64", StringComparison.Ordinal) ? arm64
                : path.Contains("armeabi", StringComparison.Ordinal) ? armeabi
                : x86;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(apkPath =>
            TestAssets.CannedBadging(versionCode: "1004", abi: abiByLength[new FileInfo(apkPath).Length]));
        var icon = RedIcon();
        var icons = new FakeLauncherIcons(_ => icon);
        var enricher = BuildEnricher(
            github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA), launcherIcons: icons);
        var app = NewApp("bilidownout", "BiliDownOut", "https://github.com/10miaomiao/bili-down-out");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(3, downloads.Calls);
        Assert.Equal(1, icons.Calls);
        Assert.Equal(icon.Sha256, app.IconHash);
    }

    [Fact]
    public async Task PrefersPhoneApkOverTvAndWearFlavorsOfSamePackage()
    {
        // universal-installer-style release: the same package ships phone, TV
        // and Wear builds. Only the phone build should be offered.
        var phone = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(200, 200, Color.Blue)));
        var tv = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(300, 300, Color.Green)));
        var wear = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(400, 400, Color.Red)));
        var badgingByHash = new Dictionary<string, string>
        {
            [Sha256(phone)] = TestAssets.CannedBadging(versionCode: "41", label: "Universal Installer"),
            [Sha256(tv)] = TestAssets.CannedBadging(versionCode: "2031", label: "Universal Installer",
                features: ["android.software.leanback"]),
            [Sha256(wear)] = TestAssets.CannedBadging(versionCode: "1038", label: "Universal Installer",
                features: ["android.hardware.type.watch"]),
        };
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("app-release.apk", "https://cdn.example/app-release.apk", phone.Length + 10_000),
            ("tv-release.apk", "https://cdn.example/tv-release.apk", tv.Length),
            ("wearos-release.apk", "https://cdn.example/wearos-release.apk", wear.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var bytes = path.Contains("wearos", StringComparison.Ordinal) ? wear
                : path.Contains("tv", StringComparison.Ordinal) ? tv
                : phone;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(apkPath => badgingByHash[Sha256(File.ReadAllBytes(apkPath))]);
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("universal-installer", "Universal Installer",
            "https://github.com/pass-with-high-score/universal-installer");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var row = Assert.Single(_db.Downloads.Local.Where(d => d.AppId == app.Id).ToList());
        Assert.Equal("https://cdn.example/app-release.apk", row.ApkUrl);
        Assert.Equal(41, row.VersionCode);
        Assert.Equal("Universal Installer", app.DisplayName);
    }

    [Fact]
    public async Task KeepsWearOnlyBuildWhenNoPhoneFlavorExists()
    {
        var wear = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(400, 400, Color.Red)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJson(
            "v1.0", "wearos-release.apk", "https://cdn.example/wearos-release.apk", wear.Length), "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(wear),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(
            versionCode: "1038", label: "Watch App", features: ["android.hardware.type.watch"]));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("watchapp", "Watch App", "https://github.com/example/watchapp");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var row = Assert.Single(_db.Downloads.Local.Where(d => d.AppId == app.Id).ToList());
        Assert.Equal(1038, row.VersionCode);
        Assert.Equal("Watch App", app.DisplayName);
    }

    [Fact]
    public async Task WatchOnlyPackageSurvivesAlongsideAnotherPackagesPhoneBuild()
    {
        // The phone flavor only suppresses TV/watch builds of its own package:
        // a watch-only sibling package still becomes a variant.
        var phone = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(200, 200, Color.Blue)));
        var watch = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(400, 400, Color.Red)));
        var badgingByHash = new Dictionary<string, string>
        {
            [Sha256(phone)] = TestAssets.CannedBadging(package: "com.example.app", versionCode: "1", label: "Phone App"),
            [Sha256(watch)] = TestAssets.CannedBadging(package: "com.example.plugin", versionCode: "2",
                label: "Watch Plugin", features: ["android.hardware.type.watch"]),
        };
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("app-release.apk", "https://cdn.example/app-release.apk", phone.Length + 10_000),
            ("plugin-watch-release.apk", "https://cdn.example/plugin-watch-release.apk", watch.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("plugin", StringComparison.Ordinal)
                ? watch
                : phone;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(apkPath => badgingByHash[Sha256(File.ReadAllBytes(apkPath))]);
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("toolbox", "Toolbox", "https://github.com/example/toolbox");

        await enricher.EnrichAsync(app, T0);

        var variant = Assert.Single(_db.Apps.Local.Where(a => a.RootAppId == app.Id).ToList());
        Assert.Equal("com.example.plugin", variant.PackageName);
        Assert.Equal("Watch Plugin (Toolbox)", variant.DisplayName);
        Assert.Equal("Phone App (Toolbox)", app.DisplayName);
    }

    private static string ReleasesJson(params (string Tag, string Name, string Url, long Size)[] releases)
    {
        var array = new JsonArray();
        foreach (var release in releases)
        {
            array.Add(new JsonObject
            {
                ["tag_name"] = release.Tag,
                ["draft"] = false,
                ["prerelease"] = false,
                ["published_at"] = "2024-06-01T00:00:00Z",
                ["assets"] = new JsonArray(new JsonObject
                {
                    ["name"] = release.Name,
                    ["browser_download_url"] = release.Url,
                    ["size"] = release.Size,
                    ["content_type"] = "application/vnd.android.package-archive",
                }),
            });
        }

        return array.ToJsonString();
    }

    /// <summary>Zip archive holding one entry, used to model release zips with an APK inside.</summary>
    private static byte[] BuildZip(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return ms.ToArray();
    }

    [Fact]
    public async Task EnrichesFromZipAssetWhenNoDirectApk()
    {
        var apk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var zipBytes = BuildZip(("AppControlX-release-generated-signed.apk", apk));
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v3.0.0", "AppControlX-release-generated-signed-3.0.0.zip",
                "https://cdn.example/appcontrolx.zip", zipBytes.Length), "\"zip-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "7"));
        var enricher = BuildEnricher(github, downloads, aapt2);
        var app = NewApp("appcontrolx", "AppControlX", "https://github.com/risunCode/AppControl-X");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(1, downloads.Calls);
        Assert.Equal(Availability.DirectApk, app.Availability);
        var primary = Primary(app);
        Assert.Equal(SourceKind.GitHub, primary.Source);
        Assert.Equal("https://cdn.example/appcontrolx.zip", primary.ApkUrl);
        Assert.Equal("AppControlX-release-generated-signed.apk", primary.ArchiveEntry);
        Assert.Equal(7, primary.VersionCode);
        Assert.Equal(zipBytes.Length, primary.SizeBytes);
        Assert.Equal(Sha256(zipBytes), primary.Sha256);
        Assert.NotNull(app.IconHash);
        Assert.True(File.Exists(Path.Combine(_iconDir, $"{app.IconHash}.png")));
    }

    [Fact]
    public async Task ZipWithoutApkFallsThroughToFdroidFallback()
    {
        var zipBytes = BuildZip(("release-notes.txt", "no apk here"u8.ToArray()));
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v3.0.0", "AppControlX-3.0.0.zip", "https://cdn.example/appcontrolx.zip", zipBytes.Length)));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes),
        });
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var enricher = BuildEnricher(github, downloads, aapt2);
        var app = NewApp("appcontrolx", "AppControlX", "https://github.com/risunCode/AppControl-X");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Contains("no .apk asset", app.LastError);
        Assert.False(HasDownloads(app));
    }

    [Fact]
    public async Task SkipsFreshAppsWithoutNetwork()
    {
        var (enricher, github, _, _, _) = HappyPath();
        var app = NewApp("micup", "MicUp", "https://github.com/papergray/MicUp");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(EnrichOutcome.SkippedFresh, (await enricher.EnrichAsync(app, T0)).Outcome);
        // First pass: releases + repo stats + readme. The fresh second pass
        // must add nothing.
        Assert.Equal(3, github.Calls);
    }

    [Fact]
    public async Task RefetchesLegacyRenderedReadmeButKeepsMarkdown()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var readmeCalls = 0;
        var github = new StubHandler(request =>
        {
            // Route the README call away from the releases payload the other
            // calls share, so the stored value is unambiguous.
            if (request.RequestUri!.AbsolutePath.EndsWith("/readme", StringComparison.Ordinal))
            {
                readmeCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("# Fresh\n\n```kt\nval x = 1\n```\n"),
                };
            }

            return JsonReleases(
                ReleaseJson("v1.0", "app-release.apk", "https://cdn.example/app.apk", zip.Length),
                "\"rel-etag\"");
        });
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var enricher = BuildEnricher(github, downloads, aapt2);

        var legacy = NewApp("legacy", "Legacy", "https://github.com/papergray/Legacy");
        legacy.FullDescription =
            "<div id=\"readme\" class=\"md\" data-path=\"README.md\"><p>rendered</p></div>";
        await enricher.EnrichAsync(legacy, T0);
        Assert.Equal("# Fresh\n\n```kt\nval x = 1\n```\n", legacy.FullDescription);
        Assert.Equal(1, readmeCalls);

        var markdown = NewApp("markdown", "Markdown", "https://github.com/papergray/Markdown");
        markdown.FullDescription = "# Already";
        await enricher.EnrichAsync(markdown, T0);
        Assert.Equal("# Already", markdown.FullDescription);
        Assert.Equal(1, readmeCalls);
    }

    [Fact]
    public async Task Honors304AsUpToDate()
    {
        var (first, _, _, _, _) = HappyPath();
        var app = NewApp("micup", "MicUp", "https://github.com/papergray/MicUp");
        await first.EnrichAsync(app, T0);
        await _db.SaveChangesAsync();

        var etagSeen = new List<string>();
        var github = new StubHandler(request =>
        {
            etagSeen.AddRange(request.Headers.IfNoneMatch.Select(e => e.ToString()));
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var downloads = new StubHandler(_ => throw new InvalidOperationException("must not download on 304"));
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2 on 304"));
        Age(app);

        var result = await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Equal(["\"rel-etag\""], etagSeen);
        Assert.Equal(0, aapt2.Calls);
        Assert.Equal(1, await _db.AppVersions.CountAsync());
    }

    [Fact]
    public async Task GitHubHealRefetchesOn304WhenPermissionsMissing()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var releases = ReleaseJson("v1.0", "app-release.apk", "https://cdn.example/app.apk", zip.Length);
        var app = NewApp("ghheal", "GhHeal", "https://github.com/example/ghheal");
        var signer = new FakeSignerRunner(_ => SignerOutputA);
        var firstGithub = new StubHandler(_ => JsonReleases(releases, "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        Assert.Equal(
            EnrichOutcome.Enriched,
            (await BuildEnricher(firstGithub, downloads, aapt2, signer: signer).EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();

        // Simulate a pre-fix row: fully analyzed build recorded, permissions blank.
        app.Permissions = [];
        await _db.SaveChangesAsync();
        Age(app);

        var etagSeen = new List<string>();
        var github = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/releases", StringComparison.Ordinal))
            {
                if (request.Headers.IfNoneMatch.Count > 0)
                {
                    etagSeen.AddRange(request.Headers.IfNoneMatch.Select(e => e.ToString()));
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }

                return JsonReleases(releases, "\"rel-etag\"");
            }

            if (url.EndsWith("/readme", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"stargazers_count":7,"owner":{"login":"example","html_url":"https://github.com/example"}}"""),
            };
        });
        var aapt2Calls = aapt2.Calls;
        var downloadCalls = downloads.Calls;

        var result = await BuildEnricher(github, downloads, aapt2, signer: signer).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(["\"rel-etag\""], etagSeen);
        Assert.True(aapt2.Calls > aapt2Calls);
        Assert.True(downloads.Calls > downloadCalls);
        Assert.Equal("android.permission.INTERNET", Assert.Single(app.Permissions));
    }

    [Fact]
    public async Task AppendsVersionRowOnlyOnBump()
    {
        var (first, _, _, _, _) = HappyPath();
        var app = NewApp("micup", "MicUp", "https://github.com/papergray/MicUp");
        await first.EnrichAsync(app, T0);
        await _db.SaveChangesAsync();

        // Same release again → checksum unchanged, so skipped, no duplicate row.
        Age(app);
        var (second, _, _, _, _) = HappyPath();
        Assert.Equal(EnrichOutcome.UpToDate, (await second.EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();
        Assert.Equal(1, await _db.AppVersions.CountAsync());

        // New upstream version → second row + orphaned icon file deleted.
        var oldIcon = Path.Combine(_iconDir, $"{app.IconHash}.png");
        Assert.True(File.Exists(oldIcon));
        Age(app);
        var (third, _, _, _, _) = HappyPath(tag: "v1.1", versionCode: "43", iconColor: Color.Red, assetUrl: "https://cdn.example/app-v1.1.apk");
        Assert.Equal(EnrichOutcome.Enriched, (await third.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(43, Primary(app).VersionCode);
        Assert.False(File.Exists(oldIcon));
        await _db.SaveChangesAsync();
        Assert.Equal(2, await _db.AppVersions.CountAsync());
    }

    [Fact]
    public async Task SameVersionCodeWithNewNameDoesNotDuplicateVersionRow()
    {
        // Live case (vFlow): v1.5.3-pr1 reused 1.5.2's versionCode. The check
        // matched (code, name) while the index is (app, code), so the insert
        // violated it and rolled back stars/permissions with the whole save.
        AppEnricher Wiring(string tag, string versionName, Color iconColor)
        {
            var zip = TestAssets.BuildApk(
                (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, iconColor)));
            var github = new StubHandler(_ => JsonReleases(
                ReleaseJson(tag, "app-release.apk", $"https://cdn.example/{tag}/app-release.apk", zip.Length),
                "\"rel-etag\""));
            var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zip),
            });
            var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(
                versionCode: "42", versionName: versionName));
            return BuildEnricher(github, downloads, aapt2);
        }

        var app = NewApp("retagged", "Retagged", "https://github.com/example/retagged");
        Assert.Equal(EnrichOutcome.Enriched, (await Wiring("v1.0", "1.2.3", Color.Blue).EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();
        Assert.Equal(1, await _db.AppVersions.CountAsync());

        // Same code under a new tag/name: no duplicate row, enrich still lands.
        // Different icon bytes keep the build distinct for the checksum guard.
        Age(app);
        Assert.Equal(EnrichOutcome.Enriched, (await Wiring("v1.1-pr1", "1.2.4", Color.Red).EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();
        Assert.Equal(1, await _db.AppVersions.CountAsync());
    }

    [Fact]
    public async Task KeepsSharedIconWhileOtherAppUsesIt()
    {
        var (enricher, _, _, _, _) = HappyPath();
        var appA = NewApp("appa", "App A", "https://github.com/o/ra");
        var appB = NewApp("appb", "App B", "https://github.com/o/rb");
        await enricher.EnrichAsync(appA, T0);
        await enricher.EnrichAsync(appB, T0);
        Assert.Equal(appA.IconHash, appB.IconHash);
        var sharedIcon = Path.Combine(_iconDir, $"{appA.IconHash}.png");

        Age(appA);
        var (second, _, _, _, _) = HappyPath(tag: "v1.1", versionCode: "43", iconColor: Color.Red, assetUrl: "https://cdn.example/app-v1.1.apk");
        await second.EnrichAsync(appA, T0);

        Assert.NotEqual(appA.IconHash, appB.IconHash);
        Assert.True(File.Exists(sharedIcon));
    }

    [Fact]
    public async Task RecordsFailureAndBacksOff()
    {
        var github = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}"""),
        });
        var downloads = new StubHandler(_ => throw new InvalidOperationException("must not download"));
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var app = NewApp("gone", "Gone", "https://github.com/o/deleted");

        var result = await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Contains("404", app.LastError);
        Assert.Equal(T0, app.LastCheckedAt);
        Assert.Equal(Availability.LinkOnly, app.Availability);
        Assert.False(HasDownloads(app));

        // Immediate retry is skipped (backoff); after the window it retries.
        Assert.Equal(EnrichOutcome.SkippedFresh, (await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0)).Outcome);
        // Releases plus the best-effort stats call, which also 404s here.
        Assert.Equal(2, github.Calls);
    }

    [Fact]
    public async Task ReportsAapt2Failure()
    {
        var (_, github, downloads, _, _) = HappyPath();
        var aapt2 = new FakeAapt2Runner(_ => throw new Aapt2Exception("not an APK"));
        var app = NewApp("bad", "Bad", "https://github.com/o/bad");

        var result = await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Contains("aapt2", app.LastError);
    }

    [Fact]
    public async Task FallsBackToAvatarWithoutGitHubSource()
    {
        var github = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub"));
        var downloads = new StubHandler(_ => throw new InvalidOperationException("must not download"));
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var app = NewApp("codeberg", "Some App", "https://codeberg.org/some/app");

        var result = await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.AvatarFallback, result.Outcome);
        Assert.Equal(SourceKind.Codeberg, app.SourceKind);
        Assert.Equal(Availability.LinkOnly, app.Availability);
        Assert.Null(app.LastError);
        var iconPath = Path.Combine(_iconDir, $"{app.IconHash}.png");
        Assert.True(File.Exists(iconPath));
        using var icon = Image.Load(iconPath);
        Assert.Equal(192, icon.Width);
    }

    [Fact]
    public async Task PlayListingIconReplacesLetterAvatarForExternalOnlyApp()
    {
        var github = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}"""),
        });
        var iconBytes = TestAssets.SolidPng(512, 512, Color.Red);
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(iconBytes),
        });
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var play = new FakePlayClient(_ => "https://play-lh.googleusercontent.com/icon=s0-br30");
        var app = NewApp("plays-only", "Play Only",
            "https://play.google.com/store/apps/details?id=com.example.playonly",
            "https://github.com/example/playonly");

        var result = await BuildEnricher(github, downloads, aapt2, play: play).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.AvatarFallback, result.Outcome);
        Assert.Equal(Availability.PlayRedirect, app.Availability);
        Assert.Equal(app.Url, app.StoreUrl);
        Assert.Null(app.ExcludedReason);
        Assert.False(HasDownloads(app));
        Assert.Null(app.LastError);
        Assert.Equal(1, play.Calls);
        Assert.NotEqual(LetterAvatarGenerator.Generate("Play Only").Sha256, app.IconHash);
        Assert.True(File.Exists(Path.Combine(_iconDir, $"{app.IconHash}.png")));
    }

    [Fact]
    public async Task PlayListingWithoutIconFallsBackToPlay()
    {
        var github = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}"""),
        });
        var downloads = new StubHandler(_ => throw new InvalidOperationException("must not download"));
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var play = new FakePlayClient(_ => null);
        var app = NewApp("plays-fail", "Play Fail",
            "https://play.google.com/store/apps/details?id=com.example.playfail",
            "https://github.com/example/playfail");

        var result = await BuildEnricher(github, downloads, aapt2, play: play).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.AvatarFallback, result.Outcome);
        Assert.Equal(Availability.PlayRedirect, app.Availability);
        Assert.Equal(app.Url, app.StoreUrl);
        Assert.Null(app.ExcludedReason);
        Assert.Null(app.LastError);
        Assert.Equal(LetterAvatarGenerator.Generate("Play Fail").Sha256, app.IconHash);
        Assert.True(File.Exists(Path.Combine(_iconDir, $"{app.IconHash}.png")));
        Assert.Equal(1, play.Calls);
    }

    [Fact]
    public async Task PlayListingDetailsPopulateExternalOnlyApp()
    {
        var github = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}"""),
        });
        var iconBytes = TestAssets.SolidPng(512, 512, Color.Red);
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(iconBytes),
        });
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var play = new FakePlayClient(
            _ => throw new InvalidOperationException("details carry the icon"),
            _ => new PlayAppDetails(
                "https://play-lh.googleusercontent.com/icon=s0-br30",
                "HyperOS MIUI 5G Switcher",
                "7220530116384657977",
                "MeowBit",
                "https://play.google.com/store/apps/dev?id=7220530116384657977",
                "2.6.0-new",
                "NOTE: This app only supports HyperOS & MIUI system.",
                DateTimeOffset.Parse("2026-07-22T00:00:00Z")));
        var app = NewApp("plays-details", "Play Details",
            "https://play.google.com/store/apps/details?id=com.ysy.switcherfiveg",
            "https://github.com/example/switcher");

        var result = await BuildEnricher(github, downloads, aapt2, play: play).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.AvatarFallback, result.Outcome);
        Assert.Equal(Availability.PlayRedirect, app.Availability);
        Assert.Equal("com.ysy.switcherfiveg", app.PackageName);
        Assert.Equal("MeowBit", app.AuthorName);
        Assert.Equal(
            "https://play.google.com/store/apps/dev?id=7220530116384657977",
            app.AuthorUrl);
        Assert.Equal("play:7220530116384657977", app.AuthorKey);
        Assert.Equal("2.6.0-new", app.VersionName);
        Assert.Equal("NOTE: This app only supports HyperOS & MIUI system.", app.FullDescription);
        Assert.Equal(DateTimeOffset.Parse("2026-07-22T00:00:00Z"), app.VersionUpdatedAt);
        Assert.Equal(IconProcessor.ProcessRawImage(iconBytes)!.Sha256, app.IconHash);
        Assert.Equal(0, play.Calls);
        Assert.Equal(1, play.DetailCalls);
    }

    [Fact]
    public async Task FallbackPrefersPlayIconOverGeneratedAvatar()
    {
        var github = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub"));
        var iconBytes = TestAssets.SolidPng(512, 512, Color.Red);
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(iconBytes),
        });
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var play = new FakePlayClient(_ => "https://play-lh.googleusercontent.com/icon=s0-br30");
        var app = NewApp("play-redirect", "Play Redirect",
            "https://play.google.com/store/apps/details?id=com.example.pr",
            "https://example.com/page");

        var result = await BuildEnricher(github, downloads, aapt2, play: play).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.AvatarFallback, result.Outcome);
        Assert.Equal(Availability.PlayRedirect, app.Availability);
        Assert.Equal("https://play.google.com/store/apps/details?id=com.example.pr", app.StoreUrl);
        Assert.Equal(IconProcessor.ProcessRawImage(iconBytes)!.Sha256, app.IconHash);
        Assert.Equal(1, play.Calls);
    }

    [Fact]
    public async Task ExcludedPlayOnlyAppStillExcluded()
    {
        var github = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub"));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(TestAssets.SolidPng(512, 512, Color.Red)),
        });
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var play = new FakePlayClient(_ => "https://play-lh.googleusercontent.com/icon=s0-br30");
        var app = NewApp("play-only-x", "Play Excluded",
            "https://play.google.com/store/apps/details?id=com.example.pe");

        var result = await BuildEnricher(github, downloads, aapt2, play: play).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Excluded, result.Outcome);
        Assert.Equal(Availability.Excluded, app.Availability);
        Assert.Contains("no APK available", app.ExcludedReason);
        Assert.False(HasDownloads(app));
        Assert.Equal(1, play.Calls);
    }

    [Theory]
    // Play + GitHub combos: GitHub wins for the APK, Play stays as store_url.
    [InlineData("https://play.google.com/store/apps/details?id=com.x", "https://github.com/o/r")]
    [InlineData("https://github.com/o/r", "https://play.google.com/store/apps/details?id=com.x")]
    public async Task KeepsPlayLinkAsStoreUrl(string url, string sourceUrl)
    {
        var (enricher, _, _, _, _) = HappyPath();
        var app = NewApp("combo", "Combo", url, sourceUrl);

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal("https://play.google.com/store/apps/details?id=com.x", app.StoreUrl);
        Assert.Equal(Availability.DirectApk, app.Availability);
    }

    [Fact]
    public async Task FallsBackToAvatarWhenApkHasNoRasterIcon()
    {
        var zip = TestAssets.BuildApk(("res/mipmap-anydpi-v26/ic.xml", "<adaptive-icon/>"u8.ToArray()));
        var github = new StubHandler(_ => JsonReleases(ReleaseJson("v1.0", "app.apk", "https://cdn.example/app.apk", zip.Length)));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ =>
            "package: name='com.x' versionCode='1' versionName='1'\n" +
            "application-icon-640:'res/mipmap-anydpi-v26/ic.xml'\n");
        var app = NewApp("adaptive", "Adaptive", "https://github.com/o/adaptive");

        Assert.Equal(EnrichOutcome.Enriched, (await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0)).Outcome);
        Assert.Equal(Availability.DirectApk, app.Availability);
        using var icon = Image.Load(Path.Combine(_iconDir, $"{app.IconHash}.png"));
        Assert.Equal(192, icon.Width);
    }

    private static string GitLabJson(string tag, string linkName, string linkUrl, string? description = null)
    {
        var release = new JsonObject
        {
            ["tag_name"] = tag,
            ["upcoming_release"] = false,
            ["released_at"] = "2024-06-01T00:00:00Z",
            ["assets"] = new JsonObject
            {
                ["links"] = new JsonArray(new JsonObject
                {
                    ["name"] = linkName,
                    ["url"] = linkUrl,
                    ["direct_asset_url"] = linkUrl,
                }),
            },
        };
        if (description is not null)
        {
            release["description"] = description;
        }

        return new JsonArray(release).ToJsonString();
    }

    private static HttpResponseMessage GitLabReleases(string json, string? etag = "\"gl-etag\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    private static string GitLabJsonMultiAssets(string tag, params (string Name, string Url)[] links)
    {
        var array = new JsonArray();
        foreach (var (name, url) in links)
        {
            array.Add(new JsonObject
            {
                ["name"] = name,
                ["url"] = url,
                ["direct_asset_url"] = url,
            });
        }

        return new JsonArray(new JsonObject
        {
            ["tag_name"] = tag,
            ["upcoming_release"] = false,
            ["released_at"] = "2024-06-01T00:00:00Z",
            ["assets"] = new JsonObject { ["links"] = array },
        }).ToJsonString();
    }

    /// <summary>GitLab happy path: release link → APK download → badging → icon.</summary>
    private (AppEnricher Enricher, StubHandler Gitlab, StubHandler Downloads, FakeAapt2Runner Aapt2, byte[] Zip)
        GitLabHappyPath(string tag = "v1.0")
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var gitlab = new StubHandler(_ => GitLabReleases(
            GitLabJson(tag, "app-release.apk", "https://cdn.example/app.apk")));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var github = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub"));
        var fdroid = new StubHandler(_ => throw new InvalidOperationException("must not fetch index"));
        return (BuildEnricher(github, downloads, aapt2, gitlab, fdroid), gitlab, downloads, aapt2, zip);
    }

    // Real index.xml shape (element-style version/versioncode/sig;
    // regression cover for the M4 attribute-only parser bug).
    private const string FdroidIndexXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <fdroid>
          <application id="com.example.app">
            <name>Example</name>
            <desc>A &lt;b&gt;plain&lt;/b&gt; summary of the app.</desc>
            <icon>com.example.app.png</icon>
            <source>https://github.com/example/aod</source>
            <package>
              <version>2.0</version>
              <versioncode>20</versioncode>
              <apkname>com.example.app_20.apk</apkname>
              <hash type="sha256">0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef</hash>
              <size>1234567</size>
              <sdkver>26</sdkver>
              <sig>b10a8db164e0754105b7a99be72e3fe5</sig>
            </package>
          </application>
        </fdroid>
        """;

    // One release per architecture plus a genuinely older package (1.0) that
    // must not be mistaken for an ABI sibling.
    private const string FdroidIndexXmlWithArchSiblings = """
        <?xml version="1.0" encoding="utf-8"?>
        <fdroid>
          <application id="com.example.app">
            <name>Example</name>
            <icon>com.example.app.png</icon>
            <source>https://github.com/example/aod</source>
            <package>
              <version>2.0</version>
              <versioncode>2004</versioncode>
              <apkname>com.example.app_2004.apk</apkname>
              <hash type="sha256">aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111</hash>
              <size>2222222</size>
              <sdkver>26</sdkver>
              <sig>b10a8db164e0754105b7a99be72e3fe5</sig>
              <nativecode>arm64-v8a</nativecode>
            </package>
            <package>
              <version>2.0</version>
              <versioncode>2003</versioncode>
              <apkname>com.example.app_2003.apk</apkname>
              <hash type="sha256">bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222</hash>
              <size>1111111</size>
              <sdkver>26</sdkver>
              <sig>b10a8db164e0754105b7a99be72e3fe5</sig>
              <nativecode>armeabi-v7a</nativecode>
            </package>
            <package>
              <version>1.0</version>
              <versioncode>1000</versioncode>
              <apkname>com.example.app_1000.apk</apkname>
              <hash type="sha256">cccc3333cccc3333cccc3333cccc3333cccc3333cccc3333cccc3333cccc3333</hash>
              <size>999999</size>
              <sdkver>26</sdkver>
              <sig>b10a8db164e0754105b7a99be72e3fe5</sig>
              <nativecode>x86</nativecode>
            </package>
          </application>
        </fdroid>
        """;

    /// <summary>F-Droid wiring: index fetch + icon mirror; the APK download is attempted but 404s, exercising the index-only fallback.</summary>
    private (AppEnricher Enricher, StubHandler Fdroid, StubHandler Downloads)
        FdroidHappyPath(byte[]? iconBytes = null, bool icon404 = false, Func<HttpResponseMessage>? githubResponse = null,
            IzzyStatsProvider? izzyStats = null, string? indexV2Json = null)
    {
        iconBytes ??= TestAssets.SolidPng(256, 256, Color.Purple);
        var fdroid = new StubHandler(request =>
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("index-v2.json")
                ? indexV2Json ?? EmptyIndexV2Json
                : FdroidIndexXml;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        var downloads = new StubHandler(request =>
        {
            if (icon404 || !request.RequestUri!.ToString().Contains("/icons"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(iconBytes) };
        });
        var github = githubResponse is null
            ? new StubHandler(_ => throw new InvalidOperationException("must not call GitHub"))
            : new StubHandler(_ => githubResponse());
        var gitlab = new StubHandler(_ => throw new InvalidOperationException("must not call GitLab"));
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        return (BuildEnricher(github, downloads, aapt2, gitlab, fdroid, izzyStats: izzyStats), fdroid, downloads);
    }

    [Fact]
    public async Task EnrichesGitLabAppEndToEnd()
    {
        var (enricher, _, downloads, aapt2, zip) = GitLabHappyPath();
        var app = NewApp("izzy", "IzzyOnDroid", "https://gitlab.com/sunilpaulmathew/izzyondroid");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(1, downloads.Calls);
        Assert.Equal(1, aapt2.Calls);
        Assert.Equal(Availability.DirectApk, app.Availability);
        Assert.Equal(SourceKind.GitLab, app.SourceKind);
        Assert.Equal("com.example.app", app.PackageName);
        var primary = Primary(app);
        Assert.Equal(SourceKind.GitLab, primary.Source);
        Assert.Null(primary.SourceRef);
        Assert.Equal(42L, primary.VersionCode);
        Assert.Equal("https://cdn.example/app.apk", primary.ApkUrl);
        Assert.Equal((long)zip.Length, primary.SizeBytes);
        Assert.Equal(Sha256(zip), primary.Sha256);
        Assert.Equal("\"gl-etag\"", app.EnrichEtag);
        Assert.Null(app.LastError);
        Assert.True(File.Exists(Path.Combine(_iconDir, $"{app.IconHash}.png")));
        var version = Assert.Single(app.Versions);
        Assert.Equal(42, version.VersionCode);
    }

    [Fact]
    public async Task EnrichesEveryGitLabArchLinkAndPrefersArm64AsPrimary()
    {
        // GitLab release links carry no sizes, so the shared pipeline records
        // every per-architecture APK and ABI order picks the default.
        var arm64 = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(128, 128, Color.Blue)));
        var armeabi = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Green)));
        var x86 = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Red)));
        var abiByLength = new Dictionary<long, string>
        {
            [arm64.Length] = "arm64-v8a",
            [armeabi.Length] = "armeabi-v7a",
            [x86.Length] = "x86",
        };
        var gitlab = new StubHandler(_ => GitLabReleases(GitLabJsonMultiAssets(
            "v1.0",
            ("app-x86-release.apk", "https://cdn.example/app-x86-release.apk"),
            ("app-armeabi-v7a-release.apk", "https://cdn.example/app-armeabi-v7a-release.apk"),
            ("app-arm64-v8a-release.apk", "https://cdn.example/app-arm64-v8a-release.apk"))));
        var downloads = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var bytes = path.Contains("arm64", StringComparison.Ordinal) ? arm64
                : path.Contains("armeabi", StringComparison.Ordinal) ? armeabi
                : x86;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(apkPath =>
            TestAssets.CannedBadging(versionCode: "1004", abi: abiByLength[new FileInfo(apkPath).Length]));
        var signer = new FakeSignerRunner(_ => SignerOutputA);
        var enricher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("must not call GitHub")),
            downloads, aapt2, gitlab,
            new StubHandler(_ => throw new InvalidOperationException("must not fetch index")),
            signer: signer);
        var app = NewApp("glarch", "GlArch", "https://gitlab.com/example/glarch");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(3, downloads.Calls);
        Assert.Equal(3, aapt2.Calls);
        var rows = _db.Downloads.Local.Where(d => d.AppId == app.Id).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(
            ["arm64-v8a", "armeabi-v7a", "x86"],
            rows.Select(row => row.Abi!).OrderBy(abi => abi, StringComparer.Ordinal).ToArray());
        var primary = Primary(app);
        Assert.Equal("arm64-v8a", primary.Abi);
        Assert.Equal("https://cdn.example/app-arm64-v8a-release.apk", primary.ApkUrl);
        Assert.Equal("com.example.app", app.PackageName);
        Assert.Equal(["android.permission.INTERNET"], app.Permissions);
    }

    [Fact]
    public async Task GitLabRecordsStarsReadmeAndPermissions()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var gitlab = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/repository/files/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("# FMD Android\n\nFind your device.\n"),
                };
            }

            if (url.Contains("/releases", StringComparison.Ordinal))
            {
                return GitLabReleases(GitLabJson(
                    "v1.0", "app-release.apk", "https://cdn.example/app.apk",
                    description: "## 1.0\n- First release"));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":21844919,"star_count":522,"default_branch":"master","readme_url":"https://gitlab.com/o/r/-/blob/master/README.md"}"""),
            };
        });
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var github = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub"));
        var fdroid = new StubHandler(_ => throw new InvalidOperationException("must not fetch index"));
        var app = NewApp("glmeta", "GlMeta", "https://gitlab.com/o/r");

        var result = await BuildEnricher(github, downloads, aapt2, gitlab, fdroid)
            .EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(522, app.Stars);
        Assert.Equal("# FMD Android\n\nFind your device.\n", app.FullDescription);
        Assert.Equal("## 1.0\n- First release", app.Changelog);
        Assert.Equal("android.permission.INTERNET", Assert.Single(app.Permissions));
    }

    [Fact]
    public async Task GitLabHealRedownloadsSameAssetWhenPermissionsMissing()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var gitlab = new StubHandler(request =>
        {
            if (request.RequestUri!.ToString().Contains("/repository/files/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.RequestUri.ToString().Contains("/releases", StringComparison.Ordinal))
            {
                return GitLabReleases(GitLabJson("v1.0", "app-release.apk", "https://cdn.example/app.apk"));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":1,"star_count":7,"default_branch":"master"}"""),
            };
        });
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var github = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub"));
        var fdroid = new StubHandler(_ => throw new InvalidOperationException("must not fetch index"));
        var signer = new FakeSignerRunner(_ => SignerOutputA);
        var app = NewApp("glheal", "GlHeal", "https://gitlab.com/o/glheal");

        Assert.Equal(
            EnrichOutcome.Enriched,
            (await BuildEnricher(github, downloads, aapt2, gitlab, fdroid, signer: signer).EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();

        // Simulate a pre-fix row: fully analyzed build recorded, permissions blank.
        app.Permissions = [];
        await _db.SaveChangesAsync();
        Age(app);
        var aapt2Calls = aapt2.Calls;
        var downloadCalls = downloads.Calls;

        Assert.Equal(
            EnrichOutcome.Enriched,
            (await BuildEnricher(github, downloads, aapt2, gitlab, fdroid, signer: signer).EnrichAsync(app, T0)).Outcome);

        Assert.True(aapt2.Calls > aapt2Calls);
        Assert.True(downloads.Calls > downloadCalls);
        Assert.Equal("android.permission.INTERNET", Assert.Single(app.Permissions));
    }

    [Fact]
    public async Task GitLabHealRefetchesOn304WhenPermissionsMissing()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var etagSeen = new List<string>();
        var gitlab = new StubHandler(request =>
        {
            if (request.RequestUri!.ToString().Contains("/repository/files/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.RequestUri.ToString().Contains("/releases", StringComparison.Ordinal))
            {
                if (request.Headers.IfNoneMatch.Count > 0)
                {
                    etagSeen.AddRange(request.Headers.IfNoneMatch.Select(e => e.ToString()));
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }

                return GitLabReleases(GitLabJson("v1.0", "app-release.apk", "https://cdn.example/app.apk"));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":1,"star_count":7,"default_branch":"master"}"""),
            };
        });
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var signer = new FakeSignerRunner(_ => SignerOutputA);
        var app = NewApp("glheal304", "GlHeal304", "https://gitlab.com/o/glheal304");
        var enricher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("must not call GitHub")),
            downloads, aapt2, gitlab,
            new StubHandler(_ => throw new InvalidOperationException("must not fetch index")),
            signer: signer);

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();

        // Simulate a pre-fix row: fully analyzed build recorded, permissions blank.
        app.Permissions = [];
        await _db.SaveChangesAsync();
        Age(app);
        var aapt2Calls = aapt2.Calls;
        var downloadCalls = downloads.Calls;

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(["\"gl-etag\""], etagSeen);
        Assert.True(downloads.Calls > downloadCalls);
        Assert.True(aapt2.Calls > aapt2Calls);
        Assert.Equal("android.permission.INTERNET", Assert.Single(app.Permissions));
    }

    [Theory]
    // Play + GitLab combos: GitLab wins for the APK, Play stays as store_url.
    [InlineData("https://play.google.com/store/apps/details?id=com.x", "https://gitlab.com/o/r")]
    [InlineData("https://gitlab.com/o/r", "https://play.google.com/store/apps/details?id=com.x")]
    public async Task KeepsPlayLinkAsStoreUrlForGitLab(string url, string sourceUrl)
    {
        var (enricher, _, _, _, _) = GitLabHappyPath();
        var app = NewApp("combo-gl", "Combo", url, sourceUrl);

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal("https://play.google.com/store/apps/details?id=com.x", app.StoreUrl);
        Assert.Equal(Availability.DirectApk, app.Availability);
    }

    [Fact]
    public async Task TreatsSameGitLabAssetAsUpToDate()
    {
        var (first, _, _, _, _) = GitLabHappyPath();
        var app = NewApp("fmd", "FindMyDevice", "https://gitlab.com/fmd-foss/fmd-android");
        await first.EnrichAsync(app, T0);
        await _db.SaveChangesAsync();

        var gitlab = new StubHandler(_ => GitLabReleases(GitLabJson("v1.0", "app-release.apk", "https://cdn.example/app.apk")));
        var downloads = new StubHandler(_ => throw new InvalidOperationException("must not re-download same asset"));
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        Age(app);

        var result = await BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("must not call GitHub")),
            downloads, aapt2, gitlab).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Equal(1, await _db.AppVersions.CountAsync());
    }

    [Fact]
    public async Task RecordsGitLabFailure()
    {
        var gitlab = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"404 Project Not Found"}"""),
        });
        var app = NewApp("gone-gl", "Gone", "https://gitlab.com/o/deleted");

        var result = await BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("must not call GitHub")),
            new StubHandler(_ => throw new InvalidOperationException("must not download")),
            new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2")),
            gitlab).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Contains("GitLab", app.LastError);
        Assert.Contains("404", app.LastError);
    }

    [Fact]
    public async Task ReportsGitLabReleaseWithoutApk()
    {
        var gitlab = new StubHandler(_ => GitLabReleases(GitLabJson("v1.0", "notes.txt", "https://cdn.example/notes.txt")));
        var app = NewApp("noapk", "NoApk", "https://gitlab.com/o/noapk");

        var result = await BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("must not call GitHub")),
            new StubHandler(_ => throw new InvalidOperationException("must not download")),
            new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2")),
            gitlab).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Contains("no .apk", app.LastError);
    }

    [Fact]
    public async Task EnrichesFDroidAppEndToEnd()
    {
        var iconBytes = TestAssets.SolidPng(256, 256, Color.Purple);
        var (enricher, fdroid, downloads) = FdroidHappyPath(iconBytes);
        var app = NewApp("catshare", "CatShare", "https://f-droid.org/packages/com.example.app/");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        // index.xml plus the two index-v2 screenshot lookups (F-Droid, Izzy).
        Assert.Equal(3, fdroid.Calls);
        Assert.Equal(2, downloads.Calls); // APK attempt (404 → index-only) + icon
        Assert.Equal(Availability.DirectApk, app.Availability);
        Assert.Equal(SourceKind.FDroid, app.SourceKind);
        Assert.Equal("com.example.app", app.PackageName);
        var primary = Primary(app);
        Assert.Equal(SourceKind.FDroid, primary.Source);
        Assert.Equal("com.example.app", primary.SourceRef);
        Assert.Equal(20L, primary.VersionCode);
        Assert.Equal("2.0", primary.VersionName);
        Assert.Equal(26, primary.MinSdk);
        Assert.Equal("https://f-droid.org/repo/com.example.app_20.apk", primary.ApkUrl);
        Assert.Equal(1234567L, primary.SizeBytes);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", primary.Sha256);
        Assert.Null(primary.SigSha256); // no APK analyzed: SHA-256 unknown
        Assert.Equal("b10a8db164e0754105b7a99be72e3fe5", primary.SigMd5); // index <sig>
        Assert.Null(app.LastError);
        // F-Droid publishes no release dates, so the app stays unknown and sorts
        // last under "recently updated".
        Assert.Null(app.VersionUpdatedAt);
        // The application-level <desc> is the only changelog text the index has.
        Assert.Equal("A <b>plain</b> summary of the app.", app.Changelog);
        // Icon is the mirrored repo PNG, normalized to 192px.
        Assert.Equal(IconProcessor.ProcessRawImage(iconBytes)!.Sha256, app.IconHash);
        using var icon = Image.Load(Path.Combine(_iconDir, $"{app.IconHash}.png"));
        Assert.Equal(192, icon.Width);
        var version = Assert.Single(app.Versions);
        Assert.Equal(20, version.VersionCode);
        Assert.Equal("https://f-droid.org/repo/com.example.app_20.apk", version.ApkUrl);
    }

    [Fact]
    public async Task AppliesScreenshotsFromIndexV2()
    {
        // index-v2.json is the only F-Droid/Izzy artifact that carries
        // screenshots; they are matched by package name and left as upstream
        // URLs. The Izzy host serves the same body, so its duplicate relative
        // names are dropped.
        const string indexV2 = """
            {
              "packages": {
                "com.example.app": {
                  "metadata": {
                    "screenshots": {
                      "phone": {
                        "en-US": [
                          { "name": "/com.example.app/en-US/phoneScreenshots/00.png" },
                          { "name": "/com.example.app/en-US/phoneScreenshots/01.png" }
                        ],
                        "de": [
                          { "name": "/com.example.app/de/phoneScreenshots/00.png" }
                        ]
                      }
                    }
                  }
                }
              }
            }
            """;
        var (enricher, _, _) = FdroidHappyPath(indexV2Json: indexV2);
        var app = NewApp("catshare", "CatShare", "https://f-droid.org/packages/com.example.app/");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(
            [
                "https://f-droid.org/repo/com.example.app/en-US/phoneScreenshots/00.png",
                "https://f-droid.org/repo/com.example.app/en-US/phoneScreenshots/01.png",
            ],
            app.Screenshots);
    }

    [Fact]
    public async Task ScreenshotsFromReachableRepoSurviveOtherRepoOutage()
    {
        // 2026-09-16: while Izzy answered connection refused, the throw escaped
        // the loop and discarded the F-Droid screenshots that had already been
        // parsed, leaving every later app without any.
        const string indexV2 = """
            {
              "packages": {
                "com.example.app": {
                  "metadata": {
                    "screenshots": {
                      "phone": {
                        "en-US": [
                          { "name": "/com.example.app/en-US/phoneScreenshots/00.png" }
                        ]
                      }
                    }
                  }
                }
              }
            }
            """;
        var iconBytes = TestAssets.SolidPng(256, 256, Color.Purple);
        var fdroid = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "apt.izzysoft.de")
            {
                throw new HttpRequestException("Connection refused (apt.izzysoft.de:443)");
            }

            var body = request.RequestUri.AbsolutePath.EndsWith("index-v2.json")
                ? indexV2
                : FdroidIndexXml;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        var downloads = new StubHandler(request => request.RequestUri!.ToString().Contains("/icons")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(iconBytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var enricher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("must not call GitHub")),
            downloads,
            new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2")),
            fdroid: fdroid);
        var app = NewApp("catshare", "CatShare", "https://f-droid.org/packages/com.example.app/");
        app.Screenshots =
        [
            "https://apt.izzysoft.de/fdroid/repo/com.example.app/en-US/phoneScreenshots/99.png",
        ];

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(
            [
                "https://f-droid.org/repo/com.example.app/en-US/phoneScreenshots/00.png",
                "https://apt.izzysoft.de/fdroid/repo/com.example.app/en-US/phoneScreenshots/99.png",
            ],
            app.Screenshots);
    }

    [Fact]
    public async Task KeepsScreenshotsWhenBothReposUnreachable()
    {
        // A repo that answers can clear gone screenshots; one that refuses the
        // connection must not, or every outage wipes the app's screenshots.
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJson(
            "v1.0", "app-release.apk", "https://cdn.example/app.apk", zip.Length), "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var enricher = BuildEnricher(
            github,
            downloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "42")),
            fdroid: new StubHandler(_ => throw new HttpRequestException("Connection refused")));
        var app = NewApp("offline", "Offline", "https://github.com/o/offline");
        string[] existing =
        [
            "https://f-droid.org/repo/com.example.app/en-US/phoneScreenshots/00.png",
            "https://apt.izzysoft.de/fdroid/repo/com.example.app/en-US/phoneScreenshots/01.png",
        ];
        app.Screenshots = [.. existing];

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(existing, app.Screenshots);
    }

    [Fact]
    public async Task EnrichesFDroidAppFromSourceUrl()
    {
        var (enricher, _, _) = FdroidHappyPath();
        var play = "https://play.google.com/store/apps/details?id=com.x";
        var app = NewApp("combo-fd", "Combo", play, "https://f-droid.org/packages/com.example.app");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(play, app.StoreUrl);
        Assert.Equal(Availability.DirectApk, app.Availability);
    }

    [Fact]
    public async Task ResolvesIzzyRepoBase()
    {
        var (enricher, _, _) = FdroidHappyPath();
        var app = NewApp("amarok", "Amarok", "https://apt.izzysoft.de/fdroid/index/apk/com.example.app");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(SourceKind.Izzy, app.SourceKind);
        Assert.StartsWith("https://apt.izzysoft.de/fdroid/repo/", Primary(app).ApkUrl);
    }

    [Fact]
    public async Task IzzyAppGetsRollingDownloadCount()
    {
        var stats = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"com.example.app":12345}"""),
        });
        var izzyStats = new IzzyStatsProvider(new IzzyStatsClient(new HttpClient(stats)));
        var (enricher, _, _) = FdroidHappyPath(izzyStats: izzyStats);
        var app = NewApp("amarok", "Amarok", "https://apt.izzysoft.de/fdroid/index/apk/com.example.app");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(12345, app.DownloadTotal);
    }

    [Fact]
    public async Task FdroidAppHasNoDownloadCount()
    {
        // f-droid.org publishes no counts, so the stats provider must not be
        // consulted for an F-Droid-primary app.
        var stats = new StubHandler(_ => throw new InvalidOperationException("must not fetch Izzy stats"));
        var izzyStats = new IzzyStatsProvider(new IzzyStatsClient(new HttpClient(stats)));
        var (enricher, _, _) = FdroidHappyPath(izzyStats: izzyStats);
        var app = NewApp("hail", "Hail", "https://f-droid.org/packages/com.example.app");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Null(app.DownloadTotal);
    }

    [Fact]
    public async Task TreatsSameFdroidVersionAsUpToDate()
    {
        var (first, _, _) = FdroidHappyPath();
        var app = NewApp("hail", "Hail", "https://f-droid.org/packages/com.example.app");
        await first.EnrichAsync(app, T0);
        await _db.SaveChangesAsync();

        var (second, _, downloads) = FdroidHappyPath();
        Age(app);

        // Index re-fetched, but the recorded version is current: no icon traffic.
        Assert.Equal(EnrichOutcome.UpToDate, (await second.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(0, downloads.Calls);
        Assert.Equal(1, await _db.AppVersions.CountAsync());
    }

    /// <summary>F-Droid wiring with a downloadable APK: index fetch + full analysis.</summary>
    private (AppEnricher Enricher, StubHandler Downloads, FakeAapt2Runner Aapt2, byte[] Zip)
        FdroidAnalyzedPath()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var iconBytes = TestAssets.SolidPng(256, 256, Color.Purple);
        var fdroid = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(FdroidIndexXml),
        });
        var downloads = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.ToString().Contains("/icons")
                ? new ByteArrayContent(iconBytes)
                : new ByteArrayContent(zip),
        });
        var github = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub"));
        var gitlab = new StubHandler(_ => throw new InvalidOperationException("must not call GitLab"));
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "20"));
        var signer = new FakeSignerRunner(_ => SignerOutputA);
        return (BuildEnricher(github, downloads, aapt2, gitlab, fdroid, signer: signer), downloads, aapt2, zip);
    }

    [Fact]
    public async Task FdroidPathRecordsAnalyzedPermissions()
    {
        var (enricher, _, _, _) = FdroidAnalyzedPath();
        var app = NewApp("fdperm", "FdPerm", "https://f-droid.org/packages/com.example.app/");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(SourceKind.FDroid, Primary(app).Source);
        Assert.Equal("android.permission.INTERNET", Assert.Single(app.Permissions));
    }

    [Fact]
    public async Task EnrichesFdroidArchSiblingsAsIndexOnlyRows()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var iconBytes = TestAssets.SolidPng(256, 256, Color.Purple);
        var fdroid = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(FdroidIndexXmlWithArchSiblings),
        });
        var downloads = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.ToString().Contains("/icons")
                ? new ByteArrayContent(iconBytes)
                : new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "2004", abi: "arm64-v8a"));
        var signer = new FakeSignerRunner(_ => SignerOutputA);
        var enricher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("must not call GitHub")),
            downloads, aapt2,
            new StubHandler(_ => throw new InvalidOperationException("must not call GitLab")),
            fdroid, signer: signer);
        var app = NewApp("fdarch", "FdArch", "https://f-droid.org/packages/com.example.app/");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(1, downloads.Calls);
        Assert.Equal(1, aapt2.Calls);

        var rows = _db.Downloads.Local.Where(d => d.AppId == app.Id).ToList();
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, row => row.Abi == "x86");

        var primary = Primary(app);
        Assert.Equal("arm64-v8a", primary.Abi);
        Assert.Equal(SourceKind.FDroid, primary.Source);
        Assert.NotNull(primary.Sha256);

        var sibling = rows.Single(row => row.Abi == "armeabi-v7a");
        Assert.False(sibling.IsPrimary);
        Assert.Equal(2003, sibling.VersionCode);
        Assert.Equal("b10a8db164e0754105b7a99be72e3fe5", sibling.SigMd5);
        Assert.Equal("bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222", sibling.Sha256);

        Assert.Equal("com.example.app", app.PackageName);
        Assert.Equal(["android.permission.INTERNET"], app.Permissions);
    }

    [Fact]
    public async Task FdroidHealReanalyzesWhenPermissionsMissing()
    {
        var (first, _, _, _) = FdroidAnalyzedPath();
        var app = NewApp("fdheal", "FdHeal", "https://f-droid.org/packages/com.example.app/");
        await first.EnrichAsync(app, T0);
        await _db.SaveChangesAsync();

        // Simulate a pre-fix row: fully analyzed build recorded, permissions blank.
        app.Permissions = [];
        await _db.SaveChangesAsync();
        Age(app);

        var (second, downloads, aapt2, _) = FdroidAnalyzedPath();
        Assert.Equal(EnrichOutcome.Enriched, (await second.EnrichAsync(app, T0)).Outcome);

        Assert.Equal(1, downloads.Calls); // same APK re-downloaded once
        Assert.Equal(1, aapt2.Calls); // and re-analyzed
        Assert.Equal("android.permission.INTERNET", Assert.Single(app.Permissions));
    }

    [Fact]
    public async Task FdroidHealRefetchesOn304WhenPermissionsMissing()
    {
        var (first, _, _, _) = FdroidAnalyzedPath();
        var app = NewApp("fdheal304", "FdHeal304", "https://f-droid.org/packages/com.example.app/");
        await first.EnrichAsync(app, T0);
        await _db.SaveChangesAsync();

        // Simulate a pre-fix row: fully analyzed build recorded, permissions
        // blank, and the old index ETag still on file.
        app.Permissions = [];
        app.EnrichEtag = "\"fd-etag\"";
        await _db.SaveChangesAsync();
        Age(app);

        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var iconBytes = TestAssets.SolidPng(256, 256, Color.Purple);
        var etagSeen = new List<string>();
        var fdroid = new StubHandler(request =>
        {
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                etagSeen.AddRange(request.Headers.IfNoneMatch.Select(e => e.ToString()));
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(FdroidIndexXml),
            };
        });
        var downloads = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.ToString().Contains("/icons")
                ? new ByteArrayContent(iconBytes)
                : new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "20"));
        var signer = new FakeSignerRunner(_ => SignerOutputA);

        var result = await BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("must not call GitHub")),
            downloads, aapt2,
            new StubHandler(_ => throw new InvalidOperationException("must not call GitLab")),
            fdroid, signer: signer).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(["\"fd-etag\""], etagSeen);
        Assert.Equal(1, aapt2.Calls);
        Assert.Equal("android.permission.INTERNET", Assert.Single(app.Permissions));
    }

    [Fact]
    public async Task FdroidIndexOnlyRowStaysUpToDate()
    {
        // Index-only rows never had an analysis to replay (no SHA-256
        // identity), so blank permissions must not trigger re-downloads.
        var (first, _, _) = FdroidHappyPath();
        var app = NewApp("fdidx", "FdIdx", "https://f-droid.org/packages/com.example.app/");
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        Assert.Empty(app.Permissions);
        Assert.Null(Primary(app).SigSha256);
        await _db.SaveChangesAsync();
        Age(app);

        var (second, _, downloads) = FdroidHappyPath();
        Assert.Equal(EnrichOutcome.UpToDate, (await second.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(0, downloads.Calls);
    }

    [Fact]
    public async Task ReportsMissingFdroidPackage()
    {
        var (enricher, _, _) = FdroidHappyPath();
        var app = NewApp("gone-fd", "Gone", "https://f-droid.org/packages/com.example.gone");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Contains("not in", app.LastError);
    }

    // ---- F-Droid source fallback + symmetric candidates ----

    [Fact]
    public async Task FallsBackToFdroidWhenGitHubHasNoApk()
    {
        // Forge-first app whose releases ship no APK: the F-Droid index is
        // matched by the application's <source> URL and locks the source.
        var (enricher, fdroid, _) = FdroidHappyPath(githubResponse: () => JsonReleases(ReleaseJson(
            "v1.0", "source.zip", "https://cdn.example/source.zip", 100)));
        var app = NewApp("aod", "Always On Display", "https://github.com/example/aod");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        // One index fetch for the source lookup (the regular package read
        // rides the run memo) plus one screenshot lookup per repo.
        Assert.Equal(3, fdroid.Calls);
        Assert.Equal(Availability.DirectApk, app.Availability);
        var primary = Primary(app);
        Assert.Equal(SourceKind.FDroid, primary.Source);
        Assert.Equal("com.example.app", primary.SourceRef);
        Assert.Equal("https://f-droid.org/repo/com.example.app_20.apk", primary.ApkUrl);
    }

    [Fact]
    public async Task FallsBackToFdroidWhenGitHubHasNoRelease()
    {
        var (enricher, _, _) = FdroidHappyPath(githubResponse: () => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}"""),
        });
        var app = NewApp("aod-404", "Always On Display", "https://github.com/example/aod");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var primary = Primary(app);
        Assert.Equal(SourceKind.FDroid, primary.Source);
        Assert.Equal("com.example.app", primary.SourceRef);
    }

    [Fact]
    public async Task FallsBackToFdroidByUrlPackageIdWhenForgeFails()
    {
        // The rescue apps carry the F-Droid listing as primary URL while the
        // forge link is only the SourceUrl: GitHub resolves first and fails,
        // then the URL's package id is used directly.
        var (enricher, _, _) = FdroidHappyPath(githubResponse: () => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}"""),
        });
        var app = NewApp("fd-primary", "Rescue", "https://f-droid.org/packages/com.example.app",
            "https://github.com/example/aod");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var primary = Primary(app);
        Assert.Equal(SourceKind.FDroid, primary.Source);
        Assert.Equal("com.example.app", primary.SourceRef);
        Assert.Equal("https://f-droid.org/repo/com.example.app_20.apk", primary.ApkUrl);
    }

    [Fact]
    public async Task FdroidEnrichRecordsForgeCandidate()
    {
        // Symmetric candidates: an F-Droid primary never suppresses a forge
        // release; clients pick by installed signature.
        var forgeZip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Green)));
        var iconBytes = TestAssets.SolidPng(256, 256, Color.Purple);
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v9.9", "app-release.apk", "https://cdn.example/forge.apk", forgeZip.Length)));
        var downloads = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/icons"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(iconBytes) };
            }

            if (url.Contains("forge.apk"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(forgeZip) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var fdroid = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(FdroidIndexXml),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var app = NewApp("fd-plus-forge", "Both", "https://f-droid.org/packages/com.example.app");

        var result = await BuildEnricher(github, downloads, aapt2, fdroid: fdroid).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var rows = _db.Downloads.Local.Where(d => d.AppId == app.Id).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, d => d.Source == SourceKind.FDroid);
        Assert.Contains(rows, d => d.Source == SourceKind.GitHub && d.ApkUrl == "https://cdn.example/forge.apk");
        Assert.Equal(SourceKind.GitHub, rows.Single(d => d.IsPrimary).Source);
    }

    [Fact]
    public async Task AnalyzedBuildThatLosesPrimaryStillStampsCheck()
    {
        // Live 2026-09-15: two rows sat at null LastCheckedAt forever (caught
        // by the never_checked report) because the analyzed-but-not-primary
        // return left no stamp, so every pass re-downloaded them.
        var (enricher, _, _, _, _) = HappyPath(versionCode: "42");
        var app = NewApp("loser", "Loser", "https://github.com/example/loser");
        AddDownload(app, SourceKind.GitHub, "https://example.com/newer.apk", versionCode: 100);

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Equal("https://example.com/newer.apk", Primary(app).ApkUrl);
        Assert.Equal(T0, app.LastCheckedAt);
        Assert.Null(app.LastError);
    }

    // ---- Special cases: instafel updater repo + GitCode mirror ----

    [Fact]
    public async Task EnrichesInstafelUpdaterFromURelRepo()
    {
        const string assetUrl =
            "https://github.com/instafel/u-rel/releases/download/v5.1.1/ifl-updater-v5.1.1-release.apk";
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var requested = new List<string>();
        var github = new StubHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            requested.Add(uri);

            // The list links mamiiblt/instafel; the updater ships from u-rel.
            return uri.Contains("/releases", StringComparison.Ordinal)
                ? JsonReleases(ReleaseJson("v5.1.1", "ifl-updater-v5.1.1-release.apk", assetUrl, zip.Length))
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var downloads = new StubHandler(request =>
        {
            Assert.Equal(assetUrl, request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "511"));
        var app = NewApp("instafel", "Instafel", "https://github.com/mamiiblt/instafel");

        var result = await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(1, downloads.Calls);
        Assert.Contains(requested,
            u => u == "https://api.github.com/repos/instafel/u-rel/releases?per_page=100");
        Assert.DoesNotContain(requested, u => u.Contains("mamiiblt", StringComparison.Ordinal));
        Assert.Equal(Availability.DirectApk, app.Availability);
        var primary = Primary(app);
        Assert.Equal(SourceKind.GitHub, primary.Source);
        Assert.Equal(assetUrl, primary.ApkUrl);
        Assert.NotNull(app.IconHash);
    }

    [Fact]
    public async Task EnrichesHlbmergeFromGitCode()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var github = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub"));
        var downloads = new StubHandler(request =>
        {
            Assert.Equal(
                "https://gitcode.com/bigmolihuan/hlbmerge_flutter/releases/download/v2.0.5/app-arm64-v8a-release.apk",
                request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        });
        var gitcode = new FakeGitCodeClient(() => new SourceRelease("v2.0.5", null, "\"gc-etag\"",
        [
            new SourceAsset("app-arm64-v8a-release.apk",
                "https://gitcode.com/bigmolihuan/hlbmerge_flutter/releases/download/v2.0.5/app-arm64-v8a-release.apk",
                Primary: true),
        ]));
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "205"));
        var app = NewApp("hlbmerge-flutter", "HLBmerge Flutter", "https://github.com/molihuan/hlbmerge_flutter");

        var result = await BuildEnricher(github, downloads, aapt2, gitcode: gitcode).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(1, gitcode.Calls);
        var primary = Primary(app);
        Assert.Equal(
            "https://gitcode.com/bigmolihuan/hlbmerge_flutter/releases/download/v2.0.5/app-arm64-v8a-release.apk",
            primary.ApkUrl);
        Assert.Equal(SourceKind.Other, primary.Source);
        Assert.Equal("\"gc-etag\"", app.EnrichEtag);
        Assert.NotNull(app.IconHash);
    }

    [Fact]
    public async Task GitCodeUnchangedAssetSkipsDownload()
    {
        var apk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var url = "https://gitcode.com/bigmolihuan/hlbmerge_flutter/releases/download/v2.0.5/app-arm64-v8a-release.apk";
        var github = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub"));
        var gitcode = new FakeGitCodeClient(() => new SourceRelease("v2.0.5", null, "\"gc-etag\"",
        [
            new SourceAsset("app-arm64-v8a-release.apk", url, Primary: true),
        ]));
        var app = NewApp("hlbmerge-flutter", "HLBmerge Flutter", "https://github.com/molihuan/hlbmerge_flutter");

        var first = BuildEnricher(github,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(apk) }),
            new FakeAapt2Runner(_ => TestAssets.CannedBadging()),
            gitcode: gitcode).EnrichAsync(app, T0);
        Assert.Equal(EnrichOutcome.Enriched, (await first).Outcome);
        await _db.SaveChangesAsync();

        Age(app);
        var result = await BuildEnricher(github,
            new StubHandler(_ => throw new InvalidOperationException("must not download")),
            new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2")),
            gitcode: gitcode).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Equal("asset URL unchanged", result.Detail);
        Assert.Equal(2, gitcode.Calls);
    }

    [Fact]
    public async Task GitCodeWithoutApkAssetsFails()
    {
        var github = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub"));
        var gitcode = new FakeGitCodeClient(() => new SourceRelease("v2.0.5", null, null,
        [
            new SourceAsset("source.zip",
                "https://gitcode.com/bigmolihuan/hlbmerge_flutter/releases/download/v2.0.5/source.zip"),
        ]));
        var app = NewApp("hlbmerge-flutter", "HLBmerge Flutter", "https://github.com/molihuan/hlbmerge_flutter");

        var result = await BuildEnricher(github,
            new StubHandler(_ => throw new InvalidOperationException("must not download")),
            new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2")),
            gitcode: gitcode).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Contains("no .apk asset", app.LastError);
    }

    // ---- M8: signatures + F-Droid alternate variant ----

    // Element-format index matching the canned badging (com.example.app, v42).
    private const string VariantIndexXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <fdroid>
          <application id="com.example.app">
            <name>Example</name>
            <package>
              <version>4.2</version>
              <versioncode>42</versioncode>
              <apkname>com.example.app_42.apk</apkname>
              <hash type="sha256">aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</hash>
              <size>7654321</size>
              <sdkver>26</sdkver>
              <sig>b10a8db164e0754105b7a99be72e3fe5</sig>
            </package>
          </application>
        </fdroid>
        """;

    /// <summary>Forge primary + F-Droid variant, downloads routed by host.</summary>
    private (AppEnricher Enricher, StubHandler Downloads) ForgeWithVariant(
        byte[] primaryZip,
        byte[] variantZip,
        FakeSignerRunner? signer = null,
        string indexXml = VariantIndexXml,
        bool variantDownload404 = false)
    {
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v4.2", "app-release.apk", "https://cdn.example/app.apk", primaryZip.Length)));
        var fdroid = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(indexXml),
        });
        var downloads = new StubHandler(request =>
        {
            if (request.RequestUri!.ToString().Contains("f-droid.org"))
            {
                return variantDownload404
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(variantZip) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(primaryZip) };
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        return (BuildEnricher(github, downloads, aapt2, signer: signer, fdroid: fdroid), downloads);
    }

    [Fact]
    public async Task RecordsPrimarySignaturesAndFdroidVariant()
    {
        var primaryZip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Blue)));
        var variantZip = new byte[] { 1, 2, 3, 4, 5 };
        var call = 0;
        var (enricher, downloads) = ForgeWithVariant(primaryZip, variantZip,
            new FakeSignerRunner(_ => call++ == 0 ? SignerOutputA : SignerOutputB));
        var app = NewApp("dual", "Dual", "https://github.com/example/app");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(2, downloads.Calls); // primary + variant APK
        var rows = _db.Downloads.Local.Where(d => d.AppId == app.Id).ToList();
        var primary = rows.Single(d => d.IsPrimary);
        Assert.Equal(SourceKind.GitHub, primary.Source);
        Assert.Equal("980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9", primary.SigSha256);
        Assert.Equal("c7b19fa46b32caa0fc9a49b6c8789253", primary.SigMd5);
        var variant = rows.Single(d => d.Source == SourceKind.FDroid);
        Assert.Equal("https://f-droid.org/repo/com.example.app_42.apk", variant.ApkUrl);
        Assert.Equal(42L, variant.VersionCode);
        Assert.Equal("1.2.3", variant.VersionName); // file truth wins (canned badging, index says 4.2)
        Assert.Equal((long)variantZip.Length, variant.SizeBytes);
        Assert.Equal(Sha256(variantZip), variant.Sha256); // bytes win over the index hash
        Assert.Equal("1111111111111111111111111111111111111111111111111111111111111111", variant.SigSha256);
        Assert.Equal("b10a8db164e0754105b7a99be72e3fe5", variant.SigMd5); // file truth wins over index <sig>
    }

    [Fact]
    public async Task MissingSignerKeepsEnrichmentWithNullSigs()
    {
        var primaryZip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Blue)));
        // Default signer throws ApkSignerException (no apksigner/Java on the box).
        var (enricher, _) = ForgeWithVariant(primaryZip, new byte[] { 9 });
        var app = NewApp("nosig", "NoSig", "https://github.com/example/nosig");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var rows = _db.Downloads.Local.Where(d => d.AppId == app.Id).ToList();
        var primary = rows.Single(d => d.IsPrimary);
        Assert.Null(primary.SigSha256);
        Assert.Null(primary.SigMd5);
        var variant = rows.Single(d => d.Source == SourceKind.FDroid);
        Assert.Null(variant.SigSha256);
        Assert.Equal("b10a8db164e0754105b7a99be72e3fe5", variant.SigMd5); // index <sig> still recorded
        Assert.Equal("https://f-droid.org/repo/com.example.app_42.apk", variant.ApkUrl);
    }

    [Fact]
    public async Task SkipsVariantDownloadWhenAlreadyRecorded()
    {
        var primaryZip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Blue)));
        var (enricher, downloads) = ForgeWithVariant(primaryZip, new byte[] { 9 });
        var app = NewApp("cached-var", "CachedVar", "https://github.com/example/cached");
        AddDownload(app, SourceKind.FDroid,
            "https://f-droid.org/repo/com.example.app_42.apk", versionCode: 42, sha256: "unchanged", primary: false);

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(1, downloads.Calls); // primary only
        var variant = _db.Downloads.Local.Single(d => d.AppId == app.Id && d.Source == SourceKind.FDroid);
        Assert.Equal("unchanged", variant.Sha256);
        Assert.Equal("b10a8db164e0754105b7a99be72e3fe5", variant.SigMd5); // refreshed from the index
    }

    [Fact]
    public async Task ClearsVariantWhenDroppedFromIndex()
    {
        var primaryZip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Blue)));
        var (enricher, _) = ForgeWithVariant(primaryZip, new byte[] { 9 }, indexXml: EmptyIndexXml);
        var app = NewApp("dropped-var", "DroppedVar", "https://github.com/example/dropped");
        AddDownload(app, SourceKind.FDroid,
            "https://f-droid.org/repo/com.example.app_42.apk",
            versionCode: 42, sha256: "old", sigMd5: "old", primary: false);

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        await _db.SaveChangesAsync();
        Assert.DoesNotContain(_db.Downloads.Local, d => d.AppId == app.Id && d.Source == SourceKind.FDroid);
    }

    [Fact]
    public async Task RecordsVariantIndexOnlyWhenDownloadFails()
    {
        var primaryZip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Blue)));
        var (enricher, _) = ForgeWithVariant(primaryZip, [], variantDownload404: true);
        var app = NewApp("idxonly", "IdxOnly", "https://github.com/example/idxonly");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var variant = _db.Downloads.Local.Single(d => d.AppId == app.Id && d.Source == SourceKind.FDroid);
        Assert.Equal("https://f-droid.org/repo/com.example.app_42.apk", variant.ApkUrl);
        Assert.Equal(42L, variant.VersionCode);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", variant.Sha256);
        Assert.Null(variant.SigSha256);
        Assert.Equal("b10a8db164e0754105b7a99be72e3fe5", variant.SigMd5);
    }

    [Fact]
    public async Task FdroidPrimaryAnalyzesApkOnVersionChange()
    {
        var orangeIcon = TestAssets.SolidPng(256, 256, Color.Orange);
        var zip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, orangeIcon));
        var fdroid = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(VariantIndexXml),
        });
        var iconBytes = TestAssets.SolidPng(256, 256, Color.Purple);
        var downloads = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(
                request.RequestUri!.ToString().EndsWith(".apk") ? zip : iconBytes),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var signer = new FakeSignerRunner(_ => SignerOutputB);
        var enricher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("must not call GitHub")),
            downloads, aapt2, signer: signer, fdroid: fdroid);
        var app = NewApp("fdanalyzed", "FdAnalyzed", "https://f-droid.org/packages/com.example.app");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(1, downloads.Calls); // APK only; its icon wins, no mirror traffic
        var primary = Primary(app);
        Assert.Equal(SourceKind.FDroid, primary.Source);
        Assert.Equal(Sha256(zip), primary.Sha256); // bytes win over the index hash
        Assert.Equal("1111111111111111111111111111111111111111111111111111111111111111", primary.SigSha256);
        Assert.Equal("b10a8db164e0754105b7a99be72e3fe5", primary.SigMd5); // signer MD5 == index <sig>
        Assert.Equal(IconProcessor.ProcessRawImage(orangeIcon)!.Sha256, app.IconHash);
    }

    [Fact]
    public async Task FdroidPrimaryRejectsPackageMismatch()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Blue)));
        var fdroid = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(VariantIndexXml),
        });
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(package: "com.other.app"));
        var enricher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("must not call GitHub")),
            downloads, aapt2, fdroid: fdroid);
        var app = NewApp("mismatch", "Mismatch", "https://f-droid.org/packages/com.example.app");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Contains("expected com.example.app", app.LastError);
        Assert.False(HasDownloads(app));
    }

    [Fact]
    public async Task FallsBackToAvatarWhenRepoIconMissing()
    {
        var (enricher, _, downloads) = FdroidHappyPath(icon404: true);
        var app = NewApp("noicon", "No Icon", "https://f-droid.org/packages/com.example.app");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(3, downloads.Calls); // APK attempt + icons-640 + legacy icons/
        Assert.Equal(LetterAvatarGenerator.Generate("No Icon").Sha256, app.IconHash);
        Assert.Equal(Availability.DirectApk, app.Availability);
    }

    [Fact]
    public async Task SkipApkAnalysisRecordsReleaseMetadataWithoutDownload()
    {
        var (enricher, _, downloads, aapt2, _) = HappyPath(etag: null, changelog: "Release notes body");
        _options.SkipApkAnalysis = true;
        var app = NewApp("skipgh", "SkipGh", "https://github.com/example/skipgh");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Equal("Release notes body", app.Changelog);
        Assert.Equal(T0, app.LastCheckedAt);
        Assert.Null(app.LastError);
        Assert.DoesNotContain(downloads.Uris, u => u.EndsWith(".apk", StringComparison.Ordinal));
        Assert.Equal(0, aapt2.Calls);
        Assert.False(HasDownloads(app));
    }

    [Fact]
    public async Task SkipApkAnalysisKeepsRecordedDownloads()
    {
        var (enricher, _, downloads, aapt2, _) = HappyPath(etag: null);
        var app = NewApp("skipkeep", "SkipKeep", "https://github.com/example/skipkeep");
        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();
        var before = Primary(app);
        var versionBefore = before.VersionCode;
        var urlBefore = before.ApkUrl;
        var shaBefore = before.Sha256;
        var downloadCalls = downloads.Calls;
        var aapt2Calls = aapt2.Calls;

        _options.SkipApkAnalysis = true;
        Age(app);
        var result = await enricher.EnrichAsync(app, T0 + TimeSpan.FromDays(3));

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Equal(downloadCalls, downloads.Calls);
        Assert.Equal(aapt2Calls, aapt2.Calls);
        var after = Primary(app);
        Assert.Equal(urlBefore, after.ApkUrl);
        Assert.Equal(versionBefore, after.VersionCode);
        Assert.Equal(shaBefore, after.Sha256);
    }

    [Fact]
    public async Task SkipApkAnalysisUsesFDroidIndexMetadataAndScreenshots()
    {
        const string indexV2 = """
            {
              "packages": {
                "com.example.app": {
                  "metadata": {
                    "screenshots": {
                      "phone": {
                        "en-US": [
                          { "name": "/com.example.app/en-US/phoneScreenshots/00.png" }
                        ]
                      }
                    }
                  }
                }
              }
            }
            """;
        var (enricher, _, downloads) = FdroidHappyPath(indexV2Json: indexV2);
        _options.SkipApkAnalysis = true;
        var app = NewApp("skipfd", "SkipFd", "https://f-droid.org/packages/com.example.app/");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        // The APK probe is gone: only the index icon mirror remains.
        Assert.Equal(1, downloads.Calls);
        Assert.DoesNotContain(downloads.Uris, u => u.EndsWith(".apk", StringComparison.Ordinal));
        Assert.Equal("A <b>plain</b> summary of the app.", app.Changelog);
        Assert.Equal(
            ["https://f-droid.org/repo/com.example.app/en-US/phoneScreenshots/00.png"],
            app.Screenshots);
        Assert.Equal(20L, Primary(app).VersionCode);
    }

    private AppEnricher NoNetworkEnricher()
    {
        static HttpClient Dead() => new(new StubHandler(_ => throw new InvalidOperationException("no network")));
        return new AppEnricher(
            new GitHubReleaseClient(Dead()),
            new GitLabReleaseClient(Dead()),
            new FdroidIndexProvider(new FdroidRepoClient(Dead())),
            new FakeAapt2Runner(_ => throw new InvalidOperationException("no network")),
            new FakeSignerRunner(_ => throw new ApkSignerException("no network")),
            new LauncherIconService(),
            Dead(),
            _options,
            _db);
    }

    [Fact]
    public async Task ExcludesPlaySoleSource()
    {
        var enricher = NoNetworkEnricher();
        var app = NewApp("tasker", "Tasker", "https://play.google.com/store/apps/details?id=net.dinglisch.android.taskerm");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Excluded, result.Outcome);
        Assert.Equal(Availability.Excluded, app.Availability);
        Assert.Equal(SourceKind.Play, app.SourceKind);
        Assert.Contains("Play", app.ExcludedReason);
        Assert.Null(app.LastError);
        Assert.False(HasDownloads(app));
    }

    [Fact]
    public async Task OverrideKeepsPlayRedirect()
    {
        var enricher = NoNetworkEnricher();
        var play = "https://play.google.com/store/apps/details?id=net.dinglisch.android.taskerm";
        var app = NewApp("tasker", "Tasker", play, excludeOverride: true);

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.AvatarFallback, result.Outcome);
        Assert.Equal(Availability.PlayRedirect, app.Availability);
        Assert.Equal(play, app.StoreUrl);
        Assert.Null(app.ExcludedReason);
    }

    [Fact]
    public async Task PlayWithAltSourceIsRedirect()
    {
        var enricher = NoNetworkEnricher();
        var play = "https://play.google.com/store/apps/details?id=com.x";
        var app = NewApp("combo-cb", "Combo", play, "https://codeberg.org/some/app");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.AvatarFallback, result.Outcome);
        Assert.Equal(Availability.PlayRedirect, app.Availability);
        Assert.Equal(play, app.StoreUrl);
    }

    [Fact]
    public async Task RecordsTimeoutAsFailureInsteadOfGoingSilent()
    {
        // Live 2026-09-12: an upstream timeout escaped as an unrecorded
        // Failed (no last_error, no backoff), hiding a whole pass.
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v4.2", "app-release.apk", "https://cdn.example/app.apk", 123)));
        var downloads = new StubHandler(_ => throw new TaskCanceledException("simulated HttpClient timeout"));
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var app = NewApp("timeout", "Timeout", "https://github.com/example/timeout");

        var result = await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Contains("timed out", app.LastError);
        Assert.Equal(T0, app.LastCheckedAt);
    }

    [Fact]
    public async Task RecordsTransportFailureInsteadOfGoingSilent()
    {
        // Transport errors (DNS, TLS, reset) escape every inner catch the
        // same way timeouts did: record them with the cause attached.
        var github = new StubHandler(_ => throw new HttpRequestException("simulated DNS failure"));
        var downloads = new StubHandler(_ => throw new InvalidOperationException("must not download"));
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var app = NewApp("dnsfail", "DnsFail", "https://github.com/example/dnsfail");

        var result = await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Contains("Upstream error", app.LastError);
        Assert.Equal(T0, app.LastCheckedAt);
    }

    [Fact]
    public async Task RecordsStarsAndDeveloperWhenReleasesAreRateLimited()
    {
        // Live case (vFlow): the releases call hit GitHub API 403 while repo
        // metadata was fine, but stars/author were dropped with the failure.
        // Deep file URLs (README_EN.md) parse to owner/repo like any other.
        var github = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"message":"API rate limit exceeded"}"""),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"stargazers_count":1494,"owner":{"login":"ChaoMixian","html_url":"https://github.com/ChaoMixian"}}"""),
            };
        });
        var downloads = new StubHandler(_ => throw new InvalidOperationException("must not download"));
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var app = NewApp("ratelimited", "RateLimited",
            "https://github.com/ChaoMixian/vFlow/blob/master/README_EN.md");

        var result = await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Contains("403", app.LastError);
        Assert.Equal(1494, app.Stars);
        Assert.Equal("ChaoMixian", app.AuthorName);
        Assert.Equal("github:chaomixian", app.AuthorKey);
        Assert.Equal("https://github.com/ChaoMixian", app.AuthorUrl);
    }

    [Fact]
    public async Task PropagatesGenuineCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v4.2", "app-release.apk", "https://cdn.example/app.apk", 123)));
        var downloads = new StubHandler(_ => throw new TaskCanceledException("slow upstream"));
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("unreached"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BuildEnricher(github, downloads, aapt2).EnrichAsync(
                NewApp("cancel", "Cancel", "https://github.com/example/cancel"), T0, cts.Token));
    }

    private sealed class FakeLauncherIcons(Func<string, ProcessedIcon?> handler) : ILauncherIconService
    {
        public int Calls;
        public PendingBatchIcon? Pending;
        public Task<ProcessedIcon?> ResolveAsync(
            string apkPath, BadgingInfo badging, CancellationToken ct = default, bool allowXmlRender = true)
        {
            Calls++;
            return Task.FromResult(handler(apkPath));
        }

        public PendingBatchIcon? PrepareBatchRender(
            ZipArchive zip, byte[]? arsc, BadgingInfo badging, string batchWorkDir, string prefix) => Pending;
    }

    private static ProcessedIcon RedIcon()
    {
        var png = TestAssets.SolidPng(192, 192, Color.Red);
        return new ProcessedIcon(Sha256(png), png);
    }

    /// <summary>Enrich first (blue raster icon), then refresh with a red icon waiting.</summary>
    private async Task<(AppEnricher Refresher, FakeLauncherIcons Icons, string OldIconHash)> RefreshSetupAsync()
    {
        var (enricher, _, _, _, _) = HappyPath();
        var app = NewApp("refresh", "Refresh", "https://github.com/example/refresh");
        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        var oldIconHash = app.IconHash!;
        var icons = new FakeLauncherIcons(_ => RedIcon());

        var zip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var refresher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("no release check on refresh")),
            downloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging()),
            launcherIcons: icons);
        return (refresher, icons, oldIconHash);
    }

    private static string BatchDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"shizu-batchtest-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task PrepareWritesRasterImmediatelyWhenNoPending()
    {
        var (refresher, icons, oldIconHash) = await RefreshSetupAsync();
        var app = _db.Apps.Single(a => a.Slug == "refresh");
        var checkedAt = app.LastCheckedAt;
        var versionCode = Primary(app).VersionCode;
        var batchDir = BatchDir();
        try
        {
            // Fake stages nothing (Pending null): the full resolver can only
            // find the red raster, written like a normal refresh.
            var result = await refresher.PrepareIconRefreshAsync(app, batchDir, "b9");

            Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
            Assert.Null(result.Pending);
            Assert.Equal(1, icons.Calls);
            Assert.Equal(RedIcon().Sha256, app.IconHash);
            Assert.False(app.IconAdaptive); // density raster, not XML
            Assert.True(File.Exists(Path.Combine(_iconDir, $"{app.IconHash}.png")));
            Assert.False(File.Exists(Path.Combine(_iconDir, $"{oldIconHash}.png")));
            Assert.Equal(versionCode, Primary(app).VersionCode); // icon-only: values untouched
            Assert.Equal(checkedAt, app.LastCheckedAt);
            Assert.Null(app.LastError);
        }
        finally
        {
            try { Directory.Delete(batchDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task PrepareStagesXmlForBatchWithoutWriting()
    {
        var (refresher, icons, oldIconHash) = await RefreshSetupAsync();
        icons.Pending = new PendingBatchIcon("b9_0", null);
        var app = _db.Apps.Single(a => a.Slug == "refresh");
        var batchDir = BatchDir();
        try
        {
            var result = await refresher.PrepareIconRefreshAsync(app, batchDir, "b9");

            Assert.NotNull(result.Pending);
            Assert.Equal("b9_0", result.Pending.DrawableName);
            Assert.Equal(0, icons.Calls); // no resolve yet: render comes later
            Assert.Equal(oldIconHash, app.IconHash); // nothing written
        }
        finally
        {
            try { Directory.Delete(batchDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task PrepareKeepsCurrentIconWhenSame()
    {
        var (enricher, _, _, _, _) = HappyPath();
        var app = NewApp("sameicon", "SameIcon", "https://github.com/example/sameicon");
        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);

        var zip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        // Real service, null renderer: same blue raster resolves to the same icon.
        var refresher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("no release check on refresh")),
            downloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging()));
        var batchDir = BatchDir();
        try
        {
            var result = await refresher.PrepareIconRefreshAsync(app, batchDir, "b9");

            Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
            Assert.Null(result.Pending);
            Assert.True(File.Exists(Path.Combine(_iconDir, $"{app.IconHash}.png")));
        }
        finally
        {
            try { Directory.Delete(batchDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task PrepareSkipsAppsWithoutRecordedApk()
    {
        var downloads = new StubHandler(_ => throw new InvalidOperationException("must not download"));
        var app = NewApp("noapk", "NoApk", "https://example.com/noapk");
        var batchDir = BatchDir();
        try
        {
            var result = await BuildEnricher(
                new StubHandler(_ => throw new InvalidOperationException("no network")),
                downloads,
                new FakeAapt2Runner(_ => throw new InvalidOperationException("no aapt2")))
                .PrepareIconRefreshAsync(app, batchDir, "b9");

            Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
            Assert.Null(result.Pending);
            Assert.Equal(0, downloads.Calls);
            Assert.Null(app.IconHash);
        }
        finally
        {
            try { Directory.Delete(batchDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task PrepareFailsCleanlyWhenApkGone()
    {
        var (enricher, _, _, _, _) = HappyPath();
        var app = NewApp("gone", "Gone", "https://github.com/example/gone");
        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        var iconHash = app.IconHash;

        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var refresher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("no release check on refresh")),
            downloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging()),
            launcherIcons: new FakeLauncherIcons(_ => RedIcon()));
        var batchDir = BatchDir();
        try
        {
            var result = await refresher.PrepareIconRefreshAsync(app, batchDir, "b9");

            Assert.Equal(EnrichOutcome.Failed, result.Outcome);
            Assert.Null(result.Pending);
            Assert.Equal(iconHash, app.IconHash); // row untouched: schedule + values kept
            Assert.Null(app.LastError);
            Assert.True(File.Exists(Path.Combine(_iconDir, $"{iconHash}.png")));
        }
        finally
        {
            try { Directory.Delete(batchDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task CommitAdoptsBatchRender()
    {
        var (refresher, _, oldIconHash) = await RefreshSetupAsync();
        var app = _db.Apps.Single(a => a.Slug == "refresh");
        var checkedAt = app.LastCheckedAt;
        var versionCode = Primary(app).VersionCode;

        var result = await refresher.CommitIconRefreshAsync(app, RedIcon().Png, isAdaptive: true);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(RedIcon().Sha256, app.IconHash);
        Assert.True(app.IconAdaptive); // caller-flagged adaptive root
        Assert.True(File.Exists(Path.Combine(_iconDir, $"{app.IconHash}.png")));
        Assert.False(File.Exists(Path.Combine(_iconDir, $"{oldIconHash}.png")));
        Assert.Equal(versionCode, Primary(app).VersionCode);
        Assert.Equal(checkedAt, app.LastCheckedAt);
    }

    [Fact]
    public async Task CommitKeepsCurrentWhenSameRender()
    {
        var (refresher, _, _) = await RefreshSetupAsync();
        var app = _db.Apps.Single(a => a.Slug == "refresh");

        // Stored icon is the blue raster normalized to 192px; the same
        // bytes back must not rewrite anything.
        var result = await refresher.CommitIconRefreshAsync(
            app, TestAssets.SolidPng(192, 192, Color.Blue));

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task CommitCountsEqualBytesAsRefreshedWhenForced()
    {
        var (refresher, _, _) = await RefreshSetupAsync();
        var app = _db.Apps.Single(a => a.Slug == "refresh");
        var iconHash = app.IconHash;

        // Same bytes back, but forced: the rewrite must count as
        // refreshed (swap detection compares fresh bytes for every app).
        var result = await refresher.CommitIconRefreshAsync(
            app, TestAssets.SolidPng(192, 192, Color.Blue), force: true);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(iconHash, app.IconHash);
    }

    [Fact]
    public async Task CommitFailsCleanlyOnUnusableRender()
    {
        var (refresher, _, _) = await RefreshSetupAsync();
        var app = _db.Apps.Single(a => a.Slug == "refresh");
        var iconHash = app.IconHash;

        var result = await refresher.CommitIconRefreshAsync(app, [1, 2, 3]);

        Assert.Equal(EnrichOutcome.Failed, result.Outcome);
        Assert.Equal(iconHash, app.IconHash);
        Assert.True(File.Exists(Path.Combine(_iconDir, $"{iconHash}.png")));
    }

    [Fact]
    public async Task CommitHealsMissingFileWhenSameRender()
    {
        var (refresher, _, _) = await RefreshSetupAsync();
        var app = _db.Apps.Single(a => a.Slug == "refresh");
        var iconHash = app.IconHash!;
        File.Delete(Path.Combine(_iconDir, $"{iconHash}.png"));

        // Same bytes back: the row is already correct, but the missing file
        // must be restored (reported as Enriched so the tally shows it).
        var result = await refresher.CommitIconRefreshAsync(
            app, TestAssets.SolidPng(192, 192, Color.Blue));

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(iconHash, app.IconHash);
        Assert.True(File.Exists(Path.Combine(_iconDir, $"{iconHash}.png")));
    }

    [Fact]
    public async Task PrepareHealsMissingRasterFile()
    {
        var (enricher, _, _, _, _) = HappyPath();
        var app = NewApp("healicon", "HealIcon", "https://github.com/example/healicon");
        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        var iconHash = app.IconHash!;
        File.Delete(Path.Combine(_iconDir, $"{iconHash}.png"));

        var zip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        // Real service, null renderer: same blue raster resolves to the same icon.
        var refresher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("no release check on refresh")),
            downloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging()));
        var batchDir = BatchDir();
        try
        {
            var result = await refresher.PrepareIconRefreshAsync(app, batchDir, "b9");

            // Same bytes, missing file: restored, reported as Enriched so
            // the tally proves the healing (same rule as the commit path).
            Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
            Assert.Equal(iconHash, app.IconHash);
            Assert.True(File.Exists(Path.Combine(_iconDir, $"{iconHash}.png")));
        }
        finally
        {
            try { Directory.Delete(batchDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task PrepareFallsBackToAvatarWhenApkHasNoIcon()
    {
        // APK declares no icon at all (the fpsviewer class): the resolver
        // finds nothing, so refresh must record the same avatar enrich
        // would instead of reporting UpToDate over a missing file.
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(TestAssets.BuildApk()),
        });
        var app = NewApp("noicon", "NoIcon", "https://github.com/example/noicon");
        app.Availability = Availability.DirectApk;
        AddDownload(app, SourceKind.GitHub, "https://cdn.example/noicon.apk", versionCode: 1);
        var refresher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("no release check on refresh")),
            downloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging()),
            launcherIcons: new FakeLauncherIcons(_ => null));
        var batchDir = BatchDir();
        try
        {
            var result = await refresher.PrepareIconRefreshAsync(app, batchDir, "b9");

            var avatar = LetterAvatarGenerator.Generate("NoIcon");
            Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
            Assert.Null(result.Pending);
            Assert.Equal(avatar.Sha256, app.IconHash);
            Assert.False(app.IconAdaptive);
            Assert.True(File.Exists(Path.Combine(_iconDir, $"{app.IconHash}.png")));
        }
        finally
        {
            try { Directory.Delete(batchDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task PrepareHealsAvatarFileForLinkOnly()
    {
        // Store-only listings carry avatars; a deleted avatar file must be
        // rewritten without any network traffic.
        var app = NewApp("storeonly", "StoreOnly", "https://example.com/storeonly");
        app.Availability = Availability.LinkOnly;
        app.IconHash = LetterAvatarGenerator.Generate("StoreOnly").Sha256;
        var refresher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("no network")),
            new StubHandler(_ => throw new InvalidOperationException("must not download")),
            new FakeAapt2Runner(_ => throw new InvalidOperationException("no aapt2")));
        var batchDir = BatchDir();
        try
        {
            var result = await refresher.PrepareIconRefreshAsync(app, batchDir, "b9");

            Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
            Assert.False(app.IconAdaptive);
            Assert.True(File.Exists(Path.Combine(_iconDir, $"{app.IconHash}.png")));
        }
        finally
        {
            try { Directory.Delete(batchDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task PrepareLeavesForeignLinkOnlyIconAlone()
    {
        // A store-only icon that is NOT the deterministic avatar has
        // unknown provenance: never touch it, even when the file is gone.
        var app = NewApp("foreign", "Foreign", "https://example.com/foreign");
        app.Availability = Availability.LinkOnly;
        app.IconHash = new string('a', 64);
        var refresher = BuildEnricher(
            new StubHandler(_ => throw new InvalidOperationException("no network")),
            new StubHandler(_ => throw new InvalidOperationException("must not download")),
            new FakeAapt2Runner(_ => throw new InvalidOperationException("no aapt2")));
        var batchDir = BatchDir();
        try
        {
            var result = await refresher.PrepareIconRefreshAsync(app, batchDir, "b9");

            Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
            Assert.Equal(new string('a', 64), app.IconHash);
            Assert.False(File.Exists(Path.Combine(_iconDir, $"{app.IconHash}.png")));
        }
        finally
        {
            try { Directory.Delete(batchDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task SinglePackageReleaseUsesApkLabelAsDisplayName()
    {
        var (enricher, _, _, _, _) = HappyPath();
        var app = NewApp("micup", "MicUp", "https://github.com/papergray/MicUp");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal("Example", app.ApkLabel);
        Assert.Equal("Example", app.DisplayName);
        Assert.DoesNotContain(_db.Apps.Local, a => a.RootAppId == app.Id);
    }

    [Fact]
    public async Task MultiPackageReleaseCreatesVariantAppRows()
    {
        var mainApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var extraApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("app-release.apk", "https://cdn.example/app.apk", mainApk.Length),
            ("plugin-release.apk", "https://cdn.example/plugin.apk", extraApk.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("plugin", StringComparison.Ordinal)
                ? extraApk
                : mainApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == extraApk.Length
            ? TestAssets.CannedBadging(package: "com.example.plugin", label: "Plugin")
            : TestAssets.CannedBadging(package: "com.example.app", label: "Example"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("smart-toolbox", "Smart Toolbox", "https://github.com/example/smart-toolbox");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);

        var rows = _db.Apps.Local.Where(a => a.Id == app.Id || a.RootAppId == app.Id).ToList();
        Assert.Equal(2, rows.Count);
        var main = rows.Single(a => a.PackageName == "com.example.app");
        var plugin = rows.Single(a => a.PackageName == "com.example.plugin");
        Assert.Equal(Availability.DirectApk, plugin.Availability);
        Assert.Equal(Availability.DirectApk, main.Availability);

        // The list row keeps one of the packages; the other becomes a variant.
        var variant = Assert.Single(rows, a => a.RootAppId == app.Id);
        Assert.NotEqual(app.Id, variant.Id);
        var root = rows.Single(a => a.Id == app.Id);
        Assert.Equal("com-example-" + variant.PackageName!.Split('.')[^1], variant.Slug);

        // More than one package is published, so each row carries the list name.
        Assert.Equal($"{root.ApkLabel} (Smart Toolbox)", app.DisplayName);
        Assert.Equal($"{variant.ApkLabel} (Smart Toolbox)", variant.DisplayName);

        Assert.Equal("https://cdn.example/app.apk", Primary(main).ApkUrl);
        Assert.Equal("https://cdn.example/plugin.apk", Primary(plugin).ApkUrl);
    }

    [Fact]
    public async Task RemovesVariantWhenPackageDisappearsOnNextPass()
    {
        var mainApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var extraApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var json = ReleaseJsonMultiAssets(
            // Declared sizes force the main app to win the primary pick so the
            // root row owns com.example.app and the plugin becomes a variant.
            ("app-release.apk", "https://cdn.example/app.apk", mainApk.Length + 1_000_000),
            ("plugin-release.apk", "https://cdn.example/plugin.apk", extraApk.Length));
        var github = new StubHandler(_ => JsonReleases(json, "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("plugin", StringComparison.Ordinal)
                ? extraApk
                : mainApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == extraApk.Length
            ? TestAssets.CannedBadging(package: "com.example.plugin", label: "Plugin")
            : TestAssets.CannedBadging(package: "com.example.app", label: "Example"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("smart-toolbox", "Smart Toolbox", "https://github.com/example/smart-toolbox");

        await enricher.EnrichAsync(app, T0);
        Assert.Single(_db.Apps.Local, a => a.RootAppId == app.Id);
        // Persist the first pass like the runner does, so removal has real keys.
        await _db.SaveChangesAsync();

        // The next release drops the second app; its variant and downloads go away.
        Age(app);
        json = ReleaseJson("v1.1", "app-release.apk", "https://cdn.example/app.apk", mainApk.Length);
        var result = await enricher.EnrichAsync(app, T0);

        // The kept APK is byte-identical so its analysis is skipped, but the
        // vanished package still needs pruning and the display name re-settled.
        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Equal("Example", app.DisplayName);
        await _db.SaveChangesAsync();
        Assert.Empty(await _db.Apps.Where(a => a.RootAppId == app.Id).ToListAsync());
        Assert.DoesNotContain(_db.Downloads.Local, d => d.ApkUrl.EndsWith("plugin.apk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HealsRootLabelWhenPrimaryIsNotInTheLatestRelease()
    {
        var oldApk = TestAssets.BuildApk((TestAssets.MdpiIcon, TestAssets.SolidPng(400, 400, Color.Red)));
        var pluginApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v2.0", "plugin-release.apk", "https://cdn.example/plugin.apk", pluginApk.Length),
            "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("old", StringComparison.Ordinal)
                ? oldApk
                : pluginApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == oldApk.Length
            ? TestAssets.CannedBadging(package: "com.example.root", versionCode: "5", label: "Root App")
            : TestAssets.CannedBadging(package: "com.example.plugin", label: "Plugin"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("droidos", "DroidOS", "https://github.com/example/droidos");
        app.PackageName = "com.example.root";
        _db.SaveChanges();
        AddDownload(app, SourceKind.GitHub, "https://cdn.example/old.apk", versionCode: 5, sigSha256: SignerOutputA);

        var result = await enricher.EnrichAsync(app, T0);

        // The release only carries the plugin, so the root's own package was
        // never a representative asset; the heal analyzes the recorded primary.
        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal("com.example.root", app.PackageName);
        Assert.Equal("Root App", app.ApkLabel);
        Assert.Contains("android.permission.INTERNET", app.Permissions);
        Assert.Equal("Root App (DroidOS)", app.DisplayName);
        Assert.Single(_db.Apps.Local, a => a.RootAppId == app.Id && a.PackageName == "com.example.plugin");
    }

    // ---- Checksum-gated enrichment: skip the expensive APK work when unchanged ----

    [Fact]
    public async Task MatchingSourceDigestSkipsDownloadAndAnalysis()
    {
        var zip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var digest = "sha256:" + Sha256(zip);
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app-release.apk", "https://cdn.example/app.apk", zip.Length, digest),
            "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("micup", "MicUp", "https://github.com/papergray/MicUp");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();
        Assert.Equal(1, downloads.Calls);
        Assert.Equal(1, aapt2.Calls);

        Age(app);
        var second = await enricher.EnrichAsync(app, T0);
        Assert.Equal(EnrichOutcome.UpToDate, second.Outcome);
        Assert.Equal("asset URL unchanged", second.Detail);

        // The recorded asset URL is unchanged and the release digest matches
        // the recorded hash, so the transfer itself is skipped along with
        // aapt2/signer/icon.
        Assert.Equal(1, downloads.Calls);
        Assert.Equal(1, aapt2.Calls);
        Assert.Equal(T0, app.LastCheckedAt);
    }

    [Fact]
    public async Task MovedAssetUrlDownloadsAgainButSkipsAnalysis()
    {
        // No source digest and a moved URL (tag re-upload): the file has to be
        // fetched, but its hash proves the derived data is unchanged.
        var zip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var url = "https://cdn.example/app.apk";
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app-release.apk", url, zip.Length),
            "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("micup", "MicUp", "https://github.com/papergray/MicUp");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();

        Age(app);
        url = "https://cdn.example/app-v1.1.apk";
        var second = await enricher.EnrichAsync(app, T0);
        Assert.Equal(EnrichOutcome.UpToDate, second.Outcome);
        Assert.Equal("APK unchanged, analysis skipped", second.Detail);
        Assert.Equal(2, downloads.Calls);
        Assert.Equal(1, aapt2.Calls);
    }

    [Fact]
    public async Task ChangedSourceDigestReanalyzes()
    {
        var firstZip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var secondZip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Red)));
        var payload = firstZip;
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson(
                "v1.0", "app-release.apk", "https://cdn.example/app.apk", payload.Length,
                "sha256:" + Sha256(payload)),
            "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("micup", "MicUp", "https://github.com/papergray/MicUp");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();

        // Same URL, new bytes: the declared digest moved, so the file is worked on.
        Age(app);
        payload = secondZip;
        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(2, downloads.Calls);
        Assert.Equal(2, aapt2.Calls);
        Assert.Equal(Sha256(secondZip), Primary(app).Sha256);
    }

    [Fact]
    public async Task MatchingDigestButMissingIconFileStillAnalyzes()
    {
        var zip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var digest = "sha256:" + Sha256(zip);
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app-release.apk", "https://cdn.example/app.apk", zip.Length, digest),
            "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("micup", "MicUp", "https://github.com/papergray/MicUp");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();
        Assert.Equal(1, aapt2.Calls);

        // A vanished icon file must be rebuilt even though the bytes are the same.
        var iconPath = Path.Combine(_iconDir, $"{app.IconHash}.png");
        File.Delete(iconPath);
        Age(app);
        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(2, aapt2.Calls);
        Assert.True(File.Exists(iconPath));
    }

    [Fact]
    public async Task NewerReleaseWithIdenticalBytesStillAnalyzes()
    {
        var zip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var digest = "sha256:" + Sha256(zip);
        var publishedAt = "2024-06-01T00:00:00Z";
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app-release.apk", "https://cdn.example/app.apk", zip.Length, digest, publishedAt),
            "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("micup", "MicUp", "https://github.com/papergray/MicUp");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();

        // A newer release date means a version bump is possible even when the
        // artifact bytes are identical, so the checksum must not short-circuit.
        Age(app);
        publishedAt = "2024-07-01T00:00:00Z";
        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(2, aapt2.Calls);
        Assert.Equal(DateTimeOffset.Parse("2024-07-01T00:00:00Z"), app.VersionUpdatedAt);
    }

    [Fact]
    public async Task SmartspacerScansAllReleasesAndGroupsPackages()
    {
        var glanceApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(400, 400, Color.Blue)));
        var weatherApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(640, 640, Color.Red)));
        var github = new StubHandler(_ => JsonReleases(ReleasesJson(
            ("glance-v1", "at-a-glance-release.apk", "https://cdn.example/glance.apk", glanceApk.Length),
            ("weather-v2", "weather-release.apk", "https://cdn.example/weather.apk", weatherApk.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("weather", StringComparison.Ordinal)
                ? weatherApk
                : glanceApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == weatherApk.Length
            ? TestAssets.CannedBadging(package: "com.kieronquinn.plugin.weather", label: "Weather")
            : TestAssets.CannedBadging(package: "com.kieronquinn.plugin.glance", label: "At a Glance"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("smartspacer-plugins", "SmartspacerPlugins",
            "https://github.com/KieronQuinn/SmartspacerPlugins");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);

        // Each plugin lives in its own release, so scanning only the newest
        // release would have missed all but one of them.
        var rows = _db.Apps.Local.Where(a => a.Id == app.Id || a.RootAppId == app.Id).ToList();
        Assert.Equal(
            ["com.kieronquinn.plugin.glance", "com.kieronquinn.plugin.weather"],
            rows.Select(a => a.PackageName!).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.All(rows, row => Assert.Equal($"{row.ApkLabel} (SmartspacerPlugins)", row.DisplayName));
        Assert.Equal("https://cdn.example/glance.apk", Primary(rows.Single(a => a.ApkLabel == "At a Glance")).ApkUrl);
        Assert.Equal("https://cdn.example/weather.apk", Primary(rows.Single(a => a.ApkLabel == "Weather")).ApkUrl);
    }

    [Fact]
    public async Task MultiPackageAnalysisFailureKeepsExistingVariants()
    {
        var mainApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var extraApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var json = ReleaseJsonMultiAssets(
            ("app-release.apk", "https://cdn.example/app.apk", mainApk.Length),
            ("plugin-release.apk", "https://cdn.example/plugin.apk", extraApk.Length));
        var github = new StubHandler(_ => JsonReleases(json, "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("broken", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var bytes = path.Contains("plugin", StringComparison.Ordinal) ? extraApk : mainApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == extraApk.Length
            ? TestAssets.CannedBadging(package: "com.example.plugin", label: "Plugin")
            : TestAssets.CannedBadging(package: "com.example.app", label: "Example"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("smart-toolbox", "Smart Toolbox", "https://github.com/example/smart-toolbox");

        await enricher.EnrichAsync(app, T0);

        // A later scan still lists both assets but only the first downloads;
        // the failed sibling must not be treated as vanished.
        Age(app);
        json = ReleaseJsonMultiAssets(
            ("app-release.apk", "https://cdn.example/app.apk", mainApk.Length),
            ("plugin-release.apk", "https://cdn.example/broken-plugin.apk", extraApk.Length));
        await enricher.EnrichAsync(app, T0);

        Assert.Single(_db.Apps.Local, a => a.RootAppId == app.Id);
    }

    // ---- Flavor grouping: same-label APKs of one release become one app with per-package candidates ----

    [Fact]
    public async Task SameLabelFlavorsCollapseIntoOneRowWithSeedPackageAsCanonical()
    {
        var baseApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var playApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("app-release.apk", "https://cdn.example/app.apk", baseApk.Length),
            ("app-play-release.apk", "https://cdn.example/app-play.apk", playApk.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("play", StringComparison.Ordinal)
                ? playApk
                : baseApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == playApk.Length
            ? TestAssets.CannedBadging(package: "com.example.app.play", label: "Example")
            : TestAssets.CannedBadging(package: "com.example.app", label: "Example"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("example-app", "Example", "https://github.com/example/example-app");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);

        // One row for both flavors; no variant rows.
        Assert.DoesNotContain(_db.Apps.Local, a => a.RootAppId == app.Id);
        Assert.Equal("com.example.app", app.PackageName);

        var candidates = _db.Downloads.Local.Where(d => d.AppId == app.Id).ToList();
        Assert.Equal(2, candidates.Count);
        Assert.Equal(
            ["com.example.app", "com.example.app.play"],
            candidates.Select(d => d.PackageName!).OrderBy(p => p, StringComparer.Ordinal).ToArray());

        // The base package owns the primary candidate.
        var primary = candidates.Single(d => d.IsPrimary);
        Assert.Equal("com.example.app", primary.PackageName);
    }

    [Fact]
    public async Task SameLabelFlavorsPickLabelTokenWhenNoPackageIsABase()
    {
        var officialApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var spoofedApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("mmrl-release.apk", "https://cdn.example/mmrl.apk", officialApk.Length),
            ("mmrl-spoofed-release.apk", "https://cdn.example/mmrl-spoofed.apk", spoofedApk.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("spoofed", StringComparison.Ordinal)
                ? spoofedApk
                : officialApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == spoofedApk.Length
            ? TestAssets.CannedBadging(package: "fsgpgfw1k.v64xr36kia19.kt1f7i4z28pvl4v", label: "MMRL")
            : TestAssets.CannedBadging(package: "com.dergoogler.mmrl", label: "MMRL"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("mmrl", "MMRL", "https://github.com/DerGoogler/MMRL");

        await enricher.EnrichAsync(app, T0);

        Assert.DoesNotContain(_db.Apps.Local, a => a.RootAppId == app.Id);
        Assert.Equal("com.dergoogler.mmrl", app.PackageName);

        var candidates = _db.Downloads.Local.Where(d => d.AppId == app.Id).ToList();
        Assert.Equal(2, candidates.Count);
        Assert.Equal("com.dergoogler.mmrl", candidates.Single(d => d.IsPrimary).PackageName);
    }

    [Fact]
    public async Task ListEndpointPackageWinsOverBasePackageAsCanonical()
    {
        var baseApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var playApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("app-release.apk", "https://cdn.example/app.apk", baseApk.Length),
            ("app-play-release.apk", "https://cdn.example/app-play.apk", playApk.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("play", StringComparison.Ordinal)
                ? playApk
                : baseApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == playApk.Length
            ? TestAssets.CannedBadging(package: "com.example.app.play", label: "Example")
            : TestAssets.CannedBadging(package: "com.example.app", label: "Example"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("example-app", "Example", "https://github.com/example/example-app",
            sourceUrl: "https://play.google.com/store/apps/details?id=com.example.app.play");

        await enricher.EnrichAsync(app, T0);

        // The list links the Play flavor, so that package stays canonical even
        // though com.example.app is a dot-prefix base.
        Assert.Equal("com.example.app.play", app.PackageName);
        Assert.Equal("com.example.app.play", _db.Downloads.Local.Single(d => d.AppId == app.Id && d.IsPrimary).PackageName);
    }

    [Fact]
    public async Task SameLabelVariantRowIsFoldedIntoRootWithTombstone()
    {
        var baseApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var playApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("app-release.apk", "https://cdn.example/app.apk", baseApk.Length),
            ("app-play-release.apk", "https://cdn.example/app-play.apk", playApk.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("play", StringComparison.Ordinal)
                ? playApk
                : baseApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == playApk.Length
            ? TestAssets.CannedBadging(package: "com.example.app.play", label: "Example")
            : TestAssets.CannedBadging(package: "com.example.app", label: "Example"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("smart-toolbox", "Smart Toolbox", "https://github.com/example/smart-toolbox");
        app.ApkLabel = "Example";
        _db.SaveChanges();

        // A variant row created before flavor grouping existed: same label as the
        // root, so the next pass must fold it back instead of keeping a twin.
        var variant = new App
        {
            Slug = "com-example-app-play",
            Name = "Smart Toolbox",
            Url = "https://github.com/example/smart-toolbox",
            Listing = Listing.Main,
            Type = AppType.App,
            RootAppId = app.Id,
            CategoryId = app.CategoryId,
            ApkLabel = "Example",
            PackageName = "com.example.app.play",
            AddedAt = T0,
            UpdatedAt = T0,
        };
        _db.Apps.Add(variant);
        _db.SaveChanges();
        AddDownload(variant, SourceKind.GitHub, "https://cdn.example/legacy-play.apk",
            versionCode: 42, sigSha256: "980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9");

        await enricher.EnrichAsync(app, T0);
        await _db.SaveChangesAsync();

        Assert.Empty(await _db.Apps.Where(a => a.RootAppId == app.Id).ToListAsync());
        Assert.Contains(await _db.RemovedApps.ToListAsync(), r => r.Slug == "com-example-app-play");

        var candidates = await _db.Downloads.Where(d => d.AppId == app.Id).ToListAsync();
        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, d => d.PackageName == "com.example.app.play");
        Assert.Equal("com.example.app", candidates.Single(d => d.IsPrimary).PackageName);
    }
}

public sealed class BulkEnricherTests
{
    private static App Row(string slug) => new()
    {
        Slug = slug,
        Name = slug,
        Url = "https://github.com/o/r",
    };

    [Fact]
    public async Task PreservesInputOrder()
    {
        var apps = new[] { Row("a"), Row("b"), Row("c") };
        var delays = new Dictionary<string, int> { ["a"] = 60, ["b"] = 30, ["c"] = 0 };

        var results = await BulkEnricher.EnrichManyAsync(
            apps,
            async (app, ct) =>
            {
                await Task.Delay(delays[app.Slug], ct);
                return new EnrichResult(EnrichOutcome.Enriched, null);
            },
            maxParallelism: 4);

        Assert.Equal(["a", "b", "c"], results.Select(r => r.App.Slug));
        Assert.All(results, r => Assert.Equal(EnrichOutcome.Enriched, r.Result.Outcome));
    }

    [Fact]
    public async Task BoundsParallelism()
    {
        var apps = Enumerable.Range(0, 6).Select(i => Row($"app-{i}")).ToList();
        var current = 0;
        var maxObserved = 0;

        await BulkEnricher.EnrichManyAsync(
            apps,
            async (app, ct) =>
            {
                var now = Interlocked.Increment(ref current);
                Interlocked.Exchange(ref maxObserved, Math.Max(maxObserved, now));
                try
                {
                    await Task.Delay(30, ct);
                }
                finally
                {
                    Interlocked.Decrement(ref current);
                }

                return new EnrichResult(EnrichOutcome.Enriched, null);
            },
            maxParallelism: 2);

        Assert.True(maxObserved <= 2, $"max parallelism observed: {maxObserved}");
        Assert.True(maxObserved >= 2, $"expected real parallelism, observed: {maxObserved}");
    }

    [Fact]
    public async Task IsolatesPerAppFaults()
    {
        var apps = new[] { Row("ok"), Row("boom") };

        var results = await BulkEnricher.EnrichManyAsync(
            apps,
            (app, _) => app.Slug == "boom"
                ? throw new InvalidOperationException("kaboom")
                : Task.FromResult(new EnrichResult(EnrichOutcome.Enriched, null)),
            maxParallelism: 2);

        Assert.Equal(EnrichOutcome.Enriched, results[0].Result.Outcome);
        Assert.Equal(EnrichOutcome.Failed, results[1].Result.Outcome);
        Assert.Contains("kaboom", results[1].Result.Error);
    }

    [Fact]
    public async Task PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BulkEnricher.EnrichManyAsync([Row("a")],
                (_, _) => Task.FromResult(new EnrichResult(EnrichOutcome.Enriched, null)),
                maxParallelism: 2,
                cts.Token));
    }
}
