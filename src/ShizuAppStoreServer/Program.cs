using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.History;
using ShizuAppStoreServer.Core.Sources;
using ShizuAppStoreServer.Core.Sync;
using ShizuAppStoreServer.Sync;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Public API behavior: fixed-window rate limit (~100 req/min/IP) +
// server-side output caching for GET /v1/*. Both come from the Api
// section; tests disable the cache and raise the limit.
var apiOptions = builder.Configuration.GetSection("Api").Get<ApiOptions>() ?? new();
builder.Services.AddSingleton(apiOptions);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Resolve ApiOptions per request (not captured) so integration tests
    // can swap the registration to vary the limit per suite.
    o.AddPolicy("api", httpContext =>
    {
        var limit = Math.Max(1,
            httpContext.RequestServices.GetRequiredService<ApiOptions>().RateLimitPerMinute);
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

if (apiOptions.EnableOutputCache)
{
    builder.Services.AddOutputCache(o =>
    {
        o.AddPolicy("apps-list", p => p.Expire(TimeSpan.FromSeconds(60)).SetVaryByQuery("*"));
        o.AddPolicy("app-detail", p => p.Expire(TimeSpan.FromSeconds(60)));
        o.AddPolicy("categories", p => p.Expire(TimeSpan.FromMinutes(5)));
        o.AddPolicy("changes", p => p.Expire(TimeSpan.FromSeconds(30)).SetVaryByQuery("*"));
        o.AddPolicy("meta", p => p.Expire(TimeSpan.FromSeconds(60)));
    });
}

var connectionString = builder.Configuration.GetConnectionString("Shizu");
builder.Services.AddDbContext<ShizuDbContext>(o => o.UseNpgsql(connectionString));

// Operator-auth secret (AdminOptions singleton so integration tests can
// swap it per suite; the admin controller takes it via DI).
var adminOptions = new AdminOptions
{
    HmacSecret = builder.Configuration["Admin:HmacSecret"]
        ?? Environment.GetEnvironmentVariable("SHIZU_ADMIN_SECRET"),
};
builder.Services.AddSingleton(adminOptions);

// Enricher (M3/M4/M8): GitHub + GitLab clients, F-Droid index provider, APK
// downloader, aapt2 + apksigner runners. The M6 workers resolve AppEnricher
// scope and fan out via BulkEnricher.
var enrichment = builder.Configuration.GetSection("Enrichment").Get<EnrichmentOptions>() ?? new();
enrichment.GitHubToken ??= Environment.GetEnvironmentVariable("SHIZU_GITHUB_TOKEN");
enrichment.GitLabToken ??= Environment.GetEnvironmentVariable("SHIZU_GITLAB_TOKEN");
builder.Services.AddSingleton(enrichment);
ConfigureEnrichmentClients(builder.Services, enrichment);
// Singleton (not scoped): the M6 loop enriches apps in parallel per-app
// scopes, so the index cache must outlive any one scope (thread-safe since M6).
builder.Services.AddSingleton<FdroidIndexProvider>();
builder.Services.AddHttpClient("apk-download",
    client => client.Timeout = enrichment.DownloadTimeout);
builder.Services.AddSingleton<IAapt2Runner>(_ => new Aapt2Runner(enrichment.Aapt2Path));
builder.Services.AddSingleton<IApkSignerRunner>(_ => new ApkSignerRunner(enrichment.ApksignerPath));
builder.Services.AddSingleton<IPaparazziRenderer>(_ => new PaparazziRenderer(
    enrichment.GradlePath, enrichment.IconToolDir, enrichment.PaparazziTimeout));
builder.Services.AddSingleton<ILauncherIconService, LauncherIconService>();
builder.Services.AddScoped<AppEnricher>(sp => new AppEnricher(
    sp.GetRequiredService<IGitHubReleaseClient>(),
    sp.GetRequiredService<IGitLabReleaseClient>(),
    sp.GetRequiredService<FdroidIndexProvider>(),
    sp.GetRequiredService<IAapt2Runner>(),
    sp.GetRequiredService<IApkSignerRunner>(),
    sp.GetRequiredService<ILauncherIconService>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("apk-download"),
    enrichment,
    sp.GetRequiredService<ShizuDbContext>(),
    sp.GetRequiredService<IInstafelReleaseClient>(),
    sp.GetRequiredService<IGitCodeReleaseClient>(),
    sp.GetRequiredService<IPlayStoreClient>(),
    sp.GetRequiredService<ILogger<AppEnricher>>()));

// Sync engine (M6): fast loop + nightly full re-check in this same binary
// (PLAN §0: single binary, single systemd service). Workers resolve
// SyncService per pass; enrichment fans out over per-app scopes.
var syncOptions = builder.Configuration.GetSection("Sync").Get<SyncOptions>() ?? new();
builder.Services.AddSingleton(syncOptions);
builder.Services.AddSingleton<GitHistoryService>();
builder.Services.AddScoped<CatalogUpserter>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<IEnrichmentRunner, EnrichmentRunner>();
builder.Services.AddSingleton<SyncGate>();
builder.Services.AddSingleton<SyncPassRunner>();
builder.Services.AddHostedService<SyncWorker>();
builder.Services.AddHostedService<NightlyWorker>();

var app = builder.Build();

// Fail fast when the enrichment toolchain is missing: without aapt2 (or
// apksigner + its JRE, gradle + its JRE for XML icons) the server would
// boot but never enrich. Skipped in the Testing environment so Web.Tests
// stay hermetic (no binaries needed).
if (!app.Environment.IsEnvironment("Testing"))
{
    var probes = await Task.WhenAll(
        ExternalToolProbe.CheckAsync("aapt2", enrichment.Aapt2Path, ["version"], TimeSpan.FromSeconds(30)),
        ExternalToolProbe.CheckAsync("apksigner", enrichment.ApksignerPath, ["--version"], TimeSpan.FromSeconds(60)),
        ExternalToolProbe.CheckAsync("gradle", enrichment.GradlePath, ["--version"], TimeSpan.FromMinutes(2)));
    foreach (var probe in probes)
    {
        if (probe.Available)
        {
            app.Logger.LogInformation("External tool {Name} OK ({Detail}) at {Path}", probe.Name, probe.Detail, probe.Path);
        }
        else
        {
            app.Logger.LogCritical("External tool {Name} unavailable at '{Path}': {Detail}", probe.Name, probe.Path, probe.Detail);
        }
    }

    var missing = probes.Where(p => !p.Available).ToList();
    if (missing.Count > 0)
    {
        throw new InvalidOperationException(
            $"Cannot start without required external tools ({string.Join(", ", missing.Select(m => $"{m.Name} at '{m.Path}'"))}); " +
            "see docs/server-setup.md (aapt2/apksigner/gradle sections).");
    }
}

// One-shot icon re-render (renderer upgrades, e.g. Paparazzi rollout):
// re-renders every recorded-APK icon, prints counts, exits before the
// workers start. Run with the server stopped (both processes write apps).
// --force rewrites + recounts every icon even when the fresh bytes match
// (catches self-consistent wrong files, e.g. swapped pairs).
if (args.Contains("--refresh-icons"))
{
    using var refreshScope = app.Services.CreateScope();
    var refresher = refreshScope.ServiceProvider.GetRequiredService<SyncService>();
    var refreshForce = args.Contains("--force");
    var refresh = await refresher.RefreshIconsAsync(app.Lifetime.ApplicationStopping, refreshForce);
    app.Logger.LogInformation(
        "Icon refresh done{Force}: {Checked} checked, {Refreshed} refreshed, {Current} already current, {Failed} failed.",
        refreshForce ? " (forced)" : "",
        refresh.Checked, refresh.Refreshed, refresh.AlreadyCurrent, refresh.Failed);
    foreach (var error in refresh.Errors.Take(20))
    {
        app.Logger.LogWarning("Icon refresh failure: {Error}", error);
    }

    return refresh.Failed > 0 ? 1 : 0;
}

// Configure the HTTP request pipeline.
app.MapOpenApi(); // Public API: clients may fetch the spec in any environment.
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
}

// Output cache first so cache hits don't consume rate-limit permits.
if (apiOptions.EnableOutputCache)
{
    app.UseOutputCache();
}

app.UseRateLimiter();

app.UseAuthorization();

app.MapControllers();

app.Run();

return 0;

// Public for WebApplicationFactory (integration tests).
public partial class Program
{
    /// <summary>
    /// Enrichment HTTP clients. The tokens live on <see cref="EnrichmentOptions"/>
    /// (config + SHIZU_*_TOKEN env fallback, see above) and MUST be applied
    /// here: the client ctors take an optional token string that DI cannot
    /// supply, so without this every forge call silently goes anonymous
    /// (found live 2026-09-12: mass 403s with a PAT configured).
    /// Covered by EnrichmentClientWiringTests.
    /// </summary>
    public static void ConfigureEnrichmentClients(IServiceCollection services, EnrichmentOptions enrichment)
    {
        services.AddHttpClient<IGitHubReleaseClient, GitHubReleaseClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            if (enrichment.GitHubToken is not null)
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", enrichment.GitHubToken);
            }
        });
        services.AddHttpClient<IGitLabReleaseClient, GitLabReleaseClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            if (enrichment.GitLabToken is not null)
            {
                client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", enrichment.GitLabToken);
            }
        });
        services.AddHttpClient<FdroidRepoClient>(
            client => client.Timeout = TimeSpan.FromSeconds(60));
        services.AddHttpClient<IInstafelReleaseClient, InstafelReleaseClient>(
            client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<IGitCodeReleaseClient, GitCodeReleaseClient>(
            client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<IPlayStoreClient, PlayStoreClient>(
            client => client.Timeout = TimeSpan.FromSeconds(30));
    }
}

