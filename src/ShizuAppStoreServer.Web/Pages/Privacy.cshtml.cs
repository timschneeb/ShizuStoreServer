using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;

namespace ShizuAppStoreServer.Web.Pages;

[OutputCache(PolicyName = "pages")]
public sealed class PrivacyModel : PageModel
{
    public void OnGet()
    {
        Response.Headers.CacheControl = "public, max-age=3600";
    }
}
