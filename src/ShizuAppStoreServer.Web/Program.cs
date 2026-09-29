using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Web.Configuration;
using ShizuAppStoreServer.Web.Rendering;
using ShizuAppStoreServer.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

var connectionString = builder.Configuration.GetConnectionString("Shizu")
    ?? throw new InvalidOperationException("ConnectionStrings:Shizu is not configured.");
builder.Services.AddDbContext<ShizuDbContext>(o => o.UseNpgsql(connectionString));

builder.Services.Configure<IconsOptions>(builder.Configuration.GetSection("Icons"));
builder.Services.Configure<AssetLinksOptions>(builder.Configuration.GetSection("AssetLinks"));
builder.Services.AddScoped<CatalogService>();
builder.Services.AddSingleton<MarkdownRenderer>();
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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();

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

app.Run();

public partial class Program;
