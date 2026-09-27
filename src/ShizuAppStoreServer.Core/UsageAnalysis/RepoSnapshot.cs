using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>
/// One checked-out source tree plus its provenance. The root directory is
/// deleted on dispose; every run owns exactly one snapshot.
/// </summary>
public sealed record RepoSnapshot(
    RepoForge Forge,
    string Owner,
    string ProjectPath,
    string CloneUrl,
    string Commit,
    string? Ref,
    string RootPath) : IDisposable
{
    /// <summary>Forge-string used in storage and stats (<c>github</c>/<c>gitlab</c>).</summary>
    public string ForgeName => Forge == RepoForge.GitHub ? "github" : "gitlab";

    /// <summary>Browsable URL of a checked-out path and line, pinned to the commit.</summary>
    public string BlobUrl(string path, int line) => Forge == RepoForge.GitHub
        ? $"https://github.com/{Owner}/{ProjectPath}/blob/{Commit}/{path}#L{line}"
        : $"https://gitlab.com/{ProjectPath}/-/blob/{Commit}/{path}#L{line}";

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch
        {
            // Best effort: a leaked temp directory is harmless next to a failed run.
        }
    }
}

public interface IRepoSnapshotProvider
{
    /// <summary>
    /// Shallow-clones the app's repo at the release tag when one matches the
    /// recorded release tag or the given version, else at the default branch
    /// HEAD. Returns null when the app has no GitHub/GitLab repo or the clone
    /// fails.
    /// </summary>
    Task<RepoSnapshot?> CreateAsync(
        App app, string? versionName, string? releaseTag = null, string? artifactUrl = null,
        CancellationToken ct = default);
}

/// <summary>
/// Extracts the forge release tag from an artifact URL that pins one (GitHub
/// release assets, GitLab release downloads, GitLab archives). Rows enriched
/// before the tag was recorded fall back to this instead of the badged
/// version name.
/// </summary>
public static class ReleaseTagParser
{
    public static string? FromArtifactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var value = url.Trim();
        return Take(value, value.IndexOf("/releases/download/", StringComparison.OrdinalIgnoreCase), "/releases/download/".Length)
            ?? Take(value, value.IndexOf("/-/releases/", StringComparison.OrdinalIgnoreCase), "/-/releases/".Length)
            ?? Take(value, value.IndexOf("/-/archive/", StringComparison.OrdinalIgnoreCase), "/-/archive/".Length);
    }

    private static string? Take(string url, int index, int markerLength)
    {
        if (index < 0)
        {
            return null;
        }

        var start = index + markerLength;
        var end = url.IndexOf('/', start);
        var raw = end < 0 ? url[start..] : url[start..end];
        var tag = Uri.UnescapeDataString(raw).Trim();
        return tag.Length == 0 ? null : tag;
    }
}

/// <summary>
/// Resolves the app repo through the same list-URL/source-URL parse as the
/// screenshot fallback, probes release tags for the recorded release tag and
/// the current version name and performs one shallow clone. Tag checkout keeps
/// the evidence pinned to the released code even when the default branch has
/// moved on.
/// </summary>
public sealed class GitRepoSnapshotProvider(
    IGitRunner git,
    UsageAnalysisOptions options,
    ILogger<GitRepoSnapshotProvider>? log = null) : IRepoSnapshotProvider
{
    public async Task<RepoSnapshot?> CreateAsync(
        App app, string? versionName, string? releaseTag = null, string? artifactUrl = null,
        CancellationToken ct = default)
    {
        if (!RepoScreenshotResolver.TryParseRepo(app.Url, app.SourceUrl, out var repo))
        {
            return null;
        }

        var pinned = string.IsNullOrWhiteSpace(releaseTag) ? ReleaseTagParser.FromArtifactUrl(artifactUrl) : releaseTag.Trim();
        // An artifact from another repo (nightly release repo, release-only
        // mirror) carries tags that version independently: pin the recorded
        // tag when the analysis repo knows it, never guess from the version.
        var versionCandidate = ArtifactComesFromAnotherRepo(repo, artifactUrl) ? null : versionName;
        var (tag, tags) = await ResolveTagAsync(repo, versionCandidate, pinned, ct);
        var root = string.IsNullOrWhiteSpace(options.SnapshotRoot)
            ? Path.GetTempPath()
            : options.SnapshotRoot!;
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, $"shizu-usage-{Guid.NewGuid():N}");

        try
        {
            var cloneArgs = new List<string> { "clone", "--depth", "1", "--single-branch", "--quiet" };
            if (tag is not null)
            {
                cloneArgs.Add("--branch");
                cloneArgs.Add(tag);
            }

            cloneArgs.Add(repo.CloneUrl);
            cloneArgs.Add(dir);

            var clone = await git.RunAsync(cloneArgs, options.CloneTimeout, ct);
            if (clone.ExitCode != 0)
            {
                log?.LogDebug("Usage snapshot clone failed for {Repo}: {Error}", repo.CloneUrl, clone.Stderr.Trim());
                Cleanup(dir);
                return null;
            }

            var head = await git.RunAsync(["-C", dir, "rev-parse", "HEAD"], options.CloneTimeout, ct);
            var commit = head.ExitCode == 0 ? head.Stdout.Trim() : string.Empty;
            if (commit.Length == 0)
            {
                Cleanup(dir);
                return null;
            }

            // The clone can carry a different tag than requested when the
            // forge serves a case-insensitive match; trust the checked-out ref.
            var actualTag = tag is not null && tags.Contains(tag, StringComparer.Ordinal) ? tag : null;
            return new RepoSnapshot(repo.Forge, repo.Owner, repo.ProjectPath, repo.CloneUrl, commit, actualTag, dir);
        }
        catch (OperationCanceledException)
        {
            Cleanup(dir);
            throw;
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "Usage snapshot failed for {Repo}.", repo.CloneUrl);
            Cleanup(dir);
            return null;
        }
    }

    /// <summary>
    /// True when the artifact URL points at a GitHub repo other than the
    /// analysis repo. A separate release repo versions independently, so its
    /// version name must not select a tag in the source repo.
    /// </summary>
    private static bool ArtifactComesFromAnotherRepo(RepoRef repo, string? artifactUrl)
    {
        if (!SourceClassifier.TryParseGitHubRepo(artifactUrl, out var owner, out var name))
        {
            return false;
        }

        return repo.Forge != RepoForge.GitHub
            || !string.Equals(owner, repo.Owner, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(name, repo.ProjectPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Exact tag candidates, most specific first: the release the artifact
    /// came from, then the version name, else HEAD. A miss falls back to HEAD:
    /// a guessed suffix match could pin the analysis to a different release
    /// than the served APK.
    /// </summary>
    private async Task<(string? Tag, IReadOnlyList<string> Tags)> ResolveTagAsync(
        RepoRef repo, string? versionName, string? releaseTag, CancellationToken ct)
    {
        var version = string.IsNullOrWhiteSpace(versionName) ? null : versionName.Trim();
        if (version is null && releaseTag is null)
        {
            return (null, []);
        }

        var remote = await git.RunAsync(
            ["ls-remote", "--tags", "--refs", repo.CloneUrl], options.CloneTimeout, ct);
        if (remote.ExitCode != 0)
        {
            return (null, []);
        }

        var tags = remote.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line[(line.LastIndexOf("refs/tags/", StringComparison.Ordinal) + "refs/tags/".Length)..].Trim())
            .Where(t => t.Length > 0)
            .ToList();

        // The release the artifact came from beats the badged version name:
        // projects that never bump versionName would otherwise pin an older
        // tag than the served APK (Open-AutoGLM v1.0.6 vs versionName 1.0.3).
        if (releaseTag is not null && tags.Contains(releaseTag, StringComparer.Ordinal))
        {
            return (releaseTag, tags);
        }

        if (version is not null)
        {
            foreach (var candidate in new[] { $"v{version}", version, $"release-{version}", $"V{version}" })
            {
                if (tags.Contains(candidate, StringComparer.Ordinal))
                {
                    return (candidate, tags);
                }
            }
        }

        return (null, tags);
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
