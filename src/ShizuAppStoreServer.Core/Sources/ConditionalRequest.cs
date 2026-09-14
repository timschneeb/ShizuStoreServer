using System.Net.Http.Headers;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Conditional-request helpers. Stored validators are replayed verbatim, and
/// GitHub (among others) returns weak ETags (<c>W/"…"</c>) that the
/// <see cref="EntityTagHeaderValue"/> constructor rejects, so parse instead.
/// </summary>
internal static class ConditionalRequest
{
    /// <summary>Adds <paramref name="etag"/> as If-None-Match when it is a valid tag.</summary>
    public static void ApplyIfNoneMatch(this HttpRequestMessage request, string? etag)
    {
        if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var parsed))
        {
            request.Headers.IfNoneMatch.Add(parsed);
        }
    }
}
