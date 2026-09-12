using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>APK info from the Instafel release API.</summary>
public sealed record InstafelRelease(string ApkUrl, string FileHash);

/// <summary>Non-success or malformed response from the Instafel release API.</summary>
public sealed class InstafelApiException(string message) : Exception(message);

public interface IInstafelReleaseClient
{
    /// <exception cref="InstafelApiException">API unreachable, non-success, or no APK in the payload.</exception>
    Task<InstafelRelease> GetLatestAsync(CancellationToken ct = default);
}

/// <summary>
/// Special case for instafel: the monorepo (mamiiblt/instafel) publishes no
/// GitHub releases; the maintainer serves them via api.mamii.dev, whose
/// payload carries <c>fileInfos.unclone</c> (standard build) and
/// <c>fileInfos.clone</c> (fallback for older target versions).
/// <c>fileHash</c> doubles as the change signal (stored as the enricher's
/// etag); the API supports no conditional requests.
/// </summary>
public sealed class InstafelReleaseClient : IInstafelReleaseClient
{
    private const string Endpoint = "https://api.mamii.dev/content/rels/get/latest";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _http;

    public InstafelReleaseClient(HttpClient http)
    {
        _http = http;
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShizuAppStoreServer", "1.0"));
        }
    }

    public async Task<InstafelRelease> GetLatestAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(Endpoint, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InstafelApiException($"api.mamii.dev answered {(int)response.StatusCode}.");
        }

        var payload = await JsonSerializer.DeserializeAsync<PayloadDto>(
            await response.Content.ReadAsStreamAsync(ct), Json, ct)
            ?? throw new InstafelApiException("api.mamii.dev returned no JSON.");

        var file = payload.FileInfos?.Unclone ?? payload.FileInfos?.Clone;
        if (file is null || string.IsNullOrWhiteSpace(file.FileUrl))
        {
            throw new InstafelApiException("api.mamii.dev returned no APK file info.");
        }

        return new InstafelRelease(file.FileUrl, file.FileHash ?? string.Empty);
    }

    private sealed class PayloadDto
    {
        [JsonPropertyName("fileInfos")]
        public FileInfosDto? FileInfos { get; set; }
    }

    private sealed class FileInfosDto
    {
        [JsonPropertyName("unclone")]
        public FileDto? Unclone { get; set; }

        [JsonPropertyName("clone")]
        public FileDto? Clone { get; set; }
    }

    private sealed class FileDto
    {
        [JsonPropertyName("fileUrl")]
        public string? FileUrl { get; set; }

        [JsonPropertyName("fileHash")]
        public string? FileHash { get; set; }
    }
}
