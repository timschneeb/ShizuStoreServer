namespace ShizuAppStoreServer.Api;

/// <summary>
/// Operator-auth settings. The API token comes from the <c>Admin:Token</c>
/// setting or the <c>SHIZU_ADMIN_TOKEN</c> environment variable, with the
/// legacy <c>SHIZU_ADMIN_SECRET</c> name still honored (never committed to
/// appsettings.json).
/// </summary>
public sealed class AdminOptions
{
    public string? Token { get; set; }
}
