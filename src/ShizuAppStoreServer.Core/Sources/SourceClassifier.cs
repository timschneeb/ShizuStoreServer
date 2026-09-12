using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Maps entry/source URLs to <see cref="SourceKind"/> and extracts
/// <c>owner/repo</c> from GitHub URLs. Pure static helpers, shared by the
/// GitHub resolver (M3) and the F-Droid/GitLab resolvers (M4).
/// </summary>
public static class SourceClassifier
{    /// <summary>Forge detection from a URL host. Unknown/garbage → <c>Other</c>.</summary>
    public static SourceKind Classify(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return SourceKind.Other;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host is "github.com" or "www.github.com")
        {
            return SourceKind.GitHub;
        }

        if (host is "gitlab.com" or "www.gitlab.com")
        {
            return SourceKind.GitLab;
        }

        if (host is "codeberg.org" or "www.codeberg.org")
        {
            return SourceKind.Codeberg;
        }

        if (host is "f-droid.org" or "www.f-droid.org" or "f-droid.github.io")
        {
            return SourceKind.FDroid;
        }

        if (host.Contains("izzysoft", StringComparison.Ordinal)
            || host.Contains("izzyondroid", StringComparison.Ordinal))
        {
            return SourceKind.Izzy;
        }

        if (host is "play.google.com" or "play.google.de")
        {
            return SourceKind.Play;
        }

        return SourceKind.Other;
    }

    /// <summary>
    /// Extracts <c>(owner, repo)</c> from <c>github.com/{owner}/{repo}</c> URLs,
    /// tolerating trailing slashes, <c>.git</c>, and deeper paths
    /// (<c>/tree/…</c>, <c>/releases/…</c>). Rejects gists and non-repo URLs.
    /// </summary>
    public static bool TryParseGitHubRepo(string? url, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("github.com" or "www.github.com"))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        owner = segments[0];
        repo = segments[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        return owner.Length > 0 && repo.Length > 0;
    }

    /// <summary>
    /// Extracts the full project path (<c>group/repo</c>, subgroups kept)
    /// from <c>gitlab.com/…</c> URLs, tolerating trailing slashes,
    /// <c>.git</c>, and deeper paths (<c>/-/releases</c>, <c>/tree/…</c>).
    /// Only gitlab.com hosts; self-hosted GitLab is <c>Other</c>.
    /// </summary>
    public static bool TryParseGitLabRepo(string? url, out string projectPath)
    {
        projectPath = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("gitlab.com" or "www.gitlab.com"))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        // GitLab UI paths interleave /-/ (releases, pipelines, …): cut there.
        var dash = Array.IndexOf(segments, "-");
        var path = dash >= 0 ? segments[..dash] : segments;
        if (path.Length < 2)
        {
            return false;
        }

        projectPath = string.Join('/', path);
        if (projectPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            projectPath = projectPath[..^4];
        }

        return projectPath.Length > 0;
    }

    /// <summary>
    /// Normalized forge repo key (<c>github:owner/repo</c>,
    /// <c>gitlab:group/project</c>, lowercased) for matching a list entry's
    /// forge URL against F-Droid index <c>&lt;source&gt;</c> elements.
    /// Null for non-forge URLs.
    /// </summary>
    public static string? RepoKey(string? url)
    {
        if (TryParseGitHubRepo(url, out var owner, out var repo))
        {
            return $"github:{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}";
        }

        if (TryParseGitLabRepo(url, out var projectPath))
        {
            return $"gitlab:{projectPath.ToLowerInvariant()}";
        }

        return null;
    }

    /// <summary>
    /// Extracts the Android package id from F-Droid/Izzy listing URLs:
    /// <c>f-droid.org/[lang/]packages/&lt;id&gt;</c> and
    /// <c>apt.izzysoft.de/fdroid/index/apk/&lt;id&gt;</c>.
    /// </summary>
    public static bool TryParseFdroidPackage(string? url, out string packageId)
    {
        packageId = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var host = uri.Host.ToLowerInvariant();
        if (host is "f-droid.org" or "www.f-droid.org")
        {
            // Optional locale prefix: /en/packages/<id>.
            var i = Array.IndexOf(segments, "packages");
            if (i >= 0 && i + 1 < segments.Length)
            {
                packageId = segments[i + 1];
                return packageId.Length > 0;
            }

            return false;
        }

        if (host.Contains("izzysoft", StringComparison.Ordinal)
            || host.Contains("izzyondroid", StringComparison.Ordinal))
        {
            // …/fdroid/index/apk/<id>.
            var i = Array.IndexOf(segments, "apk");
            if (i >= 0 && i + 1 < segments.Length)
            {
                packageId = segments[i + 1];
                return packageId.Length > 0;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// Extracts the package id from a Google Play app listing URL.
    /// </summary>
    public static bool TryParsePlayPackage(string? url, out string packageId)
    {
        packageId = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("play.google.com" or "play.google.de"))
        {
            return false;
        }

        // Manual query parse: Core has no ASP.NET query helpers, and the id
        // may be followed by tracking parameters (hl, gl, ...).
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq].Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                packageId = Uri.UnescapeDataString(pair[(eq + 1)..]);
                return packageId.Length > 0;
            }
        }

        return false;
    }
}
