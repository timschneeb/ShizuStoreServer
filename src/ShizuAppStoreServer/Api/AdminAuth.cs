using System.Security.Cryptography;
using System.Text;

namespace ShizuAppStoreServer.Api;

/// <summary>Bearer-token check shared by the operator endpoints.</summary>
public static class AdminAuth
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
