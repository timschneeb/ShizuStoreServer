using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ShizuAppStoreServer.Web.Pages;

public sealed class ErrorModel : PageModel
{
    public void OnGet()
    {
        Response.Headers.CacheControl = "no-store";
    }
}
