using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>Exit code plus captured streams of one git invocation.</summary>
public sealed record GitResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Runs a git command; an interface so the resolver tests stay hermetic.</summary>
public interface IGitRunner
{
    Task<GitResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Runs the <c>git</c> binary with an argument list (no shell), capped by the
/// caller's timeout. A timeout is not a crash: it returns exit -1 so callers
/// treat it like any other failed command.
/// </summary>
public sealed class GitProcessRunner(string gitPath) : IGitRunner
{
    public async Task<GitResult> RunAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linked.Token;

        var startInfo = new ProcessStartInfo(gitPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GitResult(-1, string.Empty, $"cannot start git '{gitPath}': {ex.Message}");
        }

        if (process is null)
        {
            return new GitResult(-1, string.Empty, $"cannot start git '{gitPath}'.");
        }

        using (process)
        {
            // Drain both pipes concurrently: sequential reads can deadlock when
            // one buffer fills while the other is being read.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exiting */ }

                if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    return new GitResult(-1, string.Empty, $"timed out after {timeout}.");
                }

                throw;
            }

            return new GitResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
    }
}

/// <summary>Where a repo lives plus the path needed to clone and raw-fetch it.</summary>
public enum RepoForge
{
    GitHub,
    GitLab,
}

/// <summary>
/// One parsed repo. GitHub keeps owner and repo separately; GitLab keeps the
/// full project path (subgroups included) because the raw URL uses it verbatim.
/// </summary>
public sealed record RepoRef(RepoForge Forge, string Owner, string ProjectPath)
{
    public string CloneUrl => Forge == RepoForge.GitHub
        ? $"https://github.com/{Owner}/{ProjectPath}"
        : $"https://gitlab.com/{ProjectPath}";
}

public interface IRepoScreenshotResolver
{
    /// <summary>
    /// Best-effort screenshot raw URLs for the app's repo; empty when the app
    /// has no parseable GitHub/GitLab repo, the clone fails, or the tree has
    /// no screenshot images. Never throws except on shutdown cancellation.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveAsync(string? url, string? sourceUrl, CancellationToken ct = default);
}

/// <summary>
/// Finds screenshots directly in an app's repo when F-Droid/Izzy have none.
/// Clones commits and trees only (<c>--filter=blob:none --no-checkout</c>),
/// lists the tree, and keeps image paths whose file name starts with
/// "screenshot" or that live under a directory containing "screenshot"
/// (fastlane <c>phoneScreenshots</c>, <c>docs/screenshots</c>, ...). Raw URLs
/// are pinned to the fetched commit, so they are content-immutable.
/// </summary>
public sealed class RepoScreenshotResolver(
    IGitRunner git,
    EnrichmentOptions options,
    ILogger<RepoScreenshotResolver>? log = null) : IRepoScreenshotResolver
{
    public const int MaxScreenshots = 12;

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    private readonly SemaphoreSlim _gate = new(Math.Max(1, options.RepoScreenshotsMaxParallelism));

    public async Task<IReadOnlyList<string>> ResolveAsync(
        string? url, string? sourceUrl, CancellationToken ct = default)
    {
        if (!options.RepoScreenshotsEnabled || !TryParseRepo(url, sourceUrl, out var repo))
        {
            return [];
        }

        await _gate.WaitAsync(ct);
        var dir = Path.Combine(Path.GetTempPath(), $"shizu-repo-{Guid.NewGuid():N}");
        try
        {
            var clone = await git.RunAsync(
                ["clone", "--depth", "1", "--filter=blob:none", "--no-checkout", "--single-branch", "--quiet",
                    repo.CloneUrl, dir],
                options.RepoScreenshotsTimeout, ct);
            if (clone.ExitCode != 0)
            {
                log?.LogDebug("Repo screenshot clone failed for {Repo}: {Error}", repo.CloneUrl, clone.Stderr.Trim());
                return [];
            }

            var head = await git.RunAsync(["-C", dir, "rev-parse", "HEAD"], options.RepoScreenshotsTimeout, ct);
            var sha = head.ExitCode == 0 ? head.Stdout.Trim() : string.Empty;
            if (sha.Length == 0)
            {
                return [];
            }

            var tree = await git.RunAsync(
                ["-C", dir, "ls-tree", "-r", "--name-only", "-z", "HEAD"],
                options.RepoScreenshotsTimeout, ct);
            if (tree.ExitCode != 0)
            {
                return [];
            }

            return SelectScreenshots(SplitNul(tree.Stdout), repo, sha, MaxScreenshots);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "Repo screenshot resolution failed for {Repo}.", repo.CloneUrl);
            return [];
        }
        finally
        {
            _gate.Release();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>GitHub then GitLab, each URL before its source URL (the list link can be a blob path).</summary>
    public static bool TryParseRepo(string? url, string? sourceUrl, out RepoRef repo)
    {
        foreach (var candidate in new[] { url, sourceUrl })
        {
            if (SourceClassifier.TryParseGitHubRepo(candidate, out var owner, out var name))
            {
                repo = new RepoRef(RepoForge.GitHub, owner, name);
                return true;
            }
        }

        foreach (var candidate in new[] { url, sourceUrl })
        {
            if (SourceClassifier.TryParseGitLabRepo(candidate, out var project))
            {
                repo = new RepoRef(RepoForge.GitLab, string.Empty, project);
                return true;
            }
        }

        repo = null!;
        return false;
    }

    /// <summary>
    /// Keeps screenshot images, builds pinned raw URLs, sorts deterministically,
    /// and caps the result. Pure: the git-free half of the resolver.
    /// </summary>
    public static IReadOnlyList<string> SelectScreenshots(
        IEnumerable<string> treePaths, RepoRef repo, string sha, int max)
    {
        var urls = new List<string>();
        foreach (var path in treePaths)
        {
            if (string.IsNullOrEmpty(path) || !IsScreenshotImage(path))
            {
                continue;
            }

            urls.Add(BuildRawUrl(repo, sha, path));
        }

        urls.Sort(StringComparer.Ordinal);
        return urls.Count <= max ? urls : urls[..max];
    }

    /// <summary>Image extension and (screenshot file name or a screenshot directory).</summary>
    public static bool IsScreenshotImage(string path)
    {
        var segments = path.Split('/');
        var file = segments[^1];
        var dot = file.LastIndexOf('.');
        if (dot < 0 || !ImageExtensions.Contains(file[dot..]))
        {
            return false;
        }

        if (file.StartsWith("screenshot", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Contains("screenshot", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string BuildRawUrl(RepoRef repo, string sha, string path)
    {
        var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return repo.Forge == RepoForge.GitHub
            ? $"https://raw.githubusercontent.com/{repo.Owner}/{repo.ProjectPath}/{sha}/{encoded}"
            : $"https://gitlab.com/{repo.ProjectPath}/-/raw/{sha}/{encoded}";
    }

    private static IEnumerable<string> SplitNul(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
}
