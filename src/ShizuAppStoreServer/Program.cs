using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
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

// Catalog JSON is large and repetitive, so compress it. Brotli first for
// clients that negotiate it (the Android client's OkHttp), gzip as fallback.
// Icons are already-compressed PNGs and stay out of the default MIME list.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(
    o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(
    o => o.Level = CompressionLevel.Fastest);

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
        o.AddPolicy("issues", p => p.Expire(TimeSpan.FromSeconds(30)).SetVaryByQuery("*"));
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
if (!string.IsNullOrWhiteSpace(enrichment.FdroidRepoBase))
{
    // f-droid.org throttles datacenter IPs to a few hundred KB/s, which
    // starves the 60MB index; production points this at a mirror instead.
    FdroidRepos.FDroidBase = enrichment.FdroidRepoBase.TrimEnd('/') + "/";
}

if (!string.IsNullOrWhiteSpace(enrichment.IzzyRepoBase))
{
    FdroidRepos.IzzyBase = enrichment.IzzyRepoBase.TrimEnd('/') + "/";
}

if (!string.IsNullOrWhiteSpace(enrichment.IzzyRepoBaseFallback))
{
    FdroidRepos.IzzyBaseFallback = enrichment.IzzyRepoBaseFallback.TrimEnd('/') + "/";
}

builder.Services.AddSingleton(enrichment);
ConfigureEnrichmentClients(builder.Services, enrichment);
// Singleton (not scoped): the M6 loop enriches apps in parallel per-app
// scopes, so the index cache must outlive any one scope (thread-safe since M6).
builder.Services.AddSingleton<FdroidIndexProvider>();
builder.Services.AddSingleton<IzzyStatsProvider>();
builder.Services.AddHttpClient("apk-download", client =>
{
    client.Timeout = enrichment.DownloadTimeout;
    // The FAU mirror censors .apk files for non-F-Droid clients (Google
    // SafeSearch flag), so mirror downloads need this agent.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("F-Droid");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    ConnectCallback = ConnectIPv4Async,
});
builder.Services.AddSingleton<IAapt2Runner>(_ => new Aapt2Runner(enrichment.Aapt2Path));
builder.Services.AddSingleton<IApkSignerRunner>(_ => new ApkSignerRunner(enrichment.ApksignerPath));
builder.Services.AddSingleton<IPaparazziRenderer>(sp => new PaparazziRenderer(
    enrichment.GradlePath, enrichment.IconToolDir, enrichment.PaparazziTimeout,
    enrichment.IconRenderCpuAffinity, sp.GetRequiredService<ILogger<PaparazziRenderer>>(),
    sp.GetRequiredService<IRunLog>()));
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
    sp.GetRequiredService<IGitCodeReleaseClient>(),
    sp.GetRequiredService<IPlayStoreClient>(),
    sp.GetRequiredService<IzzyStatsProvider>(),
    sp.GetRequiredService<ILogger<AppEnricher>>(),
    sp.GetRequiredService<IRunLog>()));

// Sync engine (M6): fast loop + nightly full re-check in this same binary
// Workers resolve SyncService per pass; enrichment fans out over per-app scopes.
var syncOptions = builder.Configuration.GetSection("Sync").Get<SyncOptions>() ?? new();
builder.Services.AddSingleton(syncOptions);
builder.Services.AddSingleton<GitHistoryService>();
builder.Services.AddScoped<CatalogUpserter>();
builder.Services.AddScoped<IReleasePoller, ReleasePoller>();
// Opt-in human-readable run log (one section per pass, one line per app).
builder.Services.AddSingleton<IRunLog>(sp => string.IsNullOrWhiteSpace(enrichment.RunLogPath)
    ? NullRunLog.Instance
    : new FileRunLog(enrichment.RunLogPath, sp.GetRequiredService<ILogger<FileRunLog>>()));
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
// The fast-loop release poll costs one feed call per app per pass, which
// needs the 5000/hr authenticated GitHub limit; without a PAT it stays
// off and fast passes enrich due-only apps.
if (string.IsNullOrEmpty(enrichment.GitHubToken))
{
    app.Logger.LogWarning(
        "Enrichment:GitHubToken (SHIZU_GITHUB_TOKEN) is not set: the fast-loop release poll is disabled.");
}

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

    // The Paparazzi renderer runs Gradle with `-p <IconToolDir>` relative to
    // the process CWD, so a relative default only resolves when the server is
    // started from the repo root. Booting with a missing tool dir does not
    // fail enrichment; it silently falls back to raster icons and letter
    // avatars, overwriting previously rendered adaptive icons. Refuse to boot.
    var iconToolDir = Path.GetFullPath(enrichment.IconToolDir);
    if (!Directory.Exists(iconToolDir))
    {
        throw new InvalidOperationException(
            $"Enrichment:IconToolDir '{enrichment.IconToolDir}' (resolved to '{iconToolDir}') does not exist. " +
            "Set Enrichment__IconToolDir to the absolute path of tools/icon-render, otherwise adaptive icons cannot " +
            "render and enrichment would degrade them to rasters or avatars.");
    }
}

// One-shot icon re-render:
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

// One-shot sync pass, for operator runs that must not wait for the nightly.
// --full selects every app (like the nightly); --skip-apk forces
// Enrichment:SkipApkAnalysis so release metadata and changelogs refresh
// without any APK download. Run with the server stopped (same rule as
// --refresh-icons: both processes write apps).
if (args.Contains("--sync-once"))
{
    var syncFull = args.Contains("--full");
    if (args.Contains("--skip-apk"))
    {
        enrichment.SkipApkAnalysis = true;
    }

    using var syncScope = app.Services.CreateScope();
    var sync = syncScope.ServiceProvider.GetRequiredService<SyncService>();
    var syncResult = await sync.RunAsync(
        "manual", syncFull, DateTimeOffset.UtcNow, app.Lifetime.ApplicationStopping);
    app.Logger.LogInformation(
        "Manual sync done ({Mode}{SkipApk}): added={Added} updated={Updated} removed={Removed} enriched={Enriched} upToDate={UpToDate} failed={Failed} warnings={Warnings} skipped={Skipped} head={Head}",
        syncFull ? "full" : "fast",
        enrichment.SkipApkAnalysis ? ", skip-apk" : "",
        syncResult.Added, syncResult.Updated, syncResult.Removed, syncResult.Enriched,
        syncResult.UpToDate, syncResult.Failed, syncResult.ParseWarnings, syncResult.Skipped,
        syncResult.HeadCommit ?? "(none)");
    foreach (var failure in syncResult.FailedMessages.Take(20))
    {
        app.Logger.LogWarning("Manual sync failure: {Failure}", failure);
    }

    return syncResult.Failed > 0 ? 1 : 0;
}

// Configure the HTTP request pipeline.
app.MapOpenApi(); // Public API: clients may fetch the spec in any environment.
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
}

// Compression outside the output cache: cached bodies stay uncompressed and
// are compressed per request on the way out.
app.UseResponseCompression();

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

public partial class Program
{
    /// <summary>
    /// Enrichment HTTP clients
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
        services.AddHttpClient<FdroidRepoClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            // The FAU mirror censors .apk files unless the client looks like
            // F-Droid; index files are unaffected but the agent is harmless.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("F-Droid");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectCallback = ConnectIPv4Async,
        });
        services.AddHttpClient<IzzyStatsClient>(
            client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<IGitCodeReleaseClient, GitCodeReleaseClient>(
            client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<IPlayStoreClient, PlayStoreClient>(
            client => client.Timeout = TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// f-droid.org serves its index over IPv6 at ~170KB/s while IPv4 is
    /// orders of magnitude faster, so resolve A records only and walk them
    /// until one connects. Falls back to nothing: an all-IPv4 host list that
    /// refuses every address fails the request instead of hanging.
    /// </summary>
    private static async ValueTask<Stream> ConnectIPv4Async(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host, AddressFamily.InterNetwork, ct);
        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"No IPv4 address for {context.DnsEndPoint.Host}.");
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                last = ex;
                socket.Dispose();
            }
        }

        throw new HttpRequestException($"Could not connect to {context.DnsEndPoint.Host}.", last);
    }
}

