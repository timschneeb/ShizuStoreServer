using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Web.Tests;

/// <summary>
/// Compatibility overloads for client tests written before the forge sources
/// took <see cref="SourceTarget"/>; they build the target the clients expect.
/// </summary>
internal static class SourceClientCompatExtensions
{
    public static Task<SourceRelease?> GetLatestReleaseAsync(
        this IGitHubReleaseClient client, string owner, string repo, string? etag) =>
        client.GetLatestReleaseAsync(new SourceTarget(SourceKind.GitHub, $"{owner}/{repo}"), etag);

    public static Task<SourceRelease?> GetLatestReleaseAsync(
        this IGitLabReleaseClient client, string projectPath, string? etag) =>
        client.GetLatestReleaseAsync(new SourceTarget(SourceKind.GitLab, projectPath), etag);

    public static Task<SourceRelease?> GetLatestReleaseAsync(
        this IGitCodeReleaseClient client, string owner, string repo, string? etag = null) =>
        client.GetLatestReleaseAsync(new SourceTarget(SourceKind.Other, $"{owner}/{repo}"), etag);
}
