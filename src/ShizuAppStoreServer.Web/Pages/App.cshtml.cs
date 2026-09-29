using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;
using ShizuAppStoreServer.Web.Mapping;
using ShizuAppStoreServer.Web.Rendering;
using ShizuAppStoreServer.Web.Services;

namespace ShizuAppStoreServer.Web.Pages;

[OutputCache(PolicyName = "pages")]
public sealed class AppModel(CatalogService catalog, MarkdownRenderer markdown) : PageModel
{
    public AppDetailView? View { get; private set; }

    public string DescriptionHtml { get; private set; } = string.Empty;

    public string UsageHtml { get; private set; } = string.Empty;

    public string ChangelogHtml { get; private set; } = string.Empty;

    public bool IsAndroid { get; private set; }

    public bool Missing { get; private set; }

    public IReadOnlyList<AppTile> MoreFromAuthor { get; private set; } = [];

    public IReadOnlyList<AppTile> MoreFromCategory { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        var app = await catalog.GetAppAsync(slug, ct);
        if (app is null)
        {
            // Rendered as the client's placeholder, with a real 404 status.
            Missing = true;
            Response.StatusCode = StatusCodes.Status404NotFound;
            Response.Headers.CacheControl = "no-store";
            return Page();
        }

        var view = new AppDetailView(app);
        View = view;
        DescriptionHtml = markdown.Render(app.FullDescription, view.BaseUrl);
        UsageHtml = markdown.Render(app.UsageMarkdown, view.BaseUrl);
        ChangelogHtml = markdown.Render(app.Changelog, view.BaseUrl);
        if (!string.IsNullOrWhiteSpace(app.AuthorKey))
        {
            MoreFromAuthor = (await catalog.GetMoreFromAuthorAsync(app, 12, ct))
                .Select(AppDetailView.ToTile).ToList();
        }

        if (app.CategoryId > 0 && view.CategoryPath.Count > 0)
        {
            MoreFromCategory = (await catalog.GetMoreFromCategoryAsync(app, 12, ct))
                .Select(AppDetailView.ToTile).ToList();
        }

        IsAndroid = DeviceDetection.IsAndroid(Request);
        Response.Headers.CacheControl = "public, max-age=60";
        return Page();
    }
}
