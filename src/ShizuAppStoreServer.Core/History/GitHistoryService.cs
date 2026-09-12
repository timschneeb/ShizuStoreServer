using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Core.History;

/// <summary>First/last list-file timestamps for one entry URL.</summary>
public sealed record EntryHistory(DateTimeOffset AddedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Derives <c>added_at</c>/<c>updated_at</c> from the awesome-list git history
/// via the <c>git</c> CLI (no LibGit2Sharp). Keyed by entry URL, which is also
/// what rename detection uses (same URL + new name = update, keep id).
///
/// Strategy: a single <c>git log --reverse -p</c> over the list file; every
/// added bullet line (<c>+* [Name](url)</c>) records a sighting. In reverse
/// order the first sighting is the introduction (<c>added_at</c>), the last
/// one the most recent touch (<c>updated_at</c>).
/// </summary>
public sealed partial class GitHistoryService
{
    // Added bullet lines only ("+" but not the "+++" file header).
    [GeneratedRegex(@"^\+(?!\+\+)\s*\*\s*\[[^\]]+\]\((?<url>[^)]+)\)", RegexOptions.Multiline)]
    private static partial Regex AddedEntryPattern();

    [GeneratedRegex(@"^COMMIT:(?<hash>[0-9a-f]+)\|(?<date>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex CommitHeaderPattern();

    /// <summary>
    /// Runs <c>git log</c> in <paramref name="repoPath"/> for
    /// <paramref name="relativePath"/> and returns URL → history.
    /// Throws <see cref="InvalidOperationException"/> when git fails so the
    /// sync can record the error instead of silently backfilling "now".
    /// </summary>
    public async Task<Dictionary<string, EntryHistory>> GetHistoryAsync(
        string repoPath, string relativePath, CancellationToken ct = default)
    {
        var output = await RunGitAsync(repoPath, $"log --reverse --format=COMMIT:%H|%aI -p -- {relativePath}", ct);
        return ParseLog(output);
    }

    /// <summary>
    /// Refreshes the list clone (<c>git fetch origin</c>).
    /// Throws <see cref="InvalidOperationException"/> when git fails (no
    /// remote, offline, …) — the sync treats that as best-effort and
    /// continues off local clone state.
    /// </summary>
    public async Task FetchAsync(string repoPath, CancellationToken ct = default)
    {
        await RunGitAsync(repoPath, "fetch origin", ct);
    }

    /// <summary>HEAD commit SHA of the list clone (sync_runs bookkeeping). Null when unavailable.</summary>
    public async Task<string?> GetHeadCommitAsync(string repoPath, CancellationToken ct = default)
    {
        try
        {
            return (await RunGitAsync(repoPath, "rev-parse HEAD", ct)).Trim();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Pure parser over <c>git log --reverse --format=COMMIT:%H|%aI -p</c> output.</summary>
    public static Dictionary<string, EntryHistory> ParseLog(string logOutput)
    {
        var result = new Dictionary<string, EntryHistory>(StringComparer.Ordinal);
        DateTimeOffset? current = null;

        // Walk line by line: commit headers set the timestamp, added bullet
        // lines (matched separately to keep the URL regex tight) record sightings.
        foreach (var line in logOutput.Split('\n'))
        {
            var header = CommitHeaderPattern().Match(line);
            if (header.Success)
            {
                // Npgsql writes DateTimeOffset to timestamptz only with
                // Offset=0 (it stores UTC instants, no offsets), while
                // SQLite accepts anything — so normalize here, or every
                // commit from a non-UTC committer breaks Postgres saves.
                current = DateTimeOffset.TryParse(
                    header.Groups["date"].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal, out var ts)
                    ? ts
                    : null;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var entry = AddedEntryPattern().Match(line);
            if (!entry.Success)
            {
                continue;
            }

            var url = entry.Groups["url"].Value.Trim();
            if (result.TryGetValue(url, out var existing))
            {
                result[url] = existing with { UpdatedAt = current.Value };
            }
            else
            {
                result[url] = new EntryHistory(current.Value, current.Value);
            }
        }

        return result;
    }

    private static async Task<string> RunGitAsync(string repoPath, string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start git in '{repoPath}'.");
        }

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed in '{repoPath}': {error.Trim()}");
        }

        return output;
    }
}
