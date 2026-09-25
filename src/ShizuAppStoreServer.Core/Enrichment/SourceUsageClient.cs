using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>Usage scan outcome: <c>Scanned</c> is false only when the repo tree could not be fetched.</summary>
public sealed record SourceUsageResult(ShizukuUsage Usage, bool Scanned);

/// <summary>
/// Bounded source scan for Shizuku usage. Lists the repo tree through the
/// forge API, fetches a small set of likely-relevant text files and runs the
/// same marker scanner as the APK path, so obfuscated builds still get a
/// signal when their source is public.
/// </summary>
public interface ISourceUsageClient
{
    Task<SourceUsageResult> ScanAsync(App app, CancellationToken ct = default);
}

public sealed class SourceUsageClient(
    IGitHubReleaseClient github,
    IGitLabReleaseClient gitlab,
    EnrichmentOptions options,
    ILogger<SourceUsageClient>? log = null) : ISourceUsageClient
{
    private static readonly string[] Extensions = [".kt", ".java", ".aidl", ".gradle", ".kts", ".toml", ".pro"];
    private static readonly string[] ManagerHints = ["shizuku", "dhizuku", "sui", "root", "shell"];

    public async Task<SourceUsageResult> ScanAsync(App app, CancellationToken ct = default)
    {
        if (!RepoScreenshotResolver.TryParseRepo(app.Url, app.SourceUrl, out var repo))
        {
            return new SourceUsageResult(ShizukuUsage.Empty, true);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.SourceUsageTimeout);
        var token = timeout.Token;
        try
        {
            var tree = repo.Forge == RepoForge.GitHub
                ? await github.GetRepoTreeAsync(repo.Owner, repo.ProjectPath, token)
                : await gitlab.GetRepoTreeAsync(repo.ProjectPath, token);
            if (tree is null)
            {
                return new SourceUsageResult(ShizukuUsage.Empty, false);
            }

            var files = new List<(string Path, string Text)>();
            foreach (var entry in Select(tree.Entries))
            {
                var text = repo.Forge == RepoForge.GitHub
                    ? await github.GetRawBlobAsync(repo.Owner, repo.ProjectPath, entry.Sha!, token)
                    : await gitlab.GetRawBlobAsync(repo.ProjectPath, entry.Sha!, token);
                if (!string.IsNullOrEmpty(text))
                {
                    files.Add((entry.Path, text));
                }
            }

            var usage = ShizukuUsageScanner.ScanFiles(files);
            log?.LogDebug(
                "usage source scan {Slug}: {Files} files, managers={Managers}, capabilities={Capabilities}",
                app.Slug, files.Count, usage.Managers.Count, usage.Capabilities.Count);
            return new SourceUsageResult(usage, true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SourceUsageResult(ShizukuUsage.Empty, false);
        }
    }

    private IReadOnlyList<RepoTreeEntry> Select(IReadOnlyList<RepoTreeEntry> entries)
    {
        var candidates = new List<(int Score, RepoTreeEntry Entry)>();
        foreach (var entry in entries)
        {
            if (entry.Sha is not { Length: > 0 } || !IsCandidate(entry.Path))
            {
                continue;
            }

            if (entry.Size is { } size && size > options.SourceUsageMaxFileBytes)
            {
                continue;
            }

            candidates.Add((Score(entry.Path), entry));
        }

        return candidates
            .OrderBy(c => c.Score)
            .ThenBy(c => c.Entry.Path, StringComparer.Ordinal)
            .Take(options.SourceUsageMaxFiles)
            .Select(c => c.Entry)
            .ToList();
    }

    private static bool IsCandidate(string path)
    {
        if (path.Contains("/test/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/androidTest/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/build/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/.git/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.EndsWith("AndroidManifest.xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    private static int Score(string path)
    {
        if (path.EndsWith("AndroidManifest.xml", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var lower = path.ToLowerInvariant();
        if (ManagerHints.Any(hint => lower.Contains(hint, StringComparison.Ordinal)))
        {
            return 1;
        }

        if (path.EndsWith(".kt", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".java", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".aidl", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }
}
