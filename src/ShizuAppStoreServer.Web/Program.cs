using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Sentry;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Web.Configuration;
using ShizuAppStoreServer.Web.Rendering;
using ShizuAppStoreServer.Web.Services;
using ShizuAppStoreServer.Web.Tracking;

var builder = WebApplication.CreateBuilder(args);

// Sentry: errors, request traces and structured logs. The storefront shares
// the API's project, split by the service tag. The DSN is server-side only
// (Sentry:Dsn or Sentry__Dsn); an empty DSN disables the SDK, which keeps
// local runs offline, and Testing skips it entirely.
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.WebHost.UseSentry(options =>
    {
        options.Dsn = builder.Configuration["Sentry:Dsn"];
        options.SendDefaultPii = false;
        options.CaptureFailedRequests = false;
        options.TracesSampleRate = builder.Configuration.GetValue("Sentry:TracesSampleRate", 0.2);
        options.EnableLogs = builder.Configuration.GetValue("Sentry:EnableLogs", true);
        options.Debug = builder.Configuration.GetValue("Sentry:Debug", false);
        options.DefaultTags["service"] = "storefront";
    });
}

builder.Services.AddRazorPages();

var connectionString = builder.Configuration.GetConnectionString("Shizu")
    ?? throw new InvalidOperationException("ConnectionStrings:Shizu is not configured.");
// Pool metrics tag connections by data source name; unnamed, Npgsql uses the
// raw connection string (secrets included) as db.client.connection.pool.name.
builder.Services.AddDbContext<ShizuDbContext>(o =>
    o.UseNpgsql(
        connectionString,
        npgsql => npgsql.ConfigureDataSource(source => source.Name = "shizuappstore")));

builder.Services.Configure<IconsOptions>(builder.Configuration.GetSection("Icons"));
builder.Services.Configure<AssetLinksOptions>(builder.Configuration.GetSection("AssetLinks"));
builder.Services.AddScoped<CatalogService>();
builder.Services.AddSingleton<MarkdownRenderer>();
builder.Services.AddSingleton<ShizuWebMetrics>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<IAppReleaseProvider, LatestReleaseProvider>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ShizuStoreWeb/1.0");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddOutputCache(options =>
{
    options.AddBasePolicy(b => b.NoCache());
    // The "open in app" button depends only on the Android flag, so the cache
    // varies on that instead of fragmenting per full User-Agent string.
    options.AddPolicy("pages", b => b
        .Expire(TimeSpan.FromSeconds(60))
        .VaryByValue(context => new KeyValuePair<string, string>(
            "device-class",
            DeviceDetection.IsAndroid(context.Request) ? "android" : "other")));
});

// Prometheus scraping (GET /metrics): the built-in ASP.NET Core, Kestrel,
// outbound HTTP, EF Core and Npgsql meters for this process. The token comes
// from Metrics:Token or SHIZU_METRICS_TOKEN; without one the endpoint is not
// mapped, and Metrics:Enabled=false opts out of collection altogether. The
// singleton is built lazily so tests can swap it via DI.
var metricsEnabled = (builder.Configuration.GetSection("Metrics").Get<MetricsOptions>() ?? new()).Enabled;
builder.Services.AddSingleton(_ =>
{
    var options = builder.Configuration.GetSection("Metrics").Get<MetricsOptions>() ?? new();
    options.Token ??= Environment.GetEnvironmentVariable("SHIZU_METRICS_TOKEN");
    return options;
});
if (metricsEnabled)
{
    builder.Services.AddOpenTelemetry().WithMetrics(metrics =>
    {
        metrics.AddMeter(
            "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Server.Kestrel",
            "System.Net.Http",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            ShizuWebMetrics.MeterName);

        metrics.AddRuntimeInstrumentation();

        // Latency buckets suited to a cached, latency-sensitive site rather
        // than the SDK's wide defaults.
        metrics.AddView(
            "http.server.request.duration",
            new ExplicitBucketHistogramConfiguration
            {
                Boundaries =
                [
                    0, 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25,
                    0.5, 0.75, 1, 2.5, 5, 7.5, 10,
                ],
            });

        metrics.AddPrometheusExporter();
    });
}

var app = builder.Build();

// Metrics options resolved through DI so tests can swap the singleton;
// configuration snapshots taken at registration time would ignore that.
var metricsOptions = app.Services.GetRequiredService<MetricsOptions>();
var metricsToken = metricsOptions.Token;

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();

// The scrape endpoint exposes operational internals, so it stays behind a
// bearer token even though the storefront itself is public.
if (metricsOptions.Enabled && !string.IsNullOrEmpty(metricsToken))
{
    app.UseWhen(
        context => context.Request.Path == "/metrics",
        branch => branch.Use(async (context, next) =>
        {
            if (!MetricsAuth.IsValidToken(context.Request, metricsToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        }));
}

// Public storefront: same-origin scripts only. Inline styles stay allowed
// because mdui sets style attributes for ripple and layout.
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' https: data:; font-src 'self' data:; form-action 'self'; " +
        "base-uri 'none'; frame-ancestors 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    await next(context);
});

// Before the output cache so pages served from it still count.
app.UseMiddleware<AppViewMetricsMiddleware>();

app.UseOutputCache();

app.MapRazorPages();

app.MapGet("/icons/{sha}.png", IResult (string sha, HttpContext context, IOptions<IconsOptions> options) =>
{
    if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
    {
        return Results.BadRequest();
    }

    var path = Path.Combine(options.Value.StorePath, sha + ".png");
    if (!File.Exists(path))
    {
        return Results.NotFound();
    }

    context.Response.Headers.CacheControl = "public, max-age=86400, immutable";
    return Results.File(path, "image/png");
});

app.MapGet("/.well-known/assetlinks.json", (HttpContext context, IOptions<AssetLinksOptions> options) =>
{
    context.Response.Headers.CacheControl = "public, max-age=3600";
    var target = new Dictionary<string, object>
    {
        ["namespace"] = "android_app",
        ["package_name"] = options.Value.PackageName,
        ["sha256_cert_fingerprints"] = options.Value.Fingerprints,
    };
    var statements = new[]
    {
        new Dictionary<string, object>
        {
            ["relation"] = new[] { "delegate_permission/common.handle_all_urls" },
            ["target"] = target,
        },
    };
    return Results.Json(statements);
});

app.MapGet("/healthz", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { status = "ok" });
});

if (metricsOptions.Enabled && !string.IsNullOrEmpty(metricsToken))
{
    // The scrape itself is excluded from HTTP metrics: scrapes are operator
    // traffic and would otherwise be the most regular series in the store.
    app.MapPrometheusScrapingEndpoint("/metrics").DisableHttpMetrics();
}
else if (metricsOptions.Enabled)
{
    app.Logger.LogWarning(
        "Metrics are enabled but no metrics token is configured; /metrics is not mapped.");
}

app.Run();

public partial class Program;
