using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Enrichment.Apk;
using ShizuAppStoreServer.Core.Enrichment.Icons;
using ShizuAppStoreServer.Core.Enrichment.Repo;
using ShizuAppStoreServer.Core.Sources;
using ShizuAppStoreServer.Core.UsageAnalysis;
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

    private sealed class FakeRepoScreenshots(Func<IReadOnlyList<string>> handler) : IRepoScreenshotResolver
    {
        public int Calls;

        /// <summary>False models an unreachable repo (clone or tree listing failed).</summary>
        public bool Reached { get; set; } = true;

        public Task<RepoScreenshotResult> ResolveAsync(
            string? url, string? sourceUrl, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new RepoScreenshotResult(Reached, handler()));
        }
    }

    private sealed class FakeTrackerCatalog(params TrackerSignature[] signatures) : ITrackerCatalog
    {
        public int Calls;
        public Task<IReadOnlyList<TrackerSignature>> GetAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<TrackerSignature>>(signatures);
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
        string? digest = null, string publishedAt = "2024-06-01T00:00:00Z", string? body = null,
        bool prerelease = false)
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
            ["prerelease"] = prerelease,
            ["published_at"] = publishedAt,
            ["html_url"] = $"https://github.com/o/r/releases/tag/{tag}",
            ["assets"] = new JsonArray(asset),
        };
        if (body is not null)
        {
            release["body"] = body;
        }

        return new JsonArray(release).ToJsonString();
    }

    private static string ReleaseJsonMultiAssets(params (string Name, string Url, long Size)[] assets) =>
        ReleaseJsonWithAssets("v1.0", false, assets);

    private static string ReleaseJsonWithAssets(
        string tag, bool prerelease, params (string Name, string Url, long Size)[] assets)
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
            ["tag_name"] = tag,
            ["draft"] = false,
            ["prerelease"] = prerelease,
            ["published_at"] = "2024-06-01T00:00:00Z",
            ["assets"] = array,
        }).ToJsonString();
    }

    /// <summary>Newest stable release without assets above an older servable one.</summary>
    private static string AssetlessNewestFeed(string newestTag, string oldTag, string oldAssetUrl, long oldSize) =>
        new JsonArray(
            new JsonObject
            {
                ["tag_name"] = newestTag,
                ["draft"] = false,
                ["prerelease"] = false,
                ["published_at"] = "2024-06-01T00:00:00Z",
                ["assets"] = new JsonArray(),
            },
            new JsonObject
            {
                ["tag_name"] = oldTag,
                ["draft"] = false,
                ["prerelease"] = false,
                ["published_at"] = "2024-01-01T00:00:00Z",
                ["assets"] = new JsonArray(new JsonObject
                {
                    ["name"] = "app-release.apk",
                    ["browser_download_url"] = oldAssetUrl,
                    ["size"] = oldSize,
                    ["content_type"] = "application/vnd.android.package-archive",
                }),
            }).ToJsonString();

    private static HttpResponseMessage JsonReleases(string json, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    /// <summary>GitHub README API answer with inline base64 content.</summary>
    private static HttpResponseMessage ReadmeJson(string markdown, string path = "README.md") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["path"] = path,
                ["encoding"] = "base64",
                ["content"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(markdown)),
            }.ToJsonString()),
        };

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

    private sealed class FakeUsageQueue : IUsageAnalysisQueue
    {
        public List<(long AppId, bool ArtifactChanged, bool FirstAnalysis, string? ReleaseRef)> Calls { get; } = [];

        public Task<bool> EnqueueAsync(
            App app, bool artifactChanged, bool firstAnalysis, string? releaseRef = null, CancellationToken ct = default)
        {
            Calls.Add((app.Id, artifactChanged, firstAnalysis, releaseRef));
            return Task.FromResult(true);
        }

        public Task<bool> EnqueueForAnalyzedArtifactAsync(
            App app, bool artifactChanged, bool firstAnalysis, string? releaseRef = null, CancellationToken ct = default) =>
            EnqueueAsync(app, artifactChanged, firstAnalysis, releaseRef, ct);

        public Task<int> BackfillAsync(
            bool onlyMissing, bool includeStale, bool force, string? slug, int? limit, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    // Most tests only need an empty repo index so package and screenshot
    // lookups stay a fast no-op.
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
        IzzyStatsProvider? izzyStats = null,
        IRepoScreenshotResolver? repoScreenshots = null,
        ITrackerCatalog? trackers = null,
        IUsageAnalysisQueue? usageQueue = null) =>
        new(new GitHubReleaseClient(new HttpClient(github), "tok"),
            new GitLabReleaseClient(new HttpClient(gitlab ?? new StubHandler(_ =>
                throw new InvalidOperationException("must not call GitLab")))),
            new FdroidIndexProvider(new FdroidRepoClient(new HttpClient(fdroid ?? new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(EmptyIndexV2Json),
                })))),
            aapt2,
            signer ?? new FakeSignerRunner(_ => throw new ApkSignerException("must not run apksigner")),
            launcherIcons ?? new LauncherIconService(),
            new HttpClient(downloads), _options, _db, gitcode, play, izzyStats,
            repoScreenshots: repoScreenshots, trackers: trackers, usageQueue: usageQueue);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Canonical happy-path wiring: stable release → APK download → badging → icon.</summary>
    private (AppEnricher Enricher, StubHandler Github, StubHandler Downloads, FakeAapt2Runner Aapt2, byte[] Zip)
        HappyPath(string tag = "v1.0", string versionCode = "42", Color? iconColor = null, string? etag = "\"rel-etag\"", FakeSignerRunner? signer = null, string? changelog = null, string assetUrl = "https://cdn.example/app.apk", IRepoScreenshotResolver? repoScreenshots = null, IUsageAnalysisQueue? usageQueue = null)
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
        return (BuildEnricher(github, downloads, aapt2, signer: signer, repoScreenshots: repoScreenshots, usageQueue: usageQueue), github, downloads, aapt2, zip);
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
        bool primary = true,
        string? packageName = null)
    {
        var row = new AppDownload
        {
            AppId = app.Id,
            PackageName = packageName,
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
    public async Task VersionNameCommandOutputFallsBackToReleaseTag()
    {
        // DevBay-style build: the APK bakes `git describe` stderr into
        // versionName; the tag is the only usable label.
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v2.0.4", "app-release.apk", "https://cdn.example/app.apk", zip.Length), "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(
            versionCode: "1", versionName: "fatal: No names found, cannot describe anything."));
        var app = NewApp("devbay-launcher", "DevBay-Launcher", "https://github.com/Zoder-Studio/DevBay-Launcher");

        var result = await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal("v2.0.4", Primary(app).VersionName);
        Assert.Equal("v2.0.4", Assert.Single(app.Versions).VersionName);
    }

    [Fact]
    public async Task GitHubReleaseBodyBecomesChangelog()
    {
        var (enricher, _, _, _, _) = HappyPath(changelog: "## 1.0\n- First release");
        var app = NewApp("micup", "MicUp", "https://github.com/papergray/MicUp");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal("## 1.0\n- First release", app.Changelog);
        Assert.Equal("https://github.com/o/r/releases/tag/v1.0", app.ChangelogUrl);
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
    public async Task EnrichmentRecordsTargetSdkCompileSdkAndLocales()
    {
        var (enricher, _, _, _, _) = HappyPath();
        var app = NewApp("sdkapp", "SdkApp", "https://github.com/example/sdkapp");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var primary = Primary(app);
        Assert.Equal(34, primary.TargetSdk);
        Assert.Equal(34, primary.CompileSdk);
        Assert.Empty(primary.Locales);
    }

    [Fact]
    public async Task EnrichmentStoresDeclaredLocalesWithoutThePseudoLocale()
    {
        var apk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(200, 200, Color.Blue)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJson(
            "v1.0", "app-release.apk", "https://cdn.example/app-release.apk", apk.Length), "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(apk),
        });
        // The pseudo-locale is not a translation and must not reach the row.
        var aapt2 = new FakeAapt2Runner(_ =>
            TestAssets.CannedBadging() + "\nlocales: '--_--' 'de' 'en-US'\n");
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("localeapp", "LocaleApp", "https://github.com/example/localeapp");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var primary = Primary(app);
        Assert.Equal(34, primary.TargetSdk);
        Assert.Equal(["de", "en-US"], primary.Locales);
    }

    [Fact]
    public async Task EnrichmentRecordsDeclaredDhizukuPermission()
    {
        var zip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJson(
            "v1.0", "app-release.apk", "https://cdn.example/app.apk", zip.Length), "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ =>
            TestAssets.CannedBadging() + "\nuses-permission: name='com.rosan.dhizuku.permission.API'\n");
        var enricher = BuildEnricher(github, downloads, aapt2);
        var app = NewApp("dhizukuapp", "DhizukuApp", "https://github.com/example/dhizukuapp");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var primary = Primary(app);
        Assert.True(primary.DhizukuDeclared);
        Assert.False(primary.ShizukuDeclared);
        // No catalog is injected in tests by default, so no DEX scan runs.
        Assert.Empty(primary.Trackers);
    }

    [Fact]
    public async Task EnrichmentRecordsDeclaredShizukuPermission()
    {
        var zip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJson(
            "v1.0", "app-release.apk", "https://cdn.example/app.apk", zip.Length), "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ =>
            TestAssets.CannedBadging() + "\nuses-permission: name='moe.shizuku.manager.permission.API_V23'\n");
        var enricher = BuildEnricher(github, downloads, aapt2);
        var app = NewApp("shizukuapp", "ShizukuApp", "https://github.com/example/shizukuapp");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var primary = Primary(app);
        Assert.True(primary.ShizukuDeclared);
        Assert.False(primary.DhizukuDeclared);
    }

    [Fact]
    public async Task EnrichmentDetectsExodusTrackerCodeSignatures()
    {
        // A class descriptor from the AppLovin SDK; the scanner matches the
        // Exodus code signature as a substring of the decoded DEX text.
        var dex = "Lcom/applovin/impl/sdk/AppLovinSdk;"u8.ToArray();
        var zip = TestAssets.BuildApk(
            ("classes.dex", dex),
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJson(
            "v1.0", "app-release.apk", "https://cdn.example/app.apk", zip.Length), "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var trackers = new FakeTrackerCatalog(
            new TrackerSignature(1, "Google Analytics", "com.google.android.apps.analytics.", ["Analytics"]),
            new TrackerSignature(2, "AppLovin", "com.applovin.", ["Analytics", "Advertisement"]));
        var enricher = BuildEnricher(github, downloads, aapt2, trackers: trackers);
        var app = NewApp("trackerapp", "TrackerApp", "https://github.com/example/trackerapp");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var primary = Primary(app);
        Assert.Equal(["AppLovin"], primary.Trackers);
        Assert.Equal(["com.applovin."], primary.TrackerSignatures);
        // Each tag stays associated with its tracker, multiple tags included.
        Assert.Equal(["AppLovin:Analytics", "AppLovin:Advertisement"], primary.TrackerTags);
        Assert.False(primary.DhizukuDeclared);
        Assert.False(primary.ShizukuDeclared);
    }

    [Fact]
    public async Task TrackerTagsBackfilledFromCatalogWithoutDownload()
    {
        var trackers = new FakeTrackerCatalog(
            new TrackerSignature(2, "AppLovin", "com.applovin.", ["Analytics", "Advertisement"]));
        var github = new StubHandler(_ => throw new InvalidOperationException("must not fetch releases"));
        var downloads = new StubHandler(_ => throw new InvalidOperationException("must not download"));
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var enricher = BuildEnricher(github, downloads, aapt2, trackers: trackers);
        var app = NewApp("tagheal", "TagHeal", "https://github.com/example/tagheal");
        var primary = AddDownload(app, SourceKind.GitHub, "https://cdn.example/tagheal.apk", versionCode: 42);
        primary.Trackers = ["AppLovin"];
        primary.Inspected = true;
        await _db.SaveChangesAsync();
        app.LastCheckedAt = T0;

        var result = await enricher.EnrichAsync(app, T0);

        // Inside the recheck window, so nothing is fetched; the missing tags
        // are still backfilled from the stored names and the cached catalog.
        Assert.Equal(EnrichOutcome.SkippedFresh, result.Outcome);
        Assert.Equal(["AppLovin:Analytics", "AppLovin:Advertisement"], primary.TrackerTags);
        Assert.Equal(0, github.Calls + downloads.Calls);
    }

    [Fact]
    public async Task SignalHealReanalyzesPreSignalRowsOnce()
    {
        var (first, _, downloads, _, _) = HappyPath();
        var app = NewApp("signalheal", "SignalHeal", "https://github.com/example/signalheal");
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(1, downloads.Calls);
        var primary = Primary(app);
        Assert.True(primary.Inspected);

        // Rows analyzed before the signal scan existed have no inspected flag
        // and must be re-analyzed once to backfill their signals.
        primary.Inspected = false;
        await _db.SaveChangesAsync();
        Age(app);

        var (second, _, downloads2, _, _) = HappyPath();
        Assert.Equal(EnrichOutcome.Enriched, (await second.EnrichAsync(app, T0)).Outcome);

        Assert.Equal(1, downloads2.Calls);
        Assert.True(Primary(app).Inspected);

        // The backfilled flag stops the heal, so the next pass skips again.
        await _db.SaveChangesAsync();
        Age(app);
        var (third, _, downloads3, _, _) = HappyPath();
        Assert.Equal(EnrichOutcome.UpToDate, (await third.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(0, downloads3.Calls);
    }

    [Fact]
    public async Task SignalHealKeepsIndexOnlyRowsUpToDate()
    {
        var (first, _, _) = FdroidHappyPath();
        var app = NewApp("fdidxsignal", "FdIdxSignal", "https://f-droid.org/packages/com.example.app/");
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        Assert.False(Primary(app).Analyzed); // APK download failed: index-only row
        await _db.SaveChangesAsync();
        Age(app);

        // Index-only rows never ran a badging pass, so the signal heal stays
        // away from them and the unchanged index remains up to date.
        var (second, _, downloads2) = FdroidHappyPath();
        Assert.Equal(EnrichOutcome.UpToDate, (await second.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(0, downloads2.Calls);
    }

    [Fact]
    public async Task FirstInspectionQueuesUsageAnalysis()
    {
        var queue = new FakeUsageQueue();
        var (enricher, _, _, _, _) = HappyPath(usageQueue: queue);
        var app = NewApp("usagefirst", "UsageFirst", "https://github.com/example/usagefirst");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var call = Assert.Single(queue.Calls);
        Assert.Equal(app.Id, call.AppId);
        Assert.False(call.ArtifactChanged);
        Assert.True(call.FirstAnalysis);
    }

    [Fact]
    public async Task FirstInspectionQueuesThroughTheRealQueueWhileTheRowIsStillLinkOnly()
    {
        // A fresh row defaults to LinkOnly and only flips to DirectApk later in
        // ApplyAnalysisAsync; the artifact hook must queue anyway.
        var queue = new UsageAnalysisQueue(_db, new UsageAnalysisOptions { Enabled = true });
        var (enricher, _, _, _, _) = HappyPath(usageQueue: queue);
        var app = NewApp("usagereal", "UsageReal", "https://github.com/example/usagereal");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(Availability.DirectApk, app.Availability);
        // EnrichAsync leaves persistence to its caller; the pass would save here.
        await _db.SaveChangesAsync();
        var run = Assert.Single(_db.UsageAnalysisRuns);
        Assert.Equal(app.Id, run.AppId);
        Assert.Equal(UsageAnalysisStatus.Pending, run.Status);
    }

    [Fact]
    public async Task UnchangedArtifactDoesNotQueueUsageAgain()
    {
        var queue = new FakeUsageQueue();
        var (first, _, _, _, _) = HappyPath(usageQueue: queue);
        var app = NewApp("usageunchanged", "UsageUnchanged", "https://github.com/example/usageunchanged");
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        Assert.Single(queue.Calls);
        Age(app);

        var (second, _, _, _, _) = HappyPath(usageQueue: queue);
        Assert.Equal(EnrichOutcome.UpToDate, (await second.EnrichAsync(app, T0)).Outcome);

        // Routine passes never re-analyze an unchanged build.
        Assert.Single(queue.Calls);
    }

    [Fact]
    public async Task NewArtifactQueuesUsageAnalysisAgain()
    {
        var queue = new FakeUsageQueue();
        var signer = new FakeSignerRunner(_ => SignerOutputA);
        var (first, _, _, _, _) = HappyPath(usageQueue: queue, signer: signer);
        var app = NewApp("usagenew", "UsageNew", "https://github.com/example/usagenew");
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        Age(app);

        // A different icon changes the artifact hash, so the row is a new APK.
        // The signing cert stays the same, so the row identity survives the
        // URL change and the hash comparison sees the new bytes.
        var (second, _, _, _, _) = HappyPath(
            tag: "v1.1", versionCode: "43", iconColor: Color.Red,
            etag: "\"rel-etag-2\"", assetUrl: "https://cdn.example/app-1.1.apk",
            signer: signer, usageQueue: queue);
        Assert.Equal(EnrichOutcome.Enriched, (await second.EnrichAsync(app, T0)).Outcome);

        Assert.Equal(2, queue.Calls.Count);
        Assert.True(queue.Calls[1].ArtifactChanged);
        Assert.False(queue.Calls[1].FirstAnalysis);
        Assert.Equal("v1.1", queue.Calls[1].ReleaseRef);
    }

    [Fact]
    public async Task SameReleaseFlavorArtifactDoesNotQueueUsageAgain()
    {
        // universal-installer-style rotation: a later pass analyzes another
        // same-package flavor artifact of a release whose report is already
        // stored. The release tag matches, so the queue must not add a run.
        var queue = new UsageAnalysisQueue(_db, new UsageAnalysisOptions { Enabled = true });
        var signer = new FakeSignerRunner(_ => SignerOutputA);
        var (first, _, _, _, _) = HappyPath(usageQueue: queue, signer: signer);
        var app = NewApp("usagesamerelease", "UsageSameRelease", "https://github.com/example/usagesamerelease");
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();

        // The runner stores the analyzed release on the app; mark the queued
        // run done so only the release dedupe is under test.
        var run = Assert.Single(_db.UsageAnalysisRuns);
        run.Status = UsageAnalysisStatus.Succeeded;
        run.RepoRef = "v1.0";
        app.UsageAnalyzedAt = T0;
        app.UsageReleaseRef = "v1.0";
        await _db.SaveChangesAsync();
        Age(app);

        // Different bytes under the same tag: the artifact changed, the
        // release did not.
        var (second, _, _, _, _) = HappyPath(
            tag: "v1.0", versionCode: "43", iconColor: Color.Red,
            etag: "\"rel-etag-2\"", assetUrl: "https://cdn.example/app-1.0.apk",
            signer: signer, usageQueue: queue);
        Assert.Equal(EnrichOutcome.Enriched, (await second.EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();

        Assert.Single(_db.UsageAnalysisRuns);
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

    [Fact]
    public async Task VariantReadmeMirrorsRootOnLaterPasses()
    {
        // Variant rows created before the root had a README route carry no
        // snapshot or raw URL of their own, and variants never run their own
        // enrichment, so a later root pass must heal them.
        var phone = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(200, 200, Color.Blue)));
        var watch = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(400, 400, Color.Red)));
        var badgingByHash = new Dictionary<string, string>
        {
            [Sha256(phone)] = TestAssets.CannedBadging(package: "com.example.app", versionCode: "1", label: "Phone App"),
            [Sha256(watch)] = TestAssets.CannedBadging(package: "com.example.plugin", versionCode: "2",
                label: "Watch Plugin", features: ["android.hardware.type.watch"]),
        };
        var readmeAvailable = false;
        var github = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("/readme", StringComparison.Ordinal)
            ? readmeAvailable
                ? ReadmeJson("# Toolbox\n")
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"message":"Not Found"}"""),
                }
            : JsonReleases(ReleaseJsonMultiAssets(
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
        Assert.Null(variant.ReadmeUrl);
        // Variants share the root's serve surface, so they publish with it.
        Assert.Equal(T0, app.PublishedAt);
        Assert.Equal(T0, variant.PublishedAt);

        // Simulate a row created before the README route existed.
        variant.FullDescription = null;
        readmeAvailable = true;
        Age(app);

        var result = await enricher.EnrichAsync(app, T0, force: true);

        Assert.NotEqual(EnrichOutcome.Failed, result.Outcome);
        var expected = "https://raw.githubusercontent.com/example/toolbox/HEAD/README.md";
        Assert.Equal("# Toolbox\n", app.FullDescription);
        Assert.Equal(expected, app.ReadmeUrl);
        Assert.Equal("# Toolbox\n", variant.FullDescription);
        Assert.Equal(expected, variant.ReadmeUrl);
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
        // First pass: releases + repo stats + readme. The star feed stays
        // unread because the stats stub never yields a star count. The fresh
        // second pass must add nothing.
        Assert.Equal(3, github.Calls);
    }

    [Fact]
    public async Task RefetchesReadmeOnEveryPass()
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
                return ReadmeJson("# Fresh\n\n```kt\nval x = 1\n```\n");
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
        Assert.Equal("https://raw.githubusercontent.com/papergray/Legacy/HEAD/README.md", legacy.ReadmeUrl);
        Assert.Equal(1, readmeCalls);

        // Stored markdown is refreshed too, not treated as final.
        var markdown = NewApp("markdown", "Markdown", "https://github.com/papergray/Markdown");
        markdown.FullDescription = "# Already";
        await enricher.EnrichAsync(markdown, T0);
        Assert.Equal("# Fresh\n\n```kt\nval x = 1\n```\n", markdown.FullDescription);
        Assert.Equal("https://raw.githubusercontent.com/papergray/Markdown/HEAD/README.md", markdown.ReadmeUrl);
        Assert.Equal(2, readmeCalls);
    }

    [Fact]
    public async Task PrefersLinkedReadmeOverRepoDefault()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var readmeCalls = 0;
        var linkedCalls = 0;
        var github = new StubHandler(request =>
        {
            // A list entry linking README_EN.md directly must use that file and
            // leave the repo default README untouched.
            if (request.RequestUri!.Host == "raw.githubusercontent.com")
            {
                linkedCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("# Linked\n"),
                };
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/readme", StringComparison.Ordinal))
            {
                readmeCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("# Default\n"),
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
        var app = NewApp("vflow", "vFlow",
            "https://github.com/ChaoMixian/vFlow/blob/master/README_EN.md");

        await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal("# Linked\n", app.FullDescription);
        Assert.Equal("https://raw.githubusercontent.com/ChaoMixian/vFlow/master/README_EN.md", app.ReadmeUrl);
        Assert.Equal(1, linkedCalls);
        Assert.Equal(0, readmeCalls);
    }

    [Fact]
    public async Task LinkedReadmeFailureFallsBackToRepoDefault()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var readmeCalls = 0;
        var github = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/readme", StringComparison.Ordinal))
            {
                readmeCalls++;
                return ReadmeJson("# Default\n");
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
        var app = NewApp("vflowfallback", "vFlowFallback",
            "https://github.com/ChaoMixian/vFlow/blob/master/README_EN.md");
        app.FullDescription =
            "<div id=\"readme\" class=\"md\" data-path=\"README.md\"><p>rendered</p></div>";

        await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal("# Default\n", app.FullDescription);
        Assert.Equal("https://raw.githubusercontent.com/ChaoMixian/vFlow/HEAD/README.md", app.ReadmeUrl);
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
    public async Task StableDowngradeFlagsSupersededPrereleaseHistory()
    {
        // Prerelease-only repos used to serve their prerelease; when a stable
        // release appears selection moves back to it. History rows above the
        // newly served code are flagged so version_anomaly stays quiet.
        AppEnricher Wiring(string tag, bool prerelease, string versionCode, Color iconColor)
        {
            var zip = TestAssets.BuildApk(
                (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, iconColor)));
            var github = new StubHandler(_ => JsonReleases(
                ReleaseJson(tag, "app-release.apk", $"https://cdn.example/{tag}/app.apk", zip.Length,
                    prerelease: prerelease),
                "\"rel-etag\""));
            var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zip),
            });
            var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: versionCode));
            return BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        }

        var app = NewApp("retrograded", "Retrograded", "https://github.com/example/retrograded");
        Assert.Equal(EnrichOutcome.Enriched,
            (await Wiring("v2.0-beta", true, "50", Color.Blue).EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();
        Assert.Equal(50, Primary(app).VersionCode);
        Assert.True((await _db.AppVersions.SingleAsync(v => v.AppId == app.Id && v.VersionCode == 50)).IsPrerelease);

        // Rows written before the flag existed start out unflagged.
        var history = await _db.AppVersions.SingleAsync(v => v.AppId == app.Id && v.VersionCode == 50);
        history.IsPrerelease = false;
        await _db.SaveChangesAsync();

        Age(app);
        Assert.Equal(EnrichOutcome.Enriched,
            (await Wiring("v1.0", false, "40", Color.Red).EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();

        Assert.Equal(40, Primary(app).VersionCode);
        Assert.True((await _db.AppVersions.SingleAsync(v => v.AppId == app.Id && v.VersionCode == 50)).IsPrerelease);
        Assert.False((await _db.AppVersions.SingleAsync(v => v.AppId == app.Id && v.VersionCode == 40)).IsPrerelease);
    }

    [Fact]
    public async Task StableSwitchOutranksLegacyPerAbiPrereleaseRows()
    {
        // universal-revanced-manager case: the prerelease shipped one APK per
        // ABI, so its higher-coded rows outlived the switch to a stable
        // universal build. The release just scanned must own the primary.
        var arm64 = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(128, 128, Color.Blue)));
        var armeabi = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Green)));
        var x86 = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Red)));
        var universal = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(1024, 1024, Color.Yellow)));
        var abiByLength = new Dictionary<long, string>
        {
            [arm64.Length] = "arm64-v8a",
            [armeabi.Length] = "armeabi-v7a",
            [x86.Length] = "x86",
        };
        var betaGithub = new StubHandler(_ => JsonReleases(ReleaseJsonWithAssets(
            "v2.0-beta", true,
            ("app-arm64-v8a-release.apk", "https://cdn.example/beta/app-arm64-v8a-release.apk", arm64.Length),
            ("app-armeabi-v7a-release.apk", "https://cdn.example/beta/app-armeabi-v7a-release.apk", armeabi.Length),
            ("app-x86-release.apk", "https://cdn.example/beta/app-x86-release.apk", x86.Length)), "\"rel-etag\""));
        var betaDownloads = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var bytes = path.Contains("arm64", StringComparison.Ordinal) ? arm64
                : path.Contains("armeabi", StringComparison.Ordinal) ? armeabi
                : x86;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var betaAapt2 = new FakeAapt2Runner(apkPath =>
            TestAssets.CannedBadging(versionCode: "1004", abi: abiByLength[new FileInfo(apkPath).Length]));
        var app = NewApp("universalish", "Universalish", "https://github.com/example/universalish");

        var beta = BuildEnricher(betaGithub, betaDownloads, betaAapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        Assert.Equal(EnrichOutcome.Enriched, (await beta.EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();
        Assert.Equal(1004, Primary(app).VersionCode);
        Assert.Equal("v2.0-beta", Primary(app).ReleaseTag);
        Assert.True((await _db.AppVersions.SingleAsync(v => v.AppId == app.Id && v.VersionCode == 1004)).IsPrerelease);

        // Stable ships only a universal build, so it cannot reuse the per-ABI
        // rows recorded from the prerelease.
        var stableUrl = "https://cdn.example/stable/app-release.apk";
        var stableGithub = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app-release.apk", stableUrl, universal.Length), "\"rel-etag\""));
        var stableDownloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(universal),
        });
        var stableAapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "42"));

        Age(app);
        var stable = BuildEnricher(stableGithub, stableDownloads, stableAapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        Assert.Equal(EnrichOutcome.Enriched, (await stable.EnrichAsync(app, T0)).Outcome);
        await _db.SaveChangesAsync();

        Assert.Equal(4, _db.Downloads.Local.Count(d => d.AppId == app.Id));

        var primary = Primary(app);
        Assert.Equal(42, primary.VersionCode);
        Assert.Equal("v1.0", primary.ReleaseTag);
        Assert.Equal(stableUrl, primary.ApkUrl);
        Assert.Null(primary.Abi);
        Assert.False((await _db.AppVersions.SingleAsync(v => v.AppId == app.Id && v.VersionCode == 42)).IsPrerelease);
        Assert.True((await _db.AppVersions.SingleAsync(v => v.AppId == app.Id && v.VersionCode == 1004)).IsPrerelease);
    }

    [Fact]
    public async Task StableSwitchHealsPrimaryWhenArtifactIsSkippedByChecksum()
    {
        // A declared digest lets the stable artifact skip its download, so no
        // analysis runs; the finalizer alone must pull the primary away from
        // the legacy prerelease rows (the prod re-enrichment path).
        var arm64 = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(128, 128, Color.Blue)));
        var armeabi = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Green)));
        var x86 = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Red)));
        var universal = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(1024, 1024, Color.Yellow)));
        var abiByLength = new Dictionary<long, string>
        {
            [arm64.Length] = "arm64-v8a",
            [armeabi.Length] = "armeabi-v7a",
            [x86.Length] = "x86",
        };
        var betaGithub = new StubHandler(_ => JsonReleases(ReleaseJsonWithAssets(
            "v2.0-beta", true,
            ("app-arm64-v8a-release.apk", "https://cdn.example/beta/app-arm64-v8a-release.apk", arm64.Length),
            ("app-armeabi-v7a-release.apk", "https://cdn.example/beta/app-armeabi-v7a-release.apk", armeabi.Length),
            ("app-x86-release.apk", "https://cdn.example/beta/app-x86-release.apk", x86.Length)), "\"rel-etag\""));
        var betaDownloads = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var bytes = path.Contains("arm64", StringComparison.Ordinal) ? arm64
                : path.Contains("armeabi", StringComparison.Ordinal) ? armeabi
                : x86;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var betaAapt2 = new FakeAapt2Runner(apkPath =>
            TestAssets.CannedBadging(versionCode: "1004", abi: abiByLength[new FileInfo(apkPath).Length]));
        var app = NewApp("universalish", "Universalish", "https://github.com/example/universalish");

        var beta = BuildEnricher(betaGithub, betaDownloads, betaAapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        await beta.EnrichAsync(app, T0);
        await _db.SaveChangesAsync();

        var stableUrl = "https://cdn.example/stable/app-release.apk";
        var stableGithub = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app-release.apk", stableUrl, universal.Length), "\"rel-etag\""));
        var stableDownloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(universal),
        });
        var stable = BuildEnricher(stableGithub, stableDownloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "42")),
            signer: new FakeSignerRunner(_ => SignerOutputA));
        Age(app);
        await stable.EnrichAsync(app, T0);
        await _db.SaveChangesAsync();

        // Legacy state: the prerelease row is primary again and carries no tag.
        var stableRow = _db.Downloads.Single(d => d.AppId == app.Id && d.ApkUrl == stableUrl);
        var legacy = _db.Downloads.Single(d => d.AppId == app.Id && d.Abi == "arm64-v8a");
        stableRow.IsPrimary = false;
        await _db.SaveChangesAsync();
        legacy.IsPrimary = true;
        legacy.ReleaseTag = null;
        await _db.SaveChangesAsync();

        Age(app);
        var skipGithub = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app-release.apk", stableUrl, universal.Length,
                digest: $"sha256:{Sha256(universal)}"), "\"rel-etag\""));
        var downloads = new StubHandler(_ => throw new InvalidOperationException("checksum skip must not download"));
        var skip = BuildEnricher(skipGithub, downloads,
            new FakeAapt2Runner(_ => throw new InvalidOperationException("checksum skip must not analyze")));

        var result = await skip.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Equal(0, downloads.Calls);
        Assert.Equal(42, Primary(app).VersionCode);
        Assert.Equal("v1.0", Primary(app).ReleaseTag);
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
    public async Task DeferredXmlRendersKeepTheExistingIcon()
    {
        var (enricher, _, _, _, _) = HappyPath();
        var app = NewApp("defericon", "Deferred Icon", "https://github.com/o/rd");
        await enricher.EnrichAsync(app, T0);
        var original = app.IconHash;
        Assert.NotNull(original);

        Age(app);
        var (second, _, _, _, _) = HappyPath(
            tag: "v1.1", versionCode: "43", iconColor: Color.Red,
            assetUrl: "https://cdn.example/app-v1.1.apk");
        // A deferred pass resolves rasters only; the inline analysis must not
        // overwrite the existing icon before the batch phase renders the XML one.
        _options.DeferXmlIconRenders = true;
        try
        {
            var result = await second.EnrichAsync(app, T0);
            Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        }
        finally
        {
            _options.DeferXmlIconRenders = false;
        }

        Assert.Equal(original, app.IconHash);
    }

    [Fact]
    public async Task SkipIconRendersKeepsTheExistingIcon()
    {
        var (enricher, _, _, _, _) = HappyPath();
        var app = NewApp("skipicon", "Skipped Icon", "https://github.com/o/rd");
        await enricher.EnrichAsync(app, T0);
        var original = app.IconHash;
        Assert.NotNull(original);

        Age(app);
        var (second, _, _, _, _) = HappyPath(
            tag: "v1.1", versionCode: "43", iconColor: Color.Red,
            assetUrl: "https://cdn.example/app-v1.1.apk");
        // An icons:false admin pass renders nothing: the build updates but the
        // recorded icon must survive untouched.
        _options.SkipIconRenders = true;
        try
        {
            var result = await second.EnrichAsync(app, T0);
            Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        }
        finally
        {
            _options.SkipIconRenders = false;
        }

        Assert.Equal(original, app.IconHash);
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
        // A failed first check must leave the row hidden until a pass succeeds.
        Assert.Null(app.PublishedAt);
        Assert.False(HasDownloads(app));

        // Immediate retry is skipped (backoff); after the window it retries.
        Assert.Equal(EnrichOutcome.SkippedFresh, (await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0)).Outcome);
        // Releases plus the best-effort stats call, both 404 here; star
        // history is skipped because no star count ever arrived to anchor
        // its levels on.
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
        Assert.Equal(T0, app.PublishedAt);
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
        app.ReadmeUrl = "https://raw.githubusercontent.com/example/switcher/HEAD/README.md";

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
        // Play copy replaces the README; the stale raw route must not survive.
        Assert.Null(app.ReadmeUrl);
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
    public async Task PlayOnlyAppBecomesPlayRedirect()
    {
        var github = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub"));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(TestAssets.SolidPng(512, 512, Color.Red)),
        });
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var play = new FakePlayClient(_ => "https://play-lh.googleusercontent.com/icon=s0-br30");
        var app = NewApp("play-only-x", "Play Only",
            "https://play.google.com/store/apps/details?id=com.example.pe");
        var enricher = BuildEnricher(github, downloads, aapt2, play: play);

        var result = await enricher.EnrichAsync(app, T0.AddMinutes(5));

        Assert.Equal(EnrichOutcome.AvatarFallback, result.Outcome);
        Assert.Equal(Availability.PlayRedirect, app.Availability);
        Assert.Equal("https://play.google.com/store/apps/details?id=com.example.pe", app.StoreUrl);
        Assert.Null(app.ExcludedReason);
        Assert.False(HasDownloads(app));
        Assert.Equal(1, play.Calls);
        // The changes feed only ships rows whose updated_at moved, so the
        // availability flip has to carry the enrich timestamp.
        Assert.Equal(T0.AddMinutes(5), app.UpdatedAt);

        // A re-check that flips nothing must not churn the feed.
        await enricher.EnrichAsync(app, T0.AddMinutes(10), force: true);
        Assert.Equal(T0.AddMinutes(5), app.UpdatedAt);
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
            ["_links"] = new JsonObject
            {
                ["self"] = $"https://gitlab.com/o/r/-/releases/{tag}",
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

    // Real index-v2.json shape: metadata plus one release and no signer, so
    // index-only rows keep a null SHA-256 until analysis or a signed index.
    private const string FdroidIndexV2Json = """
        {
          "packages": {
            "com.example.app": {
              "metadata": {
                "description": { "en-US": "A <b>plain</b> summary of the app." },
                "icon": { "en-US": { "name": "/com.example.app/en-US/icon.png" } },
                "sourceCode": "https://github.com/example/aod"
              },
              "versions": {
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef": {
                  "added": 1720872254000,
                  "file": {
                    "name": "/com.example.app_20.apk",
                    "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    "size": 1234567
                  },
                  "manifest": {
                    "versionCode": 20,
                    "versionName": "2.0",
                    "usesSdk": { "minSdkVersion": 26 }
                  }
                }
              }
            }
          }
        }
        """;

    // One release per architecture plus a genuinely older package (1.0) that
    // must not be mistaken for an ABI sibling.
    private const string FdroidIndexV2JsonWithArchSiblings = """
        {
          "packages": {
            "com.example.app": {
              "metadata": {
                "icon": { "en-US": { "name": "/com.example.app/en-US/icon.png" } },
                "sourceCode": "https://github.com/example/aod"
              },
              "versions": {
                "aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111": {
                  "file": {
                    "name": "/com.example.app_2004.apk",
                    "sha256": "aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111",
                    "size": 2222222
                  },
                  "manifest": {
                    "versionCode": 2004,
                    "versionName": "2.0",
                    "nativecode": [ "arm64-v8a" ],
                    "usesSdk": { "minSdkVersion": 26 }
                  }
                },
                "bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222": {
                  "file": {
                    "name": "/com.example.app_2003.apk",
                    "sha256": "bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222",
                    "size": 1111111
                  },
                  "manifest": {
                    "versionCode": 2003,
                    "versionName": "2.0",
                    "nativecode": [ "armeabi-v7a" ],
                    "usesSdk": { "minSdkVersion": 26 }
                  }
                },
                "cccc3333cccc3333cccc3333cccc3333cccc3333cccc3333cccc3333cccc3333": {
                  "file": {
                    "name": "/com.example.app_1000.apk",
                    "sha256": "cccc3333cccc3333cccc3333cccc3333cccc3333cccc3333cccc3333cccc3333",
                    "size": 999999
                  },
                  "manifest": {
                    "versionCode": 1000,
                    "versionName": "1.0",
                    "nativecode": [ "x86" ],
                    "usesSdk": { "minSdkVersion": 26 }
                  }
                }
              }
            }
          }
        }
        """;

    // Same package plus the authoritative signer certificate SHA-256, as
    // served by a real F-Droid repo.
    private const string FdroidIndexV2JsonWithSigner = """
        {
          "packages": {
            "com.example.app": {
              "metadata": {
                "description": { "en-US": "A <b>plain</b> summary of the app." },
                "icon": { "en-US": { "name": "/com.example.app/en-US/icon.png" } },
                "sourceCode": "https://github.com/example/aod"
              },
              "versions": {
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef": {
                  "file": {
                    "name": "/com.example.app_20.apk",
                    "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    "size": 1234567
                  },
                  "manifest": {
                    "versionCode": 20,
                    "versionName": "2.0",
                    "usesSdk": { "minSdkVersion": 26 },
                    "signer": {
                      "sha256": [ "980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9" ]
                    }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>F-Droid wiring: index fetch + icon mirror; the APK download is attempted but 404s, exercising the index-only fallback.</summary>
    private (AppEnricher Enricher, StubHandler Fdroid, StubHandler Downloads)
        FdroidHappyPath(byte[]? iconBytes = null, bool icon404 = false, Func<HttpResponseMessage>? githubResponse = null,
            IzzyStatsProvider? izzyStats = null, string? indexV2Json = null, IRepoScreenshotResolver? repoScreenshots = null)
    {
        iconBytes ??= TestAssets.SolidPng(256, 256, Color.Purple);
        var fdroid = new StubHandler(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("index-v2.json"))
            {
                throw new InvalidOperationException("must not fetch index.xml");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(indexV2Json ?? FdroidIndexV2Json),
            };
        });
        var downloads = new StubHandler(request =>
        {
            if (icon404 || !request.RequestUri!.ToString().EndsWith(".png"))
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
        return (BuildEnricher(github, downloads, aapt2, gitlab, fdroid, izzyStats: izzyStats, repoScreenshots: repoScreenshots), fdroid, downloads);
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
        Assert.Equal("https://gitlab.com/o/r/-/raw/master/README.md", app.ReadmeUrl);
        Assert.Equal("## 1.0\n- First release", app.Changelog);
        Assert.Equal("https://gitlab.com/o/r/-/releases/v1.0", app.ChangelogUrl);
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
        // One index-v2 fetch per repo serves the package and the screenshots
        // (F-Droid for the package plus screenshots, Izzy for screenshots).
        Assert.Equal(2, fdroid.Calls);
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
        // No APK was analyzed, so no signing-cert MD5 is recorded.
        Assert.Null(primary.SigMd5);
        Assert.Null(app.LastError);
        // The index version's `added` timestamp is the only release date
        // these repos publish; it feeds "recently updated" like a forge one.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1720872254000), app.VersionUpdatedAt);
        // metadata.description is the only changelog text the index has.
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
                  },
                  "versions": {
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef": {
                      "file": {
                        "name": "/com.example.app_20.apk",
                        "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                        "size": 1234567
                      },
                      "manifest": {
                        "versionCode": 20,
                        "versionName": "2.0",
                        "usesSdk": { "minSdkVersion": 26 }
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
                  },
                  "versions": {
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef": {
                      "file": {
                        "name": "/com.example.app_20.apk",
                        "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                        "size": 1234567
                      },
                      "manifest": {
                        "versionCode": 20,
                        "versionName": "2.0",
                        "usesSdk": { "minSdkVersion": 26 }
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

            if (!request.RequestUri.AbsolutePath.EndsWith("index-v2.json"))
            {
                throw new InvalidOperationException("must not fetch index.xml");
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(indexV2) };
        });
        var downloads = new StubHandler(request => request.RequestUri!.ToString().EndsWith(".png")
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
    public async Task RepoFallbackFillsScreenshotsWhenIndexesEmpty()
    {
        // F-Droid/Izzy answer (so the old code would stop) but carry nothing;
        // the app's own repo tree is the fallback source.
        string[] fromRepo =
        [
            "https://raw.githubusercontent.com/o/app/abc/fastlane/metadata/android/en-US/images/phoneScreenshots/1.jpg",
        ];
        var repo = new FakeRepoScreenshots(() => fromRepo);
        var (enricher, _, _, _, _) = HappyPath(repoScreenshots: repo);
        var app = NewApp("repo-shots", "Repo Shots", "https://github.com/o/app");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(fromRepo, app.Screenshots);
        Assert.Equal(T0, app.ScreenshotsCheckedAt);
        Assert.Equal(1, repo.Calls);
    }

    [Fact]
    public async Task FdroidScreenshotsWinOverRepoFallback()
    {
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
                        ]
                      }
                    }
                  },
                  "versions": {
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef": {
                      "file": {
                        "name": "/com.example.app_20.apk",
                        "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                        "size": 1234567
                      },
                      "manifest": {
                        "versionCode": 20,
                        "versionName": "2.0",
                        "usesSdk": { "minSdkVersion": 26 }
                      }
                    }
                  }
                }
              }
            }
            """;
        var repo = new FakeRepoScreenshots(() => throw new InvalidOperationException("must not clone"));
        var (enricher, _, _) = FdroidHappyPath(indexV2Json: indexV2, repoScreenshots: repo);
        var app = NewApp("catshare", "CatShare", "https://f-droid.org/packages/com.example.app/");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(
            [
                "https://f-droid.org/repo/com.example.app/en-US/phoneScreenshots/00.png",
                "https://f-droid.org/repo/com.example.app/en-US/phoneScreenshots/01.png",
            ],
            app.Screenshots);
        Assert.Equal(0, repo.Calls);
    }

    [Fact]
    public async Task RepoFallbackSkippedWhenScreenshotsExist()
    {
        // Indexes unreachable (so the stored set survives) and the row already
        // has screenshots: the repo must not be cloned.
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app-release.apk", "https://cdn.example/app.apk", zip.Length), "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var repo = new FakeRepoScreenshots(() => throw new InvalidOperationException("must not clone"));
        var enricher = BuildEnricher(
            github, downloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "42")),
            fdroid: new StubHandler(_ => throw new HttpRequestException("Connection refused")),
            repoScreenshots: repo);
        var app = NewApp("has-shots", "Has Shots", "https://github.com/o/app");
        app.Screenshots = ["https://f-droid.org/repo/com.example.app/en-US/phoneScreenshots/00.png"];

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Single(app.Screenshots);
        Assert.Equal(0, repo.Calls);
    }

    [Fact]
    public async Task RepoFallbackSkippedWithinRecheckWindow()
    {
        var repo = new FakeRepoScreenshots(() => throw new InvalidOperationException("must not clone"));
        var (enricher, _, _, _, _) = HappyPath(repoScreenshots: repo);
        var app = NewApp("checked", "Checked", "https://github.com/o/app");
        app.ScreenshotsCheckedAt = T0;

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Empty(app.Screenshots);
        Assert.Equal(T0, app.ScreenshotsCheckedAt);
        Assert.Equal(0, repo.Calls);
    }

    [Fact]
    public async Task RepoFallbackFailureDoesNotFailEnrichment()
    {
        var repo = new FakeRepoScreenshots(() => throw new InvalidOperationException("clone crashed"));
        var (enricher, _, _, _, _) = HappyPath(repoScreenshots: repo);
        var app = NewApp("throwing", "Throwing", "https://github.com/o/app");

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Empty(app.Screenshots);
        Assert.Equal(T0, app.ScreenshotsCheckedAt);
        Assert.Equal(1, repo.Calls);
    }

    [Fact]
    public async Task RepoUrlsSurviveEmptyIndexOnNormalPass()
    {
        // The index answers with nothing but the repo recheck window has not
        // elapsed: the stored repo URLs stay and no clone is paid for.
        var repo = new FakeRepoScreenshots(() => throw new InvalidOperationException("must not clone"));
        var (enricher, _, _, _, _) = HappyPath(repoScreenshots: repo);
        var app = NewApp("repo-only", "Repo Only", "https://github.com/o/app");
        app.Screenshots = ["https://raw.githubusercontent.com/o/app/abc/docs/screenshots/1.png"];
        app.ScreenshotsCheckedAt = T0 - TimeSpan.FromDays(1);

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(
            ["https://raw.githubusercontent.com/o/app/abc/docs/screenshots/1.png"],
            app.Screenshots);
        Assert.Equal(0, repo.Calls);
    }

    [Fact]
    public async Task RefreshScreenshotsBypassesRecheckWindow()
    {
        string[] fromRepo =
        [
            "https://raw.githubusercontent.com/o/app/abc/fastlane/metadata/android/en-US/images/phoneScreenshots/1.jpg",
        ];
        var repo = new FakeRepoScreenshots(() => fromRepo);
        var (enricher, _, _, _, _) = HappyPath(repoScreenshots: repo);
        var app = NewApp("forced", "Forced", "https://github.com/o/app");
        app.ScreenshotsCheckedAt = T0; // inside the 7-day window

        var result = await enricher.RefreshScreenshotsAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(fromRepo, app.Screenshots);
        Assert.Equal(1, repo.Calls);
    }

    [Fact]
    public async Task RefreshScreenshotsIsUpToDateWhenUnchanged()
    {
        // The forced refresh re-resolves repo URLs too; identical findings
        // leave the stored list alone.
        string[] stored = ["https://raw.githubusercontent.com/o/app/abc/docs/screenshots/1.png"];
        var repo = new FakeRepoScreenshots(() => stored);
        var (enricher, _, _, _, _) = HappyPath(repoScreenshots: repo);
        var app = NewApp("stable", "Stable", "https://github.com/o/app");
        app.Screenshots = [.. stored];

        var result = await enricher.RefreshScreenshotsAsync(app, T0);

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Equal(stored, app.Screenshots);
        Assert.Equal(1, repo.Calls);
        Assert.Equal(T0, app.ScreenshotsCheckedAt);
    }

    [Fact]
    public async Task RefreshScreenshotsReplacesRepoUrls()
    {
        // Repo URLs are pinned to a commit; a new HEAD must replace them, the
        // old list is not merged in or kept.
        string[] fresh = ["https://raw.githubusercontent.com/o/app/def/docs/screenshots/2.png"];
        var repo = new FakeRepoScreenshots(() => fresh);
        var (enricher, _, _, _, _) = HappyPath(repoScreenshots: repo);
        var app = NewApp("moved", "Moved", "https://github.com/o/app");
        app.Screenshots = ["https://raw.githubusercontent.com/o/app/abc/docs/screenshots/1.png"];

        var result = await enricher.RefreshScreenshotsAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(fresh, app.Screenshots);
    }

    [Fact]
    public async Task RefreshScreenshotsClearsRepoUrlsWhenRepoIsEmpty()
    {
        // The repo answered and no longer offers screenshots: the dead URLs go.
        var repo = new FakeRepoScreenshots(() => []);
        var (enricher, _, _, _, _) = HappyPath(repoScreenshots: repo);
        var app = NewApp("cleaned", "Cleaned", "https://github.com/o/app");
        app.Screenshots = ["https://raw.githubusercontent.com/o/app/abc/docs/screenshots/1.png"];

        var result = await enricher.RefreshScreenshotsAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Empty(app.Screenshots);
        Assert.Equal(1, repo.Calls);
    }

    [Fact]
    public async Task RefreshScreenshotsKeepsRepoUrlsWhenRepoUnreachable()
    {
        // Clone failure means the URLs are merely unverified, not dead.
        var repo = new FakeRepoScreenshots(() => []) { Reached = false };
        var (enricher, _, _, _, _) = HappyPath(repoScreenshots: repo);
        var app = NewApp("unreachable", "Unreachable", "https://github.com/o/app");
        app.Screenshots = ["https://raw.githubusercontent.com/o/app/abc/docs/screenshots/1.png"];

        var result = await enricher.RefreshScreenshotsAsync(app, T0);

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Single(app.Screenshots);
        Assert.Equal(1, repo.Calls);
        Assert.Equal(T0, app.ScreenshotsCheckedAt);
    }

    [Fact]
    public async Task RefreshScreenshotsDropsRepoUrlsWhenIndexSuppliesShots()
    {
        // Index shots win wholesale: repo leftovers are dropped without a clone.
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
        var repo = new FakeRepoScreenshots(() => throw new InvalidOperationException("must not clone"));
        var (enricher, _, _) = FdroidHappyPath(indexV2Json: indexV2, repoScreenshots: repo);
        var app = NewApp("index-wins", "Index Wins", "https://f-droid.org/packages/com.example.app/");
        app.PackageName = "com.example.app";
        app.Screenshots = ["https://raw.githubusercontent.com/o/app/abc/docs/screenshots/1.png"];

        var result = await enricher.RefreshScreenshotsAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(
            ["https://f-droid.org/repo/com.example.app/en-US/phoneScreenshots/00.png"],
            app.Screenshots);
        Assert.Equal(0, repo.Calls);
    }

    [Fact]
    public async Task RefreshScreenshotsReplacesMixedSourcesWithIndexUrls()
    {
        // A mixed list (index + repo URLs) is rebuilt from the index alone.
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
                  },
                  "versions": {
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef": {
                      "file": {
                        "name": "/com.example.app_20.apk",
                        "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                        "size": 1234567
                      },
                      "manifest": {
                        "versionCode": 20,
                        "versionName": "2.0",
                        "usesSdk": { "minSdkVersion": 26 }
                      }
                    }
                  }
                }
              }
            }
            """;
        var repo = new FakeRepoScreenshots(() => throw new InvalidOperationException("must not clone"));
        var (enricher, _, _) = FdroidHappyPath(indexV2Json: indexV2, repoScreenshots: repo);
        var app = NewApp("mixed", "Mixed", "https://f-droid.org/packages/com.example.app/");
        app.Screenshots =
        [
            "https://f-droid.org/repo/com.example.app/en-US/phoneScreenshots/99.png",
            "https://raw.githubusercontent.com/o/app/abc/docs/screenshots/1.png",
        ];

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(
            ["https://f-droid.org/repo/com.example.app/en-US/phoneScreenshots/00.png"],
            app.Screenshots);
        Assert.Equal(0, repo.Calls);
    }

    [Fact]
    public async Task NormalPassRechecksRepoUrlsAfterWindow()
    {
        // Outside the recheck window a normal pass re-verifies repo-sourced
        // URLs even though the field is not empty.
        string[] fresh = ["https://raw.githubusercontent.com/o/app/def/docs/screenshots/2.png"];
        var repo = new FakeRepoScreenshots(() => fresh);
        var (enricher, _, _, _, _) = HappyPath(repoScreenshots: repo);
        var app = NewApp("weekly", "Weekly", "https://github.com/o/app");
        app.Screenshots = ["https://raw.githubusercontent.com/o/app/abc/docs/screenshots/1.png"];
        app.ScreenshotsCheckedAt = T0 - TimeSpan.FromDays(8);

        Assert.Equal(EnrichOutcome.Enriched, (await enricher.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(fresh, app.Screenshots);
        Assert.Equal(1, repo.Calls);
        Assert.Equal(T0, app.ScreenshotsCheckedAt);
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

    [Fact]
    public async Task FdroidIndexDateHealsOnUnchangedIndex()
    {
        // Rows enriched before the index date was parsed carry no
        // VersionUpdatedAt; the unchanged-index fast path must backfill it and
        // move updated_at (the changes feed keys on that) without re-fetching
        // the APK.
        var (first, _, _) = FdroidHappyPath();
        var app = NewApp("fddate", "FdDate", "https://f-droid.org/packages/com.example.app/");
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        app.VersionUpdatedAt = null;
        await _db.SaveChangesAsync();
        Age(app);

        var (second, _, downloads) = FdroidHappyPath();
        var now = T0 + TimeSpan.FromDays(1);
        var result = await second.EnrichAsync(app, now);

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1720872254000), app.VersionUpdatedAt);
        Assert.Equal(now, app.UpdatedAt);
        Assert.Equal(0, downloads.Calls);
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
            Content = new StringContent(FdroidIndexV2Json),
        });
        var downloads = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.ToString().EndsWith(".png")
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
            Content = new StringContent(FdroidIndexV2JsonWithArchSiblings),
        });
        var downloads = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.ToString().EndsWith(".png")
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
        Assert.Null(sibling.SigMd5); // legacy v1 <sig> is not a cert digest
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
                Content = new StringContent(FdroidIndexV2Json),
            };
        });
        var downloads = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.ToString().EndsWith(".png")
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
    public async Task FdroidIndexOnlyRowBackfillsSignerFromIndexV2()
    {
        // A signed index entry carries the authoritative certificate with the
        // release, so the index-only row records its SHA-256 identity up front
        // and must not keep healing afterwards.
        var (first, _, _) = FdroidHappyPath(indexV2Json: FdroidIndexV2JsonWithSigner);
        var app = NewApp("fdv2", "FdV2", "https://f-droid.org/packages/com.example.app/");
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        var primary = Primary(app);
        Assert.Equal("980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9", primary.SigSha256);
        Assert.Null(primary.SigMd5);
        Assert.False(primary.Analyzed);
        await _db.SaveChangesAsync();
        Age(app);

        var (second, _, downloads) = FdroidHappyPath(indexV2Json: FdroidIndexV2JsonWithSigner);
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
        // One index-v2 fetch per repo serves the source lookup, the package
        // read and the screenshots.
        Assert.Equal(2, fdroid.Calls);
        Assert.Equal(Availability.DirectApk, app.Availability);
        var primary = Primary(app);
        Assert.Equal(SourceKind.FDroid, primary.Source);
        Assert.Equal("com.example.app", primary.SourceRef);
        Assert.Equal("https://f-droid.org/repo/com.example.app_20.apk", primary.ApkUrl);
    }

    [Fact]
    public async Task AssetlessNewestForgeReleaseUsesNewerFdroidBuild()
    {
        // The project stopped attaching APKs to GitHub releases and moved to
        // F-Droid; the old GitHub APK must not stay the served build.
        var (enricher, _, _) = FdroidHappyPath(githubResponse: () => JsonReleases(
            AssetlessNewestFeed("v2.0", "v1.0", "https://cdn.example/forge-v1.0.apk", 100)));
        var app = NewApp("assetless", "Assetless", "https://github.com/example/aod");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.True(app.ForgeAssetsStale);
        Assert.Equal(SourceKind.FDroid, app.SourceKind);
        Assert.Equal(Availability.DirectApk, app.Availability);
        var primary = Primary(app);
        Assert.Equal(SourceKind.FDroid, primary.Source);
        Assert.Equal(20L, primary.VersionCode);
        Assert.Equal("https://f-droid.org/repo/com.example.app_20.apk", primary.ApkUrl);
        // The stale forge release is not even recorded as a candidate.
        Assert.DoesNotContain(_db.Downloads.Local, d => d.AppId == app.Id && d.Source == SourceKind.GitHub);
    }

    [Fact]
    public async Task AssetlessNewestForgeReleaseWithoutFdroidKeepsServableForgeApk()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Green)));
        var github = new StubHandler(_ => JsonReleases(
            AssetlessNewestFeed("v2.0", "v1.0", "https://cdn.example/forge-v1.0.apk", zip.Length)));
        var downloads = new StubHandler(request =>
            request.RequestUri!.ToString().EndsWith(".apk")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "5"));
        var app = NewApp("assetless-nofd", "AssetlessNoFd", "https://github.com/example/aod");

        var result = await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.True(app.ForgeAssetsStale);
        var primary = Primary(app);
        Assert.Equal(SourceKind.GitHub, primary.Source);
        Assert.Equal(5L, primary.VersionCode);
        Assert.Equal("https://cdn.example/forge-v1.0.apk", primary.ApkUrl);
    }

    [Fact]
    public async Task StaleForgeKeepsFdroidPrimaryOnRepass()
    {
        var (first, _, _) = FdroidHappyPath(githubResponse: () => JsonReleases(
            AssetlessNewestFeed("v2.0", "v1.0", "https://cdn.example/forge-v1.0.apk", 100)));
        var app = NewApp("assetless-repass", "AssetlessRepass", "https://github.com/example/aod");
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(SourceKind.FDroid, Primary(app).Source);
        await _db.SaveChangesAsync();
        Age(app);

        var (second, _, _) = FdroidHappyPath(githubResponse: () => JsonReleases(
            AssetlessNewestFeed("v2.0", "v1.0", "https://cdn.example/forge-v1.0.apk", 100)));
        // The index is unchanged, so the pass is a no-op; the stale forge
        // feed must not flip the primary back.
        Assert.Equal(EnrichOutcome.UpToDate, (await second.EnrichAsync(app, T0)).Outcome);

        Assert.True(app.ForgeAssetsStale);
        var primary = Primary(app);
        Assert.Equal(SourceKind.FDroid, primary.Source);
        Assert.Equal(20L, primary.VersionCode);
    }

    [Fact]
    public async Task ForgeShippingAssetsAgainRestoresForgePrimary()
    {
        var (first, _, _) = FdroidHappyPath(githubResponse: () => JsonReleases(
            AssetlessNewestFeed("v2.0", "v1.0", "https://cdn.example/forge-v1.0.apk", 100)));
        var app = NewApp("assetless-resume", "AssetlessResume", "https://github.com/example/aod");
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(SourceKind.FDroid, Primary(app).Source);
        await _db.SaveChangesAsync();
        Age(app);

        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Red)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJson(
            "v2.0", "app-release.apk", "https://cdn.example/forge-v2.0.apk", zip.Length)));
        var downloads = new StubHandler(request =>
            request.RequestUri!.ToString() == "https://cdn.example/forge-v2.0.apk"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "25"));
        var fdroid = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(FdroidIndexV2Json),
        });

        var result = await BuildEnricher(github, downloads, aapt2, fdroid: fdroid).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.False(app.ForgeAssetsStale);
        var primary = Primary(app);
        Assert.Equal(SourceKind.GitHub, primary.Source);
        Assert.Equal(25L, primary.VersionCode);
        Assert.Equal("https://cdn.example/forge-v2.0.apk", primary.ApkUrl);
    }

    [Fact]
    public async Task ServedBuildSwitchQueuesUsageReAnalysis()
    {
        var queue = new FakeUsageQueue();
        var app = NewApp("assetless-queue", "AssetlessQueue", "https://github.com/example/aod");
        AddDownload(app, SourceKind.GitHub, "https://cdn.example/forge-v1.0.apk",
            versionCode: 5, sha256: "aa11", size: 100, sourceRef: "example/aod", primary: true,
            packageName: "com.example.app");
        app.PackageName = "com.example.app";
        app.UsageAnalyzedAt = T0;
        await _db.SaveChangesAsync();

        var iconBytes = TestAssets.SolidPng(256, 256, Color.Purple);
        var downloads = new StubHandler(request =>
            request.RequestUri!.ToString().EndsWith(".png")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(iconBytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var github = new StubHandler(_ => JsonReleases(
            AssetlessNewestFeed("v2.0", "v1.0", "https://cdn.example/forge-v1.0.apk", 100)));
        var fdroid = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(FdroidIndexV2Json),
        });
        var aapt2 = new FakeAapt2Runner(_ => throw new InvalidOperationException("must not run aapt2"));
        var enricher = BuildEnricher(github, downloads, aapt2, fdroid: fdroid, usageQueue: queue);

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(SourceKind.FDroid, Primary(app).Source);
        var call = Assert.Single(queue.Calls);
        Assert.True(call.ArtifactChanged);
        Assert.False(call.FirstAnalysis);
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
            if (url.EndsWith(".png"))
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
            Content = new StringContent(FdroidIndexV2Json),
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
        // Both rows carry the scanned tag, so the release-tag preference ties
        // and the higher version code keeps the primary; the analyzed build
        // must still stamp the check on its way out.
        var (enricher, _, _, _, _) = HappyPath(versionCode: "42");
        var app = NewApp("loser", "Loser", "https://github.com/example/loser");
        var incumbent = AddDownload(app, SourceKind.GitHub, "https://example.com/newer.apk", versionCode: 100);
        incumbent.ReleaseTag = "v1.0";
        await _db.SaveChangesAsync();

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Equal("https://example.com/newer.apk", Primary(app).ApkUrl);
        Assert.Equal(T0, app.LastCheckedAt);
        Assert.Null(app.LastError);
    }

    [Fact]
    public async Task PrimaryFlipLeavesExactlyOnePrimary()
    {
        // The partial unique index on (app_id, is_primary) is not deferrable,
        // so the demotion of the old primary must reach the database before
        // the promotion of the new row lands (live 2026-09-24: batched
        // promote-first updates tripped IX_app_downloads_app_id).
        var (enricher, _, _, _, _) = HappyPath(versionCode: "42");
        var app = NewApp("primary-flip", "Primary Flip", "https://github.com/example/primaryflip");
        AddDownload(app, SourceKind.GitHub, "https://example.com/old.apk", versionCode: 5, sigSha256: SignerOutputB);

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var rows = _db.Downloads.Local.Where(d => d.AppId == app.Id).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(42, rows.Single(d => d.IsPrimary).VersionCode);
    }

    [Fact]
    public async Task ReEnrichmentKeepsTheSameRowPrimary()
    {
        // The raw demote in RecomputePrimaryAsync bypasses the tracker, so the
        // tracked row keeps its old flag as the original value; without
        // rebasing it, a later recompute that picks the same row looks like a
        // no-op and silently drops the primary (live 2026-09-24: hundreds of
        // direct-APK apps ended up with no primary download).
        var (first, _, _, _, _) = HappyPath(signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("primary-keep", "Primary Keep", "https://github.com/example/primarykeep");
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        Assert.NotNull(Primary(app));
        await _db.SaveChangesAsync();
        Age(app);

        // A new release ships a new asset URL and different bytes, but the same
        // signing identity, so the artifact is analyzed again and the upsert
        // claims the exact same row.
        var (second, _, _, _, _) = HappyPath(
            tag: "v2.0",
            versionCode: "43",
            iconColor: Color.Red,
            etag: "\"rel-etag-2\"",
            signer: new FakeSignerRunner(_ => SignerOutputA),
            assetUrl: "https://cdn.example/app-v43.apk");
        Assert.Equal(EnrichOutcome.Enriched, (await second.EnrichAsync(app, T0)).Outcome);

        var rows = _db.Downloads.Local.Where(d => d.AppId == app.Id).ToList();
        Assert.Single(rows);
        Assert.True(rows[0].IsPrimary);
    }

    [Fact]
    public async Task HealMissingPrimaryRestoresFlagWithoutDownload()
    {
        // Broken post-fix state: rows exist, none primary, and the app is
        // inside its recheck window. The heal must rebuild the flag from the
        // stored rows without any network access.
        var (enricher, github, downloads, _, _) = HappyPath();
        var app = NewApp("primary-heal", "Primary Heal", "https://github.com/example/primaryheal");
        app.LastCheckedAt = T0;
        AddDownload(app, SourceKind.GitHub, "https://cdn.example/heal.apk",
            versionCode: 42, sigSha256: SignerOutputA, primary: false);
        await _db.SaveChangesAsync();

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.SkippedFresh, result.Outcome);
        Assert.True(_db.Downloads.Local.Single(d => d.AppId == app.Id).IsPrimary);
        Assert.Equal(0, github.Calls + downloads.Calls);
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
    public async Task EnrichesLinksheetFromTheNightlyRepo()
    {
        const string assetUrl =
            "https://github.com/LinkSheet/nightly/releases/download/nightly-2026091203/LinkSheet-2026-09-12T08_25_09-nightly-2026091203-foss-nightly.apk";
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var requested = new List<string>();
        var github = new StubHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            requested.Add(uri);

            // The list links LinkSheet/LinkSheet; releases live in the nightly repo.
            return uri.Contains("/releases", StringComparison.Ordinal)
                ? JsonReleases(ReleaseJson("nightly-2026091203", "nightly.apk", assetUrl, zip.Length))
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var downloads = new StubHandler(request =>
        {
            Assert.Equal(assetUrl, request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "141"));
        var app = NewApp("linksheet", "LinkSheet", "https://github.com/LinkSheet/LinkSheet");

        var result = await BuildEnricher(github, downloads, aapt2).EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Contains(requested,
            u => u == "https://api.github.com/repos/LinkSheet/nightly/releases?per_page=100");
        Assert.DoesNotContain(requested, u => u.Contains("/repos/LinkSheet/LinkSheet", StringComparison.Ordinal));
        Assert.Equal(Availability.DirectApk, app.Availability);
        var primary = Primary(app);
        Assert.Equal(SourceKind.GitHub, primary.Source);
        Assert.Equal(assetUrl, primary.ApkUrl);
        Assert.Equal("nightly-2026091203", primary.ReleaseTag);
    }

    [Fact]
    public async Task EnrichesHlbmergeFromGitCode()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var github = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("/readme", StringComparison.Ordinal)
            ? ReadmeJson("# HLBmerge\n")
            : throw new InvalidOperationException("must not call GitHub"));
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
        Assert.Equal("v2.0.5", primary.ReleaseTag);
        Assert.Equal("\"gc-etag\"", app.EnrichEtag);
        Assert.NotNull(app.IconHash);
        Assert.Equal("# HLBmerge\n", app.FullDescription);
        Assert.Equal("https://raw.githubusercontent.com/molihuan/hlbmerge_flutter/HEAD/README.md", app.ReadmeUrl);
    }

    [Fact]
    public async Task GitCodeUnchangedAssetSkipsDownload()
    {
        var apk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var url = "https://gitcode.com/bigmolihuan/hlbmerge_flutter/releases/download/v2.0.5/app-arm64-v8a-release.apk";
        var github = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("/readme", StringComparison.Ordinal)
            ? ReadmeJson("# HLBmerge\n")
            : throw new InvalidOperationException("must not call GitHub"));
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
        Assert.Equal("v2.0.5", Primary(app).ReleaseTag);
    }

    [Fact]
    public async Task GitCodeWithoutApkAssetsFails()
    {
        var github = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("/readme", StringComparison.Ordinal)
            ? ReadmeJson("# HLBmerge\n")
            : throw new InvalidOperationException("must not call GitHub"));
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

    // Index matching the canned badging (com.example.app, v42).
    private const string VariantIndexV2Json = """
        {
          "packages": {
            "com.example.app": {
              "versions": {
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa": {
                  "file": {
                    "name": "/com.example.app_42.apk",
                    "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "size": 7654321
                  },
                  "manifest": {
                    "versionCode": 42,
                    "versionName": "4.2",
                    "usesSdk": { "minSdkVersion": 26 }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>Forge primary + F-Droid variant, downloads routed by host.</summary>
    private (AppEnricher Enricher, StubHandler Downloads) ForgeWithVariant(
        byte[] primaryZip,
        byte[] variantZip,
        FakeSignerRunner? signer = null,
        string indexV2Json = VariantIndexV2Json,
        bool variantDownload404 = false)
    {
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v4.2", "app-release.apk", "https://cdn.example/app.apk", primaryZip.Length)));
        var fdroid = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(indexV2Json),
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
        Assert.Equal("b10a8db164e0754105b7a99be72e3fe5", variant.SigMd5); // file truth wins over index metadata
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
        Assert.Null(variant.SigMd5); // the index carries no signer, so the variant stays unsigned
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
        // The index carries no signer, so the seeded row keeps its fingerprints
        // and nothing is refreshed from the index.
        Assert.Null(variant.SigMd5);
    }

    [Fact]
    public async Task ClearsVariantWhenDroppedFromIndex()
    {
        var primaryZip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Blue)));
        var (enricher, _) = ForgeWithVariant(primaryZip, new byte[] { 9 }, indexV2Json: EmptyIndexV2Json);
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
        Assert.Null(variant.SigMd5); // no index signer was available
    }

    [Fact]
    public async Task FdroidPrimaryAnalyzesApkOnVersionChange()
    {
        var orangeIcon = TestAssets.SolidPng(256, 256, Color.Orange);
        var zip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, orangeIcon));
        var fdroid = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(VariantIndexV2Json),
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
        Assert.Equal("b10a8db164e0754105b7a99be72e3fe5", primary.SigMd5); // analysis MD5
        Assert.Equal(IconProcessor.ProcessRawImage(orangeIcon)!.Sha256, app.IconHash);
    }

    [Fact]
    public async Task FdroidPrimaryRejectsPackageMismatch()
    {
        var zip = TestAssets.BuildApk(
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(256, 256, Color.Blue)));
        var fdroid = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(VariantIndexV2Json),
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
        Assert.Equal(2, downloads.Calls); // APK attempt + the single v2 icon URL
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
                    "description": { "en-US": "A <b>plain</b> summary of the app." },
                    "icon": { "en-US": { "name": "/com.example.app/en-US/icon.png" } },
                    "screenshots": {
                      "phone": {
                        "en-US": [
                          { "name": "/com.example.app/en-US/phoneScreenshots/00.png" }
                        ]
                      }
                    }
                  },
                  "versions": {
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef": {
                      "file": {
                        "name": "/com.example.app_20.apk",
                        "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                        "size": 1234567
                      },
                      "manifest": {
                        "versionCode": 20,
                        "versionName": "2.0",
                        "usesSdk": { "minSdkVersion": 26 }
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
    public async Task PlaySoleSourceBecomesPlayRedirect()
    {
        var enricher = NoNetworkEnricher();
        var play = "https://play.google.com/store/apps/details?id=net.dinglisch.android.taskerm";
        var app = NewApp("tasker", "Tasker", play);

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.AvatarFallback, result.Outcome);
        Assert.Equal(Availability.PlayRedirect, app.Availability);
        Assert.Equal(SourceKind.Play, app.SourceKind);
        Assert.Equal(play, app.StoreUrl);
        Assert.Null(app.ExcludedReason);
        Assert.Null(app.LastError);
        Assert.False(HasDownloads(app));
    }

    [Fact]
    public async Task ExcludeOverrideLeavesPlayRedirect()
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

    /// <summary>
    /// Two list entries whose source_url point at one repo, plus an enricher
    /// whose release ships both their packages. The izzy URLs make each root
    /// bind its own package, like the lemmy/mastodon redirect pair.
    /// </summary>
    private (AppEnricher Enricher, App Alpha, App Beta) SharedRepoPair()
    {
        var alphaApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var betaApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var json = ReleaseJsonMultiAssets(
            ("alpha-release.apk", "https://cdn.example/alpha.apk", alphaApk.Length),
            ("beta-release.apk", "https://cdn.example/beta.apk", betaApk.Length));
        var github = new StubHandler(_ => JsonReleases(json, "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("beta", StringComparison.Ordinal)
                ? betaApk
                : alphaApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == betaApk.Length
            ? TestAssets.CannedBadging(package: "com.example.beta", label: "Beta")
            : TestAssets.CannedBadging(package: "com.example.alpha", label: "Alpha"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var alpha = NewApp("alpha-redirect", "Alpha Redirect",
            "https://apt.izzysoft.de/fdroid/index/apk/com.example.alpha",
            sourceUrl: "https://github.com/example/shared-repo");
        var beta = NewApp("beta-redirect", "Beta Redirect",
            "https://apt.izzysoft.de/fdroid/index/apk/com.example.beta",
            sourceUrl: "https://github.com/example/shared-repo/");
        return (enricher, alpha, beta);
    }

    private App AddVariant(App root, string slug, string label, string packageName)
    {
        var variant = new App
        {
            Slug = slug,
            Name = root.Name,
            DisplayName = root.Name,
            Url = root.Url,
            Listing = Listing.Main,
            Type = AppType.App,
            RootAppId = root.Id,
            CategoryId = root.CategoryId,
            ApkLabel = label,
            PackageName = packageName,
            AddedAt = T0,
            UpdatedAt = T0,
        };
        _db.Apps.Add(variant);
        _db.SaveChanges();
        return variant;
    }

    [Fact]
    public async Task SiblingEntriesSharingARepoDoNotMirrorEachOthersPackages()
    {
        var (enricher, alpha, beta) = SharedRepoPair();

        // Alpha's first pass runs before beta is enriched, so beta's package
        // is not a known claim yet and a mirror variant appears.
        await enricher.EnrichAsync(alpha, T0);
        await _db.SaveChangesAsync();
        Assert.Single(_db.Apps.Local, a => a.RootAppId == alpha.Id);

        // Beta's pass resolves its own package and yields the alpha group.
        Age(beta);
        await enricher.EnrichAsync(beta, T0);
        await _db.SaveChangesAsync();
        Assert.Empty(await _db.Apps.Where(a => a.RootAppId == beta.Id).ToListAsync());

        // Alpha's next pass prunes the mirror now that beta owns the package.
        Age(alpha);
        await enricher.EnrichAsync(alpha, T0);
        await _db.SaveChangesAsync();

        Assert.Equal("com.example.alpha", alpha.PackageName);
        Assert.Equal("com.example.beta", beta.PackageName);
        Assert.Empty(await _db.Apps.Where(a => a.RootAppId == alpha.Id).ToListAsync());
        Assert.Empty(await _db.Apps.Where(a => a.RootAppId == beta.Id).ToListAsync());
        Assert.Equal(2, await _db.Apps.CountAsync());
        Assert.Contains(await _db.RemovedApps.ToListAsync(), r => r.Slug == "com-example-beta");
    }

    [Fact]
    public async Task PrunesVariantServedBySiblingRootPackage()
    {
        var (enricher, alpha, beta) = SharedRepoPair();
        beta.PackageName = "com.example.beta";
        _db.SaveChanges();

        // A mirror variant the sibling's own row now owns.
        var mirror = AddVariant(alpha, "com-example-beta", "Beta", "com.example.beta");
        AddDownload(mirror, SourceKind.GitHub, "https://cdn.example/beta.apk",
            versionCode: 42, sigSha256: SignerOutputA);

        await enricher.EnrichAsync(alpha, T0);
        await _db.SaveChangesAsync();

        Assert.Empty(await _db.Apps.Where(a => a.RootAppId == alpha.Id).ToListAsync());
        Assert.Contains(await _db.RemovedApps.ToListAsync(), r => r.Slug == "com-example-beta");
        Assert.Equal("com.example.alpha", alpha.PackageName);
    }

    [Fact]
    public async Task SharedUnlistedPackageKeepsSingleOwnerAmongSiblings()
    {
        var alphaApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var betaApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var gammaApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(700, 700, Color.Red)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("alpha-release.apk", "https://cdn.example/alpha.apk", alphaApk.Length),
            ("beta-release.apk", "https://cdn.example/beta.apk", betaApk.Length),
            ("gamma-release.apk", "https://cdn.example/gamma.apk", gammaApk.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var bytes = path.Contains("gamma", StringComparison.Ordinal)
                ? gammaApk
                : path.Contains("beta", StringComparison.Ordinal)
                    ? betaApk
                    : alphaApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path =>
        {
            var length = new FileInfo(path).Length;
            return length == gammaApk.Length
                ? TestAssets.CannedBadging(package: "com.example.gamma", label: "Gamma")
                : length == betaApk.Length
                    ? TestAssets.CannedBadging(package: "com.example.beta", label: "Beta")
                    : TestAssets.CannedBadging(package: "com.example.alpha", label: "Alpha");
        });
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var alpha = NewApp("alpha-redirect", "Alpha Redirect",
            "https://apt.izzysoft.de/fdroid/index/apk/com.example.alpha",
            sourceUrl: "https://github.com/example/shared-repo");
        var beta = NewApp("beta-redirect", "Beta Redirect",
            "https://apt.izzysoft.de/fdroid/index/apk/com.example.beta",
            sourceUrl: "https://github.com/example/shared-repo");
        alpha.PackageName = "com.example.alpha";
        beta.PackageName = "com.example.beta";
        _db.SaveChanges();

        // Both passes previously mirrored the third package, so both roots
        // already carry a gamma variant when the fix lands.
        var alphaGamma = AddVariant(alpha, "com-example-gamma", "Gamma", "com.example.gamma");
        var betaGamma = AddVariant(beta, "com-example-gamma-2", "Gamma", "com.example.gamma");

        await enricher.EnrichAsync(beta, T0);
        await _db.SaveChangesAsync();

        var gamma = Assert.Single(await _db.Apps.Where(a => a.PackageName == "com.example.gamma").ToListAsync());
        Assert.Equal(alpha.Id, gamma.RootAppId);
        Assert.Contains(await _db.RemovedApps.ToListAsync(), r => r.Slug == betaGamma.Slug);
        Assert.Empty(await _db.Apps.Where(a => a.RootAppId == beta.Id).ToListAsync());

        // Alpha's pass agrees on the owner and keeps its variant.
        Age(alpha);
        await enricher.EnrichAsync(alpha, T0);
        await _db.SaveChangesAsync();

        gamma = Assert.Single(await _db.Apps.Where(a => a.PackageName == "com.example.gamma").ToListAsync());
        Assert.Equal(alphaGamma.Id, gamma.Id);
        Assert.Equal(alpha.Id, gamma.RootAppId);
    }

    [Fact]
    public async Task KeepsVariantWhosePackageIsOwnedByNonSibling()
    {
        var alphaApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var pluginApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("alpha-release.apk", "https://cdn.example/alpha.apk", alphaApk.Length),
            ("plugin-release.apk", "https://cdn.example/plugin.apk", pluginApk.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("plugin", StringComparison.Ordinal)
                ? pluginApk
                : alphaApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == pluginApk.Length
            ? TestAssets.CannedBadging(package: "com.example.plugin", label: "Plugin")
            : TestAssets.CannedBadging(package: "com.example.alpha", label: "Alpha"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var alpha = NewApp("alpha-redirect", "Alpha Redirect",
            "https://apt.izzysoft.de/fdroid/index/apk/com.example.alpha",
            sourceUrl: "https://github.com/example/shared-repo");
        // An excluded entry on the same repo must not claim the package, and
        // an unrelated root on another repo is not a sibling at all.
        var excluded = NewApp("beta-redirect", "Beta Redirect",
            "https://apt.izzysoft.de/fdroid/index/apk/com.example.beta",
            sourceUrl: "https://github.com/example/shared-repo");
        excluded.Availability = Availability.Excluded;
        excluded.PackageName = "com.example.plugin";
        var unrelated = NewApp("plugin-owner", "Plugin Owner", "https://github.com/example/plugin-owner");
        unrelated.PackageName = "com.example.plugin";
        _db.SaveChanges();

        await enricher.EnrichAsync(alpha, T0);
        await _db.SaveChangesAsync();

        Assert.Contains(await _db.Apps.Where(a => a.RootAppId == alpha.Id).ToListAsync(),
            v => v.PackageName == "com.example.plugin");
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

    [Fact]
    public async Task UnchangedReleaseStampsVariantsFresh()
    {
        var mainApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var pluginApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("app-release.apk", "https://cdn.example/app.apk", mainApk.Length),
            ("plugin-release.apk", "https://cdn.example/plugin.apk", pluginApk.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("plugin", StringComparison.Ordinal)
                ? pluginApk
                : mainApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == pluginApk.Length
            ? TestAssets.CannedBadging(package: "com.example.plugin", label: "Plugin")
            : TestAssets.CannedBadging(package: "com.example.app", label: "Example"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("variant-freshness", "Variant Freshness", "https://github.com/example/variant-freshness");

        await enricher.EnrichAsync(app, T0);
        var variant = _db.Apps.Local.Single(a => a.RootAppId == app.Id);

        // A later pass with the release unchanged must refresh the variant too:
        // it is never selected directly, only through the root's pass.
        Age(app);
        Age(variant);
        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(2, downloads.Calls); // second pass skipped both APKs
        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.Equal(T0, app.LastCheckedAt);
        Assert.Equal(T0, variant.LastCheckedAt);
    }

    [Fact]
    public async Task UnchangedReleaseHealsVariantWithoutPrimary()
    {
        var mainApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var pluginApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(600, 600, Color.Green)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("app-release.apk", "https://cdn.example/app.apk", mainApk.Length),
            ("plugin-release.apk", "https://cdn.example/plugin.apk", pluginApk.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("plugin", StringComparison.Ordinal)
                ? pluginApk
                : mainApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == pluginApk.Length
            ? TestAssets.CannedBadging(package: "com.example.plugin", label: "Plugin")
            : TestAssets.CannedBadging(package: "com.example.app", label: "Example"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("variant-primary-heal", "Variant Primary Heal", "https://github.com/example/variant-primary-heal");

        await enricher.EnrichAsync(app, T0);
        var variant = _db.Apps.Local.Single(a => a.RootAppId == app.Id);
        var variantDownload = _db.Downloads.Local.Single(d => d.AppId == variant.Id);
        variantDownload.IsPrimary = false;
        await _db.SaveChangesAsync();

        // The variant is never selected directly, so an unchanged root pass
        // must still repair its missing primary flag.
        Age(app);
        Age(variant);
        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.UpToDate, result.Outcome);
        Assert.True(variantDownload.IsPrimary);
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

    [Fact]
    public async Task ExcludedPackageIsDroppedAndRootRebindsToSurvivor()
    {
        var dropInApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var normalApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(640, 640, Color.Green)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("shizuku-plus-drop-in.apk", "https://cdn.example/shizuku-plus-drop-in.apk", dropInApk.Length),
            ("shizuku-plus.apk", "https://cdn.example/shizuku-plus.apk", normalApk.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("drop-in", StringComparison.Ordinal)
                ? dropInApk
                : normalApk;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(path => new FileInfo(path).Length == dropInApk.Length
            ? TestAssets.CannedBadging(package: "moe.shizuku.privileged.api", label: "Shizuku")
            : TestAssets.CannedBadging(package: "af.shizuku.plus.api", label: "Shizuku+"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("shizukuplus", "ShizukuPlus", "https://github.com/thejaustin/ShizukuPlus");
        app.PackageName = "moe.shizuku.privileged.api";
        app.ApkLabel = "Shizuku";
        _db.SaveChanges();

        // A legacy variant row for the normal APK, as created before the fold.
        var variant = new App
        {
            Slug = "af-shizuku-plus-api",
            Name = "ShizukuPlus",
            Url = "https://github.com/thejaustin/ShizukuPlus",
            Listing = Listing.Main,
            Type = AppType.App,
            RootAppId = app.Id,
            CategoryId = app.CategoryId,
            ApkLabel = "Shizuku+",
            PackageName = "af.shizuku.plus.api",
            AddedAt = T0,
            UpdatedAt = T0,
        };
        _db.Apps.Add(variant);
        _db.SaveChanges();
        AddDownload(app, SourceKind.GitHub, "https://cdn.example/legacy-drop-in.apk",
            versionCode: 2606, sigSha256: "980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9",
            packageName: "moe.shizuku.privileged.api");
        AddDownload(variant, SourceKind.GitHub, "https://cdn.example/legacy-plus.apk",
            versionCode: 2606, sigSha256: "980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9",
            packageName: "af.shizuku.plus.api");

        _db.AppDownloadExclusions.Add(new AppDownloadExclusion
        {
            AppSlug = "shizukuplus",
            PackageName = "moe.shizuku.privileged.api",
            Note = "Drop-in shares the real Shizuku package",
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        await _db.SaveChangesAsync();

        await enricher.EnrichAsync(app, T0);
        await _db.SaveChangesAsync();

        // The drop-in is gone; the root now serves the normal package and the
        // redundant variant was folded and tombstoned.
        Assert.Equal("af.shizuku.plus.api", app.PackageName);
        Assert.Equal("Shizuku+", app.ApkLabel);
        Assert.Empty(await _db.Apps.Where(a => a.RootAppId == app.Id).ToListAsync());
        Assert.Contains(await _db.RemovedApps.ToListAsync(), r => r.Slug == "af-shizuku-plus-api");

        var candidates = await _db.Downloads.Where(d => d.AppId == app.Id).ToListAsync();
        Assert.Single(candidates);
        Assert.Equal("af.shizuku.plus.api", candidates[0].PackageName);
        Assert.True(candidates[0].IsPrimary);
        Assert.DoesNotContain(await _db.Downloads.ToListAsync(),
            d => d.PackageName == "moe.shizuku.privileged.api");

        // The excluded drop-in stays remembered as a known artifact.
        Assert.Contains(candidates[0].AnalyzedArtifacts,
            entry => entry.EndsWith(" https://cdn.example/shizuku-plus-drop-in.apk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExcludedArtifactIsNotReappliedWhenOnlyItIsReanalyzed()
    {
        // After a rebind the surviving sibling sits in storage, so the extras
        // loop skips it and a later pass re-analyzes only the excluded
        // artifact. Re-applying that lone analysis would silently undo the
        // operator exclusion, so the stored state must win.
        var dropInApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var normalApk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(640, 640, Color.Green)));
        var github = new StubHandler(_ => JsonReleases(ReleaseJsonMultiAssets(
            ("shizuku-plus-drop-in.apk", "https://cdn.example/shizuku-plus-drop-in.apk", dropInApk.Length),
            ("shizuku-plus.apk", "https://cdn.example/shizuku-plus.apk", normalApk.Length)), "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(dropInApk),
        });
        var aapt2 = new FakeAapt2Runner(_ =>
            TestAssets.CannedBadging(package: "moe.shizuku.privileged.api", label: "Shizuku"));
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("shizukuplus", "ShizukuPlus", "https://github.com/thejaustin/ShizukuPlus");
        app.PackageName = "af.shizuku.plus.api";
        app.ApkLabel = "Shizuku+";
        _db.SaveChanges();

        var survivor = AddDownload(app, SourceKind.GitHub, "https://cdn.example/shizuku-plus.apk",
            versionCode: 2606, sigSha256: "980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9",
            packageName: "af.shizuku.plus.api");

        _db.AppDownloadExclusions.Add(new AppDownloadExclusion
        {
            AppSlug = "shizukuplus",
            PackageName = "moe.shizuku.privileged.api",
            Note = "Drop-in shares the real Shizuku package",
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        await _db.SaveChangesAsync();

        await enricher.EnrichAsync(app, T0);
        await _db.SaveChangesAsync();

        Assert.Equal("af.shizuku.plus.api", app.PackageName);
        Assert.DoesNotContain(await _db.Downloads.ToListAsync(),
            d => d.PackageName == "moe.shizuku.privileged.api");
        var candidates = await _db.Downloads.Where(d => d.AppId == app.Id).ToListAsync();
        Assert.Single(candidates);
        Assert.Equal(survivor.Id, candidates[0].Id);
        Assert.True(candidates[0].IsPrimary);

        // The dropped drop-in must stay remembered: the fast-path poll counts
        // analyzed artifacts as recorded, and forgetting it would force this
        // app on every pass although enrichment never serves it.
        Assert.Contains(candidates[0].AnalyzedArtifacts,
            entry => entry.EndsWith(" https://cdn.example/shizuku-plus-drop-in.apk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExcludingEveryPackageLeavesAppUnchanged()
    {
        var apk = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app-release.apk", "https://cdn.example/app.apk", apk.Length), "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(apk),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging());
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA));
        var app = NewApp("guarded", "Guarded", "https://github.com/example/guarded");
        _db.AppDownloadExclusions.Add(new AppDownloadExclusion
        {
            AppSlug = "guarded",
            PackageName = "com.example.app",
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        await _db.SaveChangesAsync();

        var result = await enricher.EnrichAsync(app, T0);

        // An operator typo must never empty a row: with every package excluded
        // the release is applied unchanged.
        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal("com.example.app", app.PackageName);
        Assert.Contains(_db.Downloads.Local, d => d.AppId == app.Id && d.PackageName == "com.example.app");
    }

    [Fact]
    public async Task SameVersionTwinsAreAnalyzedOnceAndRemembered()
    {
        var zip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        const string releaseUrl = "https://cdn.example/app-release.apk";
        const string debugUrl = "https://cdn.example/app-debug.apk";
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJsonMultiAssets(("app-release.apk", releaseUrl, zip.Length), ("app-debug.apk", debugUrl, zip.Length)),
            "\"rel-etag\""));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "42"));
        var queue = new FakeUsageQueue();
        var enricher = BuildEnricher(github, downloads, aapt2, signer: new FakeSignerRunner(_ => SignerOutputA), usageQueue: queue);
        var app = NewApp("twins", "Twins", "https://github.com/example/twins");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(2, downloads.Calls);
        Assert.Equal(2, aapt2.Calls);
        var row = Assert.Single(_db.Downloads.Local.Where(d => d.AppId == app.Id).ToList());
        Assert.Equal(releaseUrl, row.ApkUrl);
        Assert.Contains(row.AnalyzedArtifacts, entry => entry.EndsWith($" {debugUrl}", StringComparison.Ordinal));
        var call = Assert.Single(queue.Calls);
        Assert.True(call.FirstAnalysis);
        Assert.False(call.ArtifactChanged);

        Age(app);
        var downloadsSecond = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip),
        });
        var aapt2Second = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "42"));
        var second = BuildEnricher(github, downloadsSecond, aapt2Second, signer: new FakeSignerRunner(_ => SignerOutputA), usageQueue: queue);

        var secondResult = await second.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.UpToDate, secondResult.Outcome);
        Assert.Equal(0, downloadsSecond.Calls);
        Assert.Equal(0, aapt2Second.Calls);
        Assert.Single(queue.Calls);
    }

    [Fact]
    public async Task ReleaseApkBeatsDebugSiblingWithDifferentSigner()
    {
        var releaseZip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var debugZip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Red)));
        const string releaseUrl = "https://cdn.example/app-release.apk";
        const string debugUrl = "https://cdn.example/app-debug.apk";
        var github = new StubHandler(_ => JsonReleases(
            ReleaseJsonMultiAssets(("app-release.apk", releaseUrl, releaseZip.Length), ("app-debug.apk", debugUrl, debugZip.Length)),
            "\"rel-etag\""));
        var downloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("release") ? releaseZip : debugZip;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var aapt2 = new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "42"));
        // The two assets carry different signing certs, so they land in
        // separate rows and the old version/ABI/source tie-break served the
        // debug sibling that was analyzed last.
        var signerCalls = 0;
        var signer = new FakeSignerRunner(_ => signerCalls++ == 0 ? SignerOutputA : SignerOutputB);
        var enricher = BuildEnricher(github, downloads, aapt2, signer: signer);
        var app = NewApp("siblings", "Siblings", "https://github.com/example/siblings");

        var result = await enricher.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(2, downloads.Calls);
        Assert.Equal(2, _db.Downloads.Local.Count(d => d.AppId == app.Id));
        var primary = Primary(app);
        Assert.Equal(releaseUrl, primary.ApkUrl);
        Assert.Equal(
            "980ceb20fd248b13eb6e224d73b3dfcd722ab120dfa6632ae8528e7be1cfd6c9",
            primary.SigSha256);
    }

    [Fact]
    public async Task ReleaseLikeTwinHealsADebugIncumbent()
    {
        var releaseZip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var debugZip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Red)));
        const string releaseUrl = "https://cdn.example/app-release.apk";
        const string debugUrl = "https://cdn.example/app-debug.apk";
        var app = NewApp("heal", "Heal", "https://github.com/example/heal");

        var firstGithub = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app-debug.apk", debugUrl, debugZip.Length), "\"rel-etag\""));
        var firstDownloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(debugZip),
        });
        var queue = new FakeUsageQueue();
        var first = BuildEnricher(firstGithub, firstDownloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "42")),
            signer: new FakeSignerRunner(_ => SignerOutputA), usageQueue: queue);

        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        Assert.Equal(debugUrl, Primary(app).ApkUrl);

        Age(app);
        var secondGithub = new StubHandler(_ => JsonReleases(
            ReleaseJsonMultiAssets(("app-release.apk", releaseUrl, releaseZip.Length), ("app-debug.apk", debugUrl, debugZip.Length)),
            "\"rel-etag\""));
        var secondDownloads = new StubHandler(request =>
        {
            var bytes = request.RequestUri!.AbsolutePath.Contains("release") ? releaseZip : debugZip;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        var second = BuildEnricher(secondGithub, secondDownloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "42")),
            signer: new FakeSignerRunner(_ => SignerOutputA), usageQueue: queue);

        var secondResult = await second.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, secondResult.Outcome);
        Assert.Equal(1, secondDownloads.Calls);
        var healed = Primary(app);
        Assert.Equal(releaseUrl, healed.ApkUrl);
        Assert.Contains(healed.AnalyzedArtifacts, entry => entry.EndsWith($" {debugUrl}", StringComparison.Ordinal));
        Assert.Equal(2, queue.Calls.Count);
        Assert.True(queue.Calls[1].ArtifactChanged);
    }

    [Fact]
    public async Task SameUrlReuploadReanalyzesAndQueues()
    {
        var firstZip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var secondZip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Red)));
        const string url = "https://cdn.example/app.apk";
        var app = NewApp("reupload", "Reupload", "https://github.com/example/reupload");

        var firstGithub = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app.apk", url, firstZip.Length, digest: $"sha256:{Sha256(firstZip)}"), "\"rel-etag\""));
        var firstDownloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(firstZip),
        });
        var queue = new FakeUsageQueue();
        var first = BuildEnricher(firstGithub, firstDownloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "42")),
            signer: new FakeSignerRunner(_ => SignerOutputA), usageQueue: queue);
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);

        Age(app);
        var secondGithub = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app.apk", url, secondZip.Length, digest: $"sha256:{Sha256(secondZip)}"), "\"rel-etag\""));
        var secondDownloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(secondZip),
        });
        var second = BuildEnricher(secondGithub, secondDownloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "42")),
            signer: new FakeSignerRunner(_ => SignerOutputA), usageQueue: queue);

        var result = await second.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        Assert.Equal(1, secondDownloads.Calls);
        Assert.Equal(2, queue.Calls.Count);
        Assert.True(queue.Calls[1].ArtifactChanged);
    }

    [Fact]
    public async Task VersionBumpClearsTwinMemory()
    {
        var firstZip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var secondZip = TestAssets.BuildApk((TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Red)));
        const string url = "https://cdn.example/app.apk";
        const string twinUrl = "https://cdn.example/app-debug.apk";
        var app = NewApp("bump", "Bump", "https://github.com/example/bump");

        var firstGithub = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app.apk", url, firstZip.Length, digest: $"sha256:{Sha256(firstZip)}"), "\"rel-etag\""));
        var firstDownloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(firstZip),
        });
        var queue = new FakeUsageQueue();
        var first = BuildEnricher(firstGithub, firstDownloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "42")),
            signer: new FakeSignerRunner(_ => SignerOutputA), usageQueue: queue);
        Assert.Equal(EnrichOutcome.Enriched, (await first.EnrichAsync(app, T0)).Outcome);
        var row = Primary(app);
        row.AnalyzedArtifacts.Add($"{new string('0', 64)} {twinUrl}");
        await _db.SaveChangesAsync();

        Age(app);
        var secondGithub = new StubHandler(_ => JsonReleases(
            ReleaseJson("v1.0", "app.apk", url, secondZip.Length, digest: $"sha256:{Sha256(secondZip)}"), "\"rel-etag\""));
        var secondDownloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(secondZip),
        });
        var second = BuildEnricher(secondGithub, secondDownloads,
            new FakeAapt2Runner(_ => TestAssets.CannedBadging(versionCode: "43")),
            signer: new FakeSignerRunner(_ => SignerOutputA), usageQueue: queue);

        var result = await second.EnrichAsync(app, T0);

        Assert.Equal(EnrichOutcome.Enriched, result.Outcome);
        var bumped = Primary(app);
        Assert.Equal(43L, bumped.VersionCode);
        Assert.DoesNotContain(bumped.AnalyzedArtifacts, entry => entry.EndsWith($" {twinUrl}", StringComparison.Ordinal));
        Assert.Single(queue.Calls, c => c.ArtifactChanged);
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
