namespace ShizuAppStoreServer.Web.Configuration;

/// <summary>Public site constants shared by pages and mapped endpoints.</summary>
public static class SiteUrls
{
    /// <summary>
    /// Canonical origin. The Cloudflare Tunnel terminates TLS, so building
    /// absolute URLs from the request scheme would emit http.
    /// </summary>
    public const string PublicBase = "https://shizustore.com";
}
