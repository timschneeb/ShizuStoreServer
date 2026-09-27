using Microsoft.Extensions.Logging;

namespace ShizuAppStoreServer.Core.Jobs;

/// <summary>
/// Append-only human readable job log, one section per run. Opt-in through
/// <c>Enrichment:RunLogPath</c>; the database sink is the primary record. IO
/// failures never fail a job: they are warned once and the file goes quiet.
/// Rotates at run boundaries once the active file reaches
/// <paramref name="maxBytes"/>, keeping exactly two files (<c>path</c> and
/// <c>path.1</c>), so every section stays whole.
/// </summary>
public sealed class FileJobSink(string path, ILogger? log = null, long maxBytes = 1_048_576) : IJobSink
{
    private readonly object _gate = new();
    private bool _warned;

    public IJobSinkSession Begin(JobStart start)
    {
        lock (_gate)
        {
            RotateIfNeeded();
            var items = start.ItemCount is { } count ? $" | items={count}" : string.Empty;
            Append($"===== job {Stamp(start.StartedAt)} | kind={Label(start.Kind)} | "
                + $"trigger={Label(start.Trigger)}{items} =====");
        }

        return new Session(this, start);
    }

    public void Skipped(JobStart start, string reason)
    {
        lock (_gate)
        {
            RotateIfNeeded();
            Append($"===== job {Stamp(start.StartedAt)} | kind={Label(start.Kind)} | "
                + $"trigger={Label(start.Trigger)} | skipped: {reason} =====");
        }
    }

    public Task RecoverInterruptedAsync(CancellationToken ct) => Task.CompletedTask;

    private void WriteEvent(int appIndex, int total, JobEventRecord record)
    {
        lock (_gate)
        {
            if (record.Type == JobEventType.App)
            {
                var totalText = total > 0 ? total.ToString() : "?";
                Append($"[{appIndex.ToString().PadLeft(Math.Max(2, totalText.Length))}/{totalText}] {record.Message}");
                return;
            }

            var phase = string.IsNullOrEmpty(record.Phase) ? string.Empty : $"[{record.Phase}] ";
            var duration = record.DurationMs is { } ms ? $" ({ms}ms)" : string.Empty;
            Append($"{Stamp(DateTimeOffset.UtcNow)} [{Label(record.Level)}] {phase}{record.Message}{duration}");
        }
    }

    private void WriteFinish(JobStart start, JobFinish finish)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(finish.Error))
            {
                Append($"{Stamp(DateTimeOffset.UtcNow)} [error] {SingleLine(finish.Error)}");
            }

            var elapsed = DateTimeOffset.UtcNow - start.StartedAt;
            Append($"----- job {Label(finish.Status)} {Stamp(DateTimeOffset.UtcNow)} | {elapsed:h\\:mm\\:ss} | "
                + $"ok={finish.ItemsOk} skip={finish.ItemsSkipped} fail={finish.ItemsFailed} -----");
            Append(string.Empty);
        }
    }

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
    /// exist. Called only at run boundaries, so runs never split.
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
        log?.LogWarning("Job log '{Path}' is not writable: {Error}", path, ex.Message);
    }

    private static string SingleLine(string text) =>
        text.ReplaceLineEndings(" | ");

    private static string Stamp(DateTimeOffset now) =>
        now.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'");

    private static string Label(JobKind kind) => kind switch
    {
        JobKind.IconRefresh => "icon-refresh",
        JobKind.ScreenshotRefresh => "screenshot-refresh",
        JobKind.UsageAnalysis => "usage-analysis",
        _ => "sync",
    };

    private static string Label(JobTrigger trigger) => trigger.ToString().ToLowerInvariant();

    private static string Label(JobStatus status) => status.ToString().ToLowerInvariant();

    private static string Label(JobEventLevel level) => level.ToString().ToLowerInvariant();

    private sealed class Session(FileJobSink sink, JobStart start) : IJobSinkSession
    {
        private int _apps;
        private int _total = start.ItemCount ?? 0;

        public long? RunId => null;

        public void SetItemCount(int count) => _total = count;

        public void Write(JobEventRecord record)
        {
            var index = record.Type == JobEventType.App ? Interlocked.Increment(ref _apps) : 0;
            sink.WriteEvent(index, _total, record);
        }

        public Task FinishAsync(JobFinish finish, CancellationToken ct)
        {
            sink.WriteFinish(start, finish);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
