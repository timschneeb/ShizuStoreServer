using System.Security.Cryptography;
using System.Text;

namespace ShizuAppStoreServer.Web.Configuration;

/// <summary>
/// Bearer-token check for the storefront's operator-only endpoints. Mirrors
/// the API's <c>AdminAuth</c>; the storefront cannot reference the API project.
/// </summary>
public static class MetricsAuth
{
    private const string Scheme = "Bearer";

    /// <summary>Constant-time compare of the <c>Authorization: Bearer</c> header against the configured token.</summary>
    public static bool IsValidToken(HttpRequest request, string token)
    {
        if (!request.Headers.TryGetValue("Authorization", out var header))
        {
            return false;
        }

        var value = header.ToString();
        if (!value.StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = value[(Scheme.Length + 1)..].Trim();
        var expected = Encoding.UTF8.GetBytes(token);
        var actual = Encoding.UTF8.GetBytes(presented);
        return actual.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
