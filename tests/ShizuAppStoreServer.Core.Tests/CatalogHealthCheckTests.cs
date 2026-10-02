using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sync;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Hermetic quality-rule tests over seeded SQLite rows.</summary>
public sealed class CatalogHealthCheckTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ShizuDbContext _db;

    public CatalogHealthCheckTests()
    {
        _connection.Open();
        _db = new ShizuDbContext(new DbContextOptionsBuilder<ShizuDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Category Category()
    {
        var category = new Category { Name = "Audio", Slug = "audio", Section = CategorySection.Apps };
        _db.Categories.Add(category);
        return category;
    }

    private App HealthyApp(Category category, string slug) => new()
    {
        Name = slug,
        Slug = slug,
        Url = $"https://github.com/example/{slug}",
        Description = $"{slug} description",
        License = "MIT",
        Listing = Listing.Main,
        Type = AppType.App,
        Availability = Availability.LinkOnly,
        IconHash = "abc123",
        Category = category,
        AddedAt = T0,
        UpdatedAt = T0,
        LastCheckedAt = T0,
        PublishedAt = T0,
    };

    [Fact]
    public async Task HealthyAppHasNoIssues()
    {
        var category = Category();
        _db.Apps.Add(HealthyApp(category, "tuner"));
        await _db.SaveChangesAsync();

        var issues = await CatalogHealthCheck.CheckAsync(_db, T0.AddHours(1), Window);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task MissingFieldsAreFlagged()
    {
        var category = Category();
        var app = HealthyApp(category, "tuner");
        app.License = null;
        app.Description = string.Empty;
        app.IconHash = null;
        _db.Apps.Add(app);
        await _db.SaveChangesAsync();

        var rules = (await CatalogHealthCheck.CheckAsync(_db, T0.AddHours(1), Window))
            .Select(i => i.Rule)
            .ToHashSet();

        Assert.Contains(CatalogHealthCheck.MissingLicense, rules);
        Assert.Contains(CatalogHealthCheck.MissingDescription, rules);
        Assert.Contains(CatalogHealthCheck.MissingIcon, rules);
    }

    [Fact]
    public async Task DirectApkWithoutPackageOrPrimaryIsFlagged()
    {
        var category = Category();
        var app = HealthyApp(category, "tuner");
        app.Availability = Availability.DirectApk;
        app.PackageName = null;
        _db.Apps.Add(app);
        await _db.SaveChangesAsync();

        var rules = (await CatalogHealthCheck.CheckAsync(_db, T0.AddHours(1), Window))
            .Select(i => i.Rule)
            .ToHashSet();

        Assert.Contains(CatalogHealthCheck.MissingPackage, rules);
        Assert.Contains(CatalogHealthCheck.DirectApkWithoutPrimary, rules);
    }

    [Fact]
    public async Task DirectApkWithPrimaryDownloadIsClean()
    {
        var category = Category();
        var app = HealthyApp(category, "tuner");
        app.Availability = Availability.DirectApk;
        app.PackageName = "com.example.tuner";
        app.Downloads.Add(new AppDownload
        {
            Source = SourceKind.GitHub,
            ApkUrl = "https://example.com/tuner.apk",
            SigKey = "abc",
            IsPrimary = true,
            ResolvedAt = T0,
        });
        _db.Apps.Add(app);
        await _db.SaveChangesAsync();

        var rules = (await CatalogHealthCheck.CheckAsync(_db, T0.AddHours(1), Window))
            .Select(i => i.Rule)
            .ToHashSet();

        Assert.DoesNotContain(CatalogHealthCheck.MissingPackage, rules);
        Assert.DoesNotContain(CatalogHealthCheck.DirectApkWithoutPrimary, rules);
    }

    [Fact]
    public async Task BadUrlsAreFlagged()
    {
        var category = Category();
        var badEntry = HealthyApp(category, "badentry");
        badEntry.Url = "not a url";
        var badSource = HealthyApp(category, "badsource");
        badSource.SourceUrl = "ftp://example.com/code";
        _db.Apps.AddRange(badEntry, badSource);
        await _db.SaveChangesAsync();

        var issues = await CatalogHealthCheck.CheckAsync(_db, T0.AddHours(1), Window);

        Assert.Equal(2, issues.Count(i => i.Rule == CatalogHealthCheck.BadUrl));
    }

    [Fact]
    public async Task NeverCheckedAndStaleAreFlagged()
    {
        var category = Category();
        var fresh = HealthyApp(category, "fresh");
        var never = HealthyApp(category, "never");
        never.LastCheckedAt = null;
        var stale = HealthyApp(category, "stale");
        stale.LastCheckedAt = T0.AddHours(-49);
        _db.Apps.AddRange(fresh, never, stale);
        await _db.SaveChangesAsync();

        var issues = await CatalogHealthCheck.CheckAsync(_db, T0, Window);

        Assert.Contains(issues, i => i.Rule == CatalogHealthCheck.NeverChecked && i.Slug == "never");
        Assert.Contains(issues, i => i.Rule == CatalogHealthCheck.StaleCheck && i.Slug == "stale");
        Assert.DoesNotContain(issues, i => i.Slug == "fresh");
    }

    [Fact]
    public async Task ExcludedAppsAreSkipped()
    {
        var category = Category();
        var app = HealthyApp(category, "tuner");
        app.Availability = Availability.Excluded;
        app.License = null;
        app.IconHash = null;
        app.LastCheckedAt = null;
        _db.Apps.Add(app);
        await _db.SaveChangesAsync();

        var issues = await CatalogHealthCheck.CheckAsync(_db, T0.AddHours(1), Window);

        Assert.Empty(issues);
    }

    [Fact]
    public async Task DuplicatePackageFlagsEveryMember()
    {
        var category = Category();
        var first = HealthyApp(category, "island");
        first.PackageName = "com.oasisfeng.island";
        var second = HealthyApp(category, "com-oasisfeng-island");
        second.PackageName = "Com.Oasisfeng.Island";
        var excluded = HealthyApp(category, "island-excluded");
        excluded.Availability = Availability.Excluded;
        excluded.PackageName = "com.oasisfeng.island";
        var unique = HealthyApp(category, "tuner");
        unique.PackageName = "com.example.tuner";
        _db.Apps.AddRange(first, second, excluded, unique);
        await _db.SaveChangesAsync();

        var issues = await CatalogHealthCheck.CheckAsync(_db, T0.AddHours(1), Window);

        var duplicates = issues.Where(i => i.Rule == CatalogHealthCheck.DuplicatePackage).ToList();
        Assert.Equal(2, duplicates.Count);
        Assert.Contains(duplicates, i => i.Slug == "island" && i.Message.Contains("'com-oasisfeng-island'"));
        Assert.Contains(duplicates, i => i.Slug == "com-oasisfeng-island" && i.Message.Contains("'island'"));
        Assert.DoesNotContain(duplicates, i => i.Slug == "tuner");
    }

    [Fact]
    public async Task VersionAnomalyFlagsPrimaryOlderThanHistory()
    {
        var category = Category();
        var regressed = HealthyApp(category, "universal-installer");
        regressed.Availability = Availability.DirectApk;
        regressed.PackageName = "com.example.installer";
        regressed.Downloads.Add(new AppDownload
        {
            Source = SourceKind.GitHub,
            ApkUrl = "https://example.com/installer.apk",
            SigKey = "abc",
            IsPrimary = true,
            VersionCode = 42,
            VersionName = "1.0",
            ResolvedAt = T0,
        });
        regressed.Versions.Add(new AppVersion { VersionCode = 2032, VersionName = "2.0", DetectedAt = T0 });
        var clean = HealthyApp(category, "clean");
        clean.Availability = Availability.DirectApk;
        clean.PackageName = "com.example.clean";
        clean.Downloads.Add(new AppDownload
        {
            Source = SourceKind.GitHub,
            ApkUrl = "https://example.com/clean.apk",
            SigKey = "abc",
            IsPrimary = true,
            VersionCode = 7,
            VersionName = "1.0",
            ResolvedAt = T0,
        });
        clean.Versions.Add(new AppVersion { VersionCode = 7, VersionName = "1.0", DetectedAt = T0 });

        // A prerelease history row above the served stable is channel
        // history, not a skipped stable release.
        clean.Versions.Add(new AppVersion { VersionCode = 99, VersionName = "2.0-beta", IsPrerelease = true, DetectedAt = T0 });

        // Multi-ABI artifacts encode the ABI in the version code (an arm64
        // 4017 of the same 1.0 release); the same name is not a skip.
        clean.Versions.Add(new AppVersion { VersionCode = 4017, VersionName = "1.0", DetectedAt = T0 });
        _db.Apps.AddRange(regressed, clean);
        await _db.SaveChangesAsync();

        var issues = await CatalogHealthCheck.CheckAsync(_db, T0.AddHours(1), Window);

        var anomaly = Assert.Single(issues, i => i.Rule == CatalogHealthCheck.VersionAnomaly);
        Assert.Equal("universal-installer", anomaly.Slug);
        Assert.Contains("2032", anomaly.Message);
    }
}
