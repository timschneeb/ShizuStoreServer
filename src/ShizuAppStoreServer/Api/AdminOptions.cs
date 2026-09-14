namespace ShizuAppStoreServer.Api;

/// <summary>Operator-auth settings. The HMAC secret comes from the
/// <c>Admin:HmacSecret</c> setting or the <c>SHIZU_ADMIN_SECRET</c>
/// environment variable (never committed to appsettings.json).</summary>
public sealed class AdminOptions
{
    public string? HmacSecret { get; set; }
}
