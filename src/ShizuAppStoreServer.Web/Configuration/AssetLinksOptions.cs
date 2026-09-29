namespace ShizuAppStoreServer.Web.Configuration;

/// <summary>Android App Link verification payload served at /.well-known/assetlinks.json.</summary>
public sealed class AssetLinksOptions
{
    public string PackageName { get; set; } = string.Empty;

    public List<string> Fingerprints { get; set; } = [];
}
