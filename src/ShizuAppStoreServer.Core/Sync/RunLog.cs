using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;

namespace ShizuAppStoreServer.Core.Sync;

/// <summary>
/// Human-readable enrichment log: one clearly separated section per sync pass,
/// one line per scanned app. Opt-in through <c>Enrichment:RunLogPath</c>; the
/// no-op implementation keeps tests and library use side-effect free.
/// </summary>
public interface IRunLog
{
    /// <summary>Opens a section for a pass that is about to enrich <paramref name="appCount"/> apps.</summary>
    void Begin(string trigger, bool fullRecheck, DateTimeOffset now, int appCount);

    /// <summary>One scanned app's outcome, streamed as it finishes.</summary>
    void App(string slug, string? displayName, EnrichResult result);

    /// <summary>
    /// Mid-run progress line for a slow action (download, release fetch,
    /// analysis phase, icon render). Timestamped when written.
    /// </summary>
    void Detail(string message);

    /// <summary>A pass that found nothing due; a single line so the cadence stays visible.</summary>
    void Skip(string trigger, DateTimeOffset now, string reason);

    /// <summary>Closes the section with the run's totals and duration.</summary>
    void End(DateTimeOffset now, int enriched, int upToDate, int failed);

    /// <summary>
    /// Post-run catalog health snapshot, same data as <c>GET /v1/issues</c>.
    /// </summary>
    void Issues(long? runId, string? head, IReadOnlyList<SyncIssue> issues);
}

/// <summary>Discards everything; used when no <c>RunLogPath</c> is configured.</summary>
public sealed class NullRunLog : IRunLog
{
    public static readonly NullRunLog Instance = new();

    private NullRunLog()
    {
    }

    public void Begin(string trigger, bool fullRecheck, DateTimeOffset now, int appCount)
    {
    }

    public void App(string slug, string? displayName, EnrichResult result)
    {
    }

    public void Detail(string message)
    {
    }

    public void Skip(string trigger, DateTimeOffset now, string reason)
    {
    }

    public void End(DateTimeOffset now, int enriched, int upToDate, int failed)
    {
    }

    public void Issues(long? runId, string? head, IReadOnlyList<SyncIssue> issues)
    {
    }
}

/// <summary>
/// Append-only <see cref="IRunLog"/>. One line per app, written as it
/// finishes, so a long nightly pass shows progress. IO failures never fail a
/// pass: they are warned once and the log goes quiet. Rotates at run
/// boundaries once the active file reaches <paramref name="maxBytes"/>, keeping
/// exactly two files (<c>path</c> and <c>path.1</c>); rotating only when a run
/// starts keeps every section whole.
/// </summary>
public sealed class FileRunLog(
    string path, ILogger<FileRunLog>? log = null, long maxBytes = 1_048_576) : IRunLog
{
    private readonly object _gate = new();
    private DateTimeOffset _runStart;
    private int _total;
    private int _index;
    private bool _warned;

    public void Begin(string trigger, bool fullRecheck, DateTimeOffset now, int appCount)
    {
        lock (_gate)
        {
            _runStart = now;
            _total = appCount;
            _index = 0;
            RotateIfNeeded();
            Append($"===== run {Stamp(now)} | trigger={trigger} | "
                + $"mode={(fullRecheck ? "full" : "due")} | apps={appCount} =====");
        }
    }

    public void App(string slug, string? displayName, EnrichResult result)
    {
        lock (_gate)
        {
            _index++;
            var width = Math.Max(2, _total.ToString().Length);
            var name = string.IsNullOrWhiteSpace(displayName) || displayName == slug
                ? slug
                : $"{slug} ({displayName})";
            var status = Status(result.Outcome);
            var note = result.Outcome == EnrichOutcome.Failed ? result.Error : result.Detail;
            var line = $"[{_index.ToString().PadLeft(width)}/{_total}] {name}  {status.PadRight(8)}";
            if (!string.IsNullOrEmpty(note))
            {
                line += $"  {note}";
            }

            Append(line);
        }
    }

    public void Detail(string message)
    {
        lock (_gate)
        {
            Append($"{Stamp(DateTimeOffset.UtcNow)} {message}");
        }
    }

    public void Skip(string trigger, DateTimeOffset now, string reason)
    {
        lock (_gate)
        {
            Append($"===== run {Stamp(now)} | trigger={trigger} | {reason} =====");
        }
    }

    public void End(DateTimeOffset now, int enriched, int upToDate, int failed)
    {
        lock (_gate)
        {
            var elapsed = now - _runStart;
            Append($"----- run finished {Stamp(now)} | {elapsed:h\\:mm\\:ss} | "
                + $"ok={enriched} skip={upToDate} fail={failed} -----");
        }
    }

    public void Issues(long? runId, string? head, IReadOnlyList<SyncIssue> issues)
    {
        lock (_gate)
        {
            var parse = issues.Count(i => i.Kind == IssueKind.Parse);
            var enrich = issues.Count(i => i.Kind == IssueKind.Enrich);
            var quality = issues.Count(i => i.Kind == IssueKind.Quality);
            var headText = string.IsNullOrEmpty(head) ? "-" : head[..Math.Min(8, head.Length)];
            Append($"----- issues | run={runId?.ToString() ?? "-"} | head={headText} | "
                + $"parse={parse} enrich={enrich} quality={quality} total={issues.Count} -----");
            foreach (var issue in issues
                .OrderBy(i => i.Kind).ThenBy(i => i.Rule).ThenBy(i => i.Slug).ThenBy(i => i.Id))
            {
                var subject = issue.Slug ?? issue.Location ?? "-";
                Append($"{Kind(issue.Kind).PadRight(8)} {issue.Rule.PadRight(18)} "
                    + $"{subject.PadRight(24)} {issue.Message}");
            }

            Append(string.Empty);
        }
    }

    private static string Kind(IssueKind kind) => kind switch
    {
        IssueKind.Parse => "parse",
        IssueKind.Enrich => "enrich",
        _ => "quality",
    };

    private static string Stamp(DateTimeOffset now) =>
        now.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'");

    private static string Status(EnrichOutcome outcome) => outcome switch
    {
        EnrichOutcome.Enriched => "OK",
        EnrichOutcome.AvatarFallback => "ok",
        EnrichOutcome.Failed => "FAIL",
        EnrichOutcome.Excluded => "excluded",
        _ => "skip", // UpToDate, SkippedFresh
    };

    private void Append(string line)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(full, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Warn(ex);
        }
    }

    /// <summary>
    /// Moves the active file to <c>path.1</c> (overwriting the previous
    /// rotation) once it reaches <see cref="maxBytes"/>, so at most two files
    /// exist. Called only from <see cref="Begin"/>, so runs never split.
    /// </summary>
    private void RotateIfNeeded()
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) || new FileInfo(full).Length < maxBytes)
            {
                return;
            }

            File.Move(full, full + ".1", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Warn(ex);
        }
    }

    private void Warn(Exception ex)
    {
        if (_warned)
        {
            return;
        }

        _warned = true;
        log?.LogWarning("Run log '{Path}' is not writable: {Error}", path, ex.Message);
    }
}
