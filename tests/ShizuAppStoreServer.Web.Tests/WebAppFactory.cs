using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Web.Configuration;
using ShizuAppStoreServer.Web.Services;

namespace ShizuAppStoreServer.Web.Tests;

/// <summary>
/// In-memory storefront host: SQLite (shared open connection) instead of
/// Npgsql, a temp icon store and fixed assetlinks values. One instance per
/// test class; each test starts with <see cref="ResetAsync"/>.
/// </summary>
public sealed class WebAppFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public WebAppFactory()
    {
        _connection.Open();
        IconDir = Directory.CreateTempSubdirectory("shizu-web-icons-").FullName;
    }

    public string IconDir { get; }

    public const string PackageName = "me.timschneeberger.shizustore";

    public const string Fingerprint = "AA:BB:CC";

    public const string ApkUrl = "https://example.com/ShizuStore-test.apk";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            // AddDbContext registers options plus an options-action holder; the
            // Npgsql holder must go too or EF refuses to start with two providers.
            services.RemoveAll<DbContextOptions<ShizuDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ShizuDbContext>>();
            services.AddDbContext<ShizuDbContext>(o => o.UseSqlite(_connection));

            services.RemoveAll<IConfigureOptions<IconsOptions>>();
            services.Configure<IconsOptions>(o => o.StorePath = IconDir);

            services.RemoveAll<IConfigureOptions<AssetLinksOptions>>();
            services.Configure<AssetLinksOptions>(o =>
            {
                o.PackageName = PackageName;
                o.Fingerprints = [Fingerprint];
            });

            services.AddOutputCache(o => o.AddPolicy("pages", NoOutputCachePolicy.Instance));

            // The banner/nudge link comes from GitHub in production; stub it.
            services.RemoveAll<IAppReleaseProvider>();
            services.AddSingleton<IAppReleaseProvider>(new StubReleaseProvider(ApkUrl));
        });
    }

    /// <summary>Wipes the database and runs <paramref name="seed"/> in a fresh scope.</summary>
    public async Task ResetAsync(Action<ShizuDbContext> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        seed(db);
        await db.SaveChangesAsync();
    }

    public HttpClient NewClient() => CreateClient();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            if (Directory.Exists(IconDir))
            {
                Directory.Delete(IconDir, recursive: true);
            }
        }
    }
}

internal sealed class StubReleaseProvider(string apkUrl) : IAppReleaseProvider
{
    public Task<string> GetLatestApkUrlAsync(CancellationToken ct = default) =>
        Task.FromResult(apkUrl);
}
