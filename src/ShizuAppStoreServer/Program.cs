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
using ShizuAppStoreServer.Core.Jobs;
using ShizuAppStoreServer.Core.Sources;
using ShizuAppStoreServer.Core.Sync;
using ShizuAppStoreServer.Core.UsageAnalysis;
using ShizuAppStoreServer.Jobs;
using ShizuAppStoreServer.Sync;
using ShizuAppStoreServer.Tracking;

var builder = WebApplication.CreateBuilder(args);

// A worker exception must never take the API down. The hosted workers catch
// per iteration; this covers the gaps (for example a request-level timeout
// surfacing as OperationCanceledException) that would otherwise stop the host.
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Add services to the container.

builder.Services.AddControllers(o => o.Filters.Add<ResponsePoisonFilter>());
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

// Scraper poisoning (SPEC 2): known bad User-Agents get subtly wrong list
// and detail payloads; the output cache policy below keeps those responses
// from ever being stored or served.
var poisonOptions = builder.Configuration.GetSection("Poison").Get<PoisonOptions>() ?? new();
builder.Services.AddSingleton(poisonOptions);

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
        o.AddPolicy("apps-list", p => p.Expire(TimeSpan.FromSeconds(60)).SetVaryByQuery("*")
            .AddPolicy<SkipPoisonedRequestsPolicy>());
        o.AddPolicy("app-detail", p => p.Expire(TimeSpan.FromSeconds(60))
            .AddPolicy<SkipPoisonedRequestsPolicy>());
        o.AddPolicy("categories", p => p.Expire(TimeSpan.FromMinutes(5)));
        o.AddPolicy("changes", p => p.Expire(TimeSpan.FromSeconds(30)).SetVaryByQuery("*"));
        o.AddPolicy("issues", p => p.Expire(TimeSpan.FromSeconds(30)).SetVaryByQuery("*"));
        o.AddPolicy("meta", p => p.Expire(TimeSpan.FromSeconds(60)));
    });
}

var connectionString = builder.Configuration.GetConnectionString("Shizu");
builder.Services.AddDbContext<ShizuDbContext>(o => o.UseNpgsql(connectionString));

// Operator auth token (AdminOptions singleton so integration tests can swap
// it per suite; the admin controller takes it via DI). The legacy env name
// keeps existing deployments working until the secret is rotated.
var adminOptions = new AdminOptions
{
    Token = builder.Configuration["Admin:Token"]
        ?? Environment.GetEnvironmentVariable("SHIZU_ADMIN_TOKEN")
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
// Exodus tracker signatures for static code-signature detection. Singleton
// so the fetched catalog (a few hundred KB) survives per-app scopes and is
// refreshed at most once per interval; the DEX scan only runs when a catalog
// is injected.
builder.Services.AddHttpClient("exodus-trackers", client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<ITrackerCatalog>(sp => new ExodusTrackerCatalog(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("exodus-trackers"),
    enrichment));
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
// Renders run in a transient systemd user scope when enabled: the render
// JVMs (~1GB) charged to this unit crossed its MemoryHigh and stalled every
// API request in cgroup reclaim. A scope is bounded on its own, so a runaway
// render throttles or OOM-kills itself instead of taking the API down.
var renderScope = enrichment.IconRenderScope
    ? new RenderScope(
        enrichment.SystemdRunPath, enrichment.IconRenderScopeMemoryHigh,
        enrichment.IconRenderScopeMemoryMax, enrichment.IconRenderScopeSwapMax,
        enrichment.IconRenderScopeCpuQuota)
    : null;
builder.Services.AddSingleton<IPaparazziRenderer>(sp => new PaparazziRenderer(
    enrichment.GradlePath, enrichment.IconToolDir, enrichment.PaparazziTimeout,
    enrichment.IconRenderCpuAffinity, sp.GetRequiredService<ILogger<PaparazziRenderer>>(),
    renderScope));
builder.Services.AddSingleton<ILauncherIconService, LauncherIconService>();
builder.Services.AddSingleton<IGitRunner>(_ => new GitProcessRunner(enrichment.GitPath));
builder.Services.AddSingleton<IRepoScreenshotResolver>(sp => new RepoScreenshotResolver(
    sp.GetRequiredService<IGitRunner>(), enrichment,
    sp.GetRequiredService<ILogger<RepoScreenshotResolver>>()));
// AI source analysis: a queued worker reads each app's public repo through
// read-only tools and stores a one-line summary plus a markdown report. Off
// until UsageAnalysis:Enabled plus base URL and model are configured; the API
// key falls back to SHIZU_USAGE_ANALYSIS_KEY. The chat client factory is the
// test seam (a scripted IChatClient replaces the real OpenAI-compatible one).
var usageAnalysis = builder.Configuration.GetSection("UsageAnalysis").Get<UsageAnalysisOptions>() ?? new();
usageAnalysis.ApiKey ??= Environment.GetEnvironmentVariable("SHIZU_USAGE_ANALYSIS_KEY");
builder.Services.AddSingleton(usageAnalysis);
builder.Services.AddSingleton<IUsageAnalysisChatClientFactory, OpenAiUsageAnalysisChatClientFactory>();
builder.Services.AddSingleton<IUsageAnalysisAgent, UsageAnalysisAgent>();
builder.Services.AddSingleton<IRepoSnapshotProvider, GitRepoSnapshotProvider>();
builder.Services.AddSingleton<UsageContextBuilder>();
builder.Services.AddSingleton<IUsageAnalysisLogWriter, UsageAnalysisLogWriter>();
builder.Services.AddScoped<IUsageAnalysisQueue, UsageAnalysisQueue>();
builder.Services.AddScoped<IUsageAnalysisRunner, UsageAnalysisRunner>();
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
    sp.GetRequiredService<IRepoScreenshotResolver>(),
    sp.GetRequiredService<ITrackerCatalog>(),
    sp.GetRequiredService<IUsageAnalysisQueue>()));

// Sync engine (M6): fast loop + nightly full re-check in this same binary
// Workers resolve SyncService per pass; enrichment fans out over per-app scopes.
var syncOptions = builder.Configuration.GetSection("Sync").Get<SyncOptions>() ?? new();
builder.Services.AddSingleton(syncOptions);
builder.Services.AddSingleton<GitHistoryService>();
builder.Services.AddScoped<CatalogUpserter>();
builder.Services.AddScoped<IReleasePoller, ReleasePoller>();
// Job log: run rows plus the ordered event stream in Postgres (the stats
// dashboard reads both). The optional file sink writes the same stream as a
// human-readable second copy; the DB sink is first so run ids come from it.
var jobLogOptions = builder.Configuration.GetSection("Jobs").Get<JobLogOptions>() ?? new();
builder.Services.AddSingleton(jobLogOptions);
builder.Services.AddSingleton<DbJobSink>();
builder.Services.AddSingleton<IJobSink>(sp => sp.GetRequiredService<DbJobSink>());
if (!string.IsNullOrWhiteSpace(enrichment.RunLogPath))
{
    builder.Services.AddSingleton<IJobSink>(sp => new FileJobSink(
        enrichment.RunLogPath, sp.GetRequiredService<ILogger<FileJobSink>>()));
}

builder.Services.AddSingleton<IJobLog>(sp => new JobLog([.. sp.GetServices<IJobSink>()]));
builder.Services.AddHostedService<JobLogWorker>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<IEnrichmentRunner, EnrichmentRunner>();
builder.Services.AddSingleton<SyncGate>();
builder.Services.AddSingleton<SyncSignal>();
builder.Services.AddSingleton<SyncPassRunner>();
builder.Services.AddSingleton<ISyncPassRunner>(sp => sp.GetRequiredService<SyncPassRunner>());
builder.Services.AddSingleton<IconRefreshCoordinator>();
builder.Services.AddSingleton<ScreenshotRefreshCoordinator>();
builder.Services.AddHostedService<SyncWorker>();
builder.Services.AddHostedService<NightlyWorker>();
builder.Services.AddHostedService<UsageAnalysisWorker>();

// Anonymous client usage stats (aggregate per User-Agent + per UTC day).
// DB-only, no endpoint; the buffer keeps request latency unaffected.
var userAgentTracking = builder.Configuration.GetSection("UserAgentTracking").Get<UserAgentTrackingOptions>() ?? new();
builder.Services.AddSingleton(userAgentTracking);
builder.Services.AddSingleton<UserAgentTracker>();
builder.Services.AddHostedService<UserAgentTrackingWorker>();

// Non-client request log: every request whose User-Agent is not a ShizuStore
// release, on all paths (icons and admin included). DB-only, append-only.
var requestLog = builder.Configuration.GetSection("RequestLog").Get<RequestLogOptions>() ?? new();
builder.Services.AddSingleton(requestLog);
builder.Services.AddSingleton<RequestLogTracker>();
builder.Services.AddHostedService<RequestLogWorker>();

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
    var probeTasks = new List<Task<ToolProbeResult>>
    {
        ExternalToolProbe.CheckAsync("aapt2", enrichment.Aapt2Path, ["version"], TimeSpan.FromSeconds(30)),
        ExternalToolProbe.CheckAsync("apksigner", enrichment.ApksignerPath, ["--version"], TimeSpan.FromSeconds(60)),
        ExternalToolProbe.CheckAsync("gradle", enrichment.GradlePath, ["--version"], TimeSpan.FromMinutes(2)),
        ExternalToolProbe.CheckAsync("git", enrichment.GitPath, ["--version"], TimeSpan.FromSeconds(30)),
    };
    if (enrichment.IconRenderScope)
    {
        // Renders silently fall back to letter avatars when the scope cannot
        // start, so probe one real scope: this fails on a missing linger or
        // a unit that masks /run/user (ProtectHome=yes).
        probeTasks.Add(ExternalToolProbe.CheckAsync(
            "systemd-run (render scope)", enrichment.SystemdRunPath,
            ["--user", "--scope", "--quiet", "--collect",
             $"--unit=shizu-scope-probe-{Guid.NewGuid():N}",
             "-p", $"MemoryHigh={enrichment.IconRenderScopeMemoryHigh}",
             "-p", $"MemoryMax={enrichment.IconRenderScopeMemoryMax}",
             "-p", $"MemorySwapMax={enrichment.IconRenderScopeSwapMax}",
             "-p", $"CPUQuota={enrichment.IconRenderScopeCpuQuota}",
             "--", "/bin/true"],
            TimeSpan.FromSeconds(30)));
    }

    var probes = await Task.WhenAll(probeTasks);
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
            "see docs/server-setup.md (aapt2/apksigner/gradle sections; the render-scope entry needs a lingering " +
            "service user and ProtectHome=read-only so /run/user stays visible).");
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

if (!app.Environment.IsEnvironment("Testing"))
{
    // A Gradle daemon can outlive the service instance that started it and
    // keeps that instance's private /tmp mount, which is gone after a
    // restart. Paparazzi then cannot attach its ByteBuddy agent and every
    // render falls back to a letter avatar, so stop stale daemons before
    // the first render of this instance.
    await app.Services.GetRequiredService<IPaparazziRenderer>().StopGradleDaemonsAsync();
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

    // One-shots exit before the workers start, so the hosted flush worker is
    // not running: pump the DB sink here or events would never be persisted.
    // The session wraps the call so this run gets its own job row.
    var refreshPumpCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
    var refreshPump = app.Services.GetRequiredService<DbJobSink>().RunAsync(refreshPumpCts.Token);
    try
    {
        await using var refreshSession = app.Services.GetRequiredService<IJobLog>().Begin(new JobStart(
            JobKind.IconRefresh, JobTrigger.Cli, DateTimeOffset.UtcNow, Metadata: new { force = refreshForce }));
        var refresh = await refresher.RefreshIconsAsync(app.Lifetime.ApplicationStopping, refreshForce);
        app.Logger.LogInformation(
            "Icon refresh done{Force}: {Checked} checked, {Refreshed} refreshed, {Current} already current, {Failed} failed.",
            refreshForce ? " (forced)" : "",
            refresh.Checked, refresh.Refreshed, refresh.AlreadyCurrent, refresh.Failed);
        foreach (var error in refresh.Errors.Take(20))
        {
            app.Logger.LogWarning("Icon refresh failure: {Error}", error);
        }

        await refreshSession.FinishAsync(new JobFinish(
            JobStatus.Succeeded,
            Summary: $"{refresh.Checked} checked, {refresh.Refreshed} refreshed, "
                + $"{refresh.AlreadyCurrent} current, {refresh.Failed} failed",
            ItemsTotal: refresh.Checked,
            ItemsOk: refresh.Refreshed,
            ItemsSkipped: refresh.AlreadyCurrent,
            ItemsFailed: refresh.Failed,
            Metadata: new { force = refreshForce, errors = refresh.Errors.Count }),
            CancellationToken.None);

        return refresh.Failed > 0 ? 1 : 0;
    }
    finally
    {
        refreshPumpCts.Cancel();
        await refreshPump;
        refreshPumpCts.Dispose();
    }
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

    // Pump the job-log sink while this process owns the run; the hosted
    // flush worker only starts with the web host, which one-shots never run.
    var syncPumpCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
    var syncPump = app.Services.GetRequiredService<DbJobSink>().RunAsync(syncPumpCts.Token);
    try
    {
        using var syncScope = app.Services.CreateScope();
        var sync = syncScope.ServiceProvider.GetRequiredService<SyncService>();
        var syncResult = await sync.RunAsync(
            "cli", syncFull, DateTimeOffset.UtcNow, app.Lifetime.ApplicationStopping);
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
    finally
    {
        syncPumpCts.Cancel();
        await syncPump;
        syncPumpCts.Dispose();
    }
}

// Re-render stored analysis transcripts after a renderer change:
// --render-usage-logs [dir] rewrites every .html next to its .json source.
// Defaults to UsageAnalysis:LogPath; exits before the workers start.
if (args.Contains("--render-usage-logs"))
{
    var logFlag = Array.IndexOf(args, "--render-usage-logs");
    var logDir = logFlag + 1 < args.Length && !args[logFlag + 1].StartsWith("--", StringComparison.Ordinal)
        ? args[logFlag + 1]
        : usageAnalysis.LogPath ?? string.Empty;
    var renderedLogs = UsageAnalysisLogWriter.RenderDirectory(logDir, app.Logger);
    app.Logger.LogInformation("Rendered {Count} usage log page(s) in {Dir}.", renderedLogs, logDir);
    return renderedLogs > 0 ? 0 : 1;
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

// Before the output cache so cache hits and 429s are visible; records the
// response for requests that are not from a ShizuStore client.
if (requestLog.Enabled)
{
    app.UseMiddleware<NonClientRequestLoggingMiddleware>();
}

// Before the output cache so cache hits are counted; records after the
// response, which keeps 429s out of the stats.
if (userAgentTracking.Enabled)
{
    app.UseMiddleware<UserAgentTrackingMiddleware>();
}

// Output cache first so cache hits don't consume rate-limit permits.
if (apiOptions.EnableOutputCache)
{
    app.UseOutputCache();
}

app.UseRateLimiter();

app.UseAuthorization();

app.MapControllers();

// The API has no HTML surface; a browser hitting the bare host gets the
// project page instead of a 404.
app.MapGet("/", () => Results.Redirect("https://github.com/timschneeb/ShizuStore"));

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

