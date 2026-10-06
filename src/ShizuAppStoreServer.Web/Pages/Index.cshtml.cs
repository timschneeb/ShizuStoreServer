using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;
using ShizuAppStoreServer.Core.Overrides;
using ShizuAppStoreServer.Web.Services;

namespace ShizuAppStoreServer.Web.Pages;

[OutputCache(PolicyName = "pages")]
public sealed class IndexModel(IAppReleaseProvider releaseProvider, CatalogService catalog) : PageModel
{
    private const string AppSlug = "shizustore";

    public string ApkUrl { get; private set; } = string.Empty;

    public IReadOnlyList<string> Screenshots { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        ApkUrl = await releaseProvider.GetLatestApkUrlAsync(ct);
        var app = await catalog.GetAppAsync(AppSlug, ct);
        var blocked = await catalog.GetBlockedScreenshotsAsync(ct);
        Screenshots = app is null ? [] : ScreenshotBlocklist.Filter(app.Screenshots, blocked).Take(4).ToList();
        Response.Headers.CacheControl = "public, max-age=300";
    }
}
