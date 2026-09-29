namespace ShizuAppStoreServer.Web.Configuration;

/// <summary>Icon store shared with the API (read-only from here).</summary>
public sealed class IconsOptions
{
    public string StorePath { get; set; } = string.Empty;
}
