namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>
/// AI source-analysis settings. Binds to the <c>UsageAnalysis</c> config
/// section; the API key additionally falls back to the
/// <c>SHIZU_USAGE_ANALYSIS_KEY</c> environment variable (see Program.cs).
/// Disabled by default: without an explicit enable flag, base URL and model
/// there is no queue, no worker and no outbound request.
/// </summary>
public sealed class UsageAnalysisOptions
{
    /// <summary>Master switch. False keeps the queue empty and the worker idle.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// OpenAI-compatible base URL. The opencode Go endpoint is
    /// <c>https://opencode.ai/zen/go/v1</c>.
    /// </summary>
    public string BaseUrl { get; set; } = "https://opencode.ai/zen/go/v1";

    /// <summary>Model id; the Go catalog exposes <c>mimo-v2.6-flash</c> and <c>mimo-v2.6-pro</c>.</summary>
    public string Model { get; set; } = "mimo-v2.6-flash";

    /// <summary>
    /// Wire protocol for the model: <c>chat</c> for <c>/chat/completions</c> or
    /// <c>responses</c> for <c>/responses</c>. Some catalog models, such as
    /// <c>muse-spark-1.3-contributor</c>, only speak the Responses API.
    /// </summary>
    public string Protocol { get; set; } = "chat";

    /// <summary>API key for the endpoint. Never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// User-Agent the endpoint sees. The Go subscription asks for a
    /// coding-agent identity rather than a generic HTTP library name.
    /// </summary>
    public string UserAgent { get; set; } = "ShizuStoreAnalyzer/1.0";

    /// <summary>
    /// Prompt contract generation. Stored on the app; a bump makes the next
    /// backfill re-analyze rows whose prompt version is older.
    /// </summary>
    public int PromptVersion { get; set; } = 1;

    /// <summary>
    /// Pipeline generation (context, tools, validation). Stored on the app; a
    /// bump makes the next backfill re-analyze older rows.
    /// </summary>
    public int AnalysisVersion { get; set; } = 1;

    /// <summary>
    /// Sanity cap on model turns per analysis; the agent should stop once the
    /// evidence is sufficient. Reaching it forces a final answer with tools
    /// disabled instead of spending unbounded tokens.
    /// </summary>
    public int MaxToolSteps { get; set; } = 400;

    /// <summary>
    /// Extra tool rounds the agent gets when it finalizes while surface entries
    /// are still uninspected. The coverage note lists the entries; after this
    /// many rounds the report is accepted as is. Keep this small: every round
    /// is a full tool pass and the point of the check is to catch a surface
    /// entry the agent skipped, not to re-run the investigation.
    /// </summary>
    public int MaxCoverageRounds { get; set; } = 1;

    /// <summary>
    /// When true, entry, user-service and AIDL surface entries count as
    /// inspected only after a symbol tool (read_symbol, find_callers or
    /// trace_symbol) has followed them; a plain file read is not enough.
    /// Bridge and command entries have no reliable traceable symbol and stay
    /// satisfied by a read. False keeps tracing advisory, but a run that used
    /// no symbol tool at all is still sent back once with its traceable
    /// entries, so tracing never silently disappears.
    /// </summary>
    public bool RequireTracing { get; set; }

    /// <summary>Lines one <c>read_file</c> call may return.</summary>
    public int MaxReadLines { get; set; } = 250;

    /// <summary>
    /// Output token cap per model call. Reasoning models spend this budget on
    /// hidden reasoning too, so a low cap truncates the JSON answer.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>Results one <c>search_code</c> call may return.</summary>
    public int MaxSearchResults { get; set; } = 60;

    /// <summary>Largest source file the tools will read.</summary>
    public int MaxFileBytes { get; set; } = 512 * 1024;

    /// <summary>Shallow clone timeout.</summary>
    public TimeSpan CloneTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Timeout for one model HTTP request.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Wall-clock budget for the whole per-app analysis.</summary>
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Parallel analyses. Each one clones a repo and calls the model.</summary>
    public int MaxParallelism { get; set; } = 3;

    /// <summary>Queued runs started per UTC day before the worker idles.</summary>
    public int MaxRunsPerDay { get; set; } = 100;

    /// <summary>
    /// Monthly spend ceiling in USD. The worker stops claiming runs once the
    /// recorded cost of the current UTC month reaches it.
    /// </summary>
    public decimal MonthlyBudgetUsd { get; set; } = 20m;

    /// <summary>Price per 1M uncached input tokens (default: MiMo-V2.6-Flash).</summary>
    public decimal InputPricePerMillion { get; set; } = 0.14m;

    /// <summary>Price per 1M cached input tokens (default: MiMo-V2.6-Flash).</summary>
    public decimal CachedInputPricePerMillion { get; set; } = 0.0028m;

    /// <summary>Price per 1M output tokens (default: MiMo-V2.6-Flash).</summary>
    public decimal OutputPricePerMillion { get; set; } = 0.28m;

    /// <summary>Attempts before a run is parked as failed.</summary>
     /// <summary>Attempts before a run is parked as failed.</summary>
    public int RetryMaxAttempts { get; set; } = 3;

    /// <summary>Base delay before a failed run is retried; multiplied by the attempt count.</summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Directory the per-run source checkouts live under. Null uses the system
    /// temp directory. Every checkout is deleted when the run ends.
    /// </summary>
    public string? SnapshotRoot { get; set; }

    /// <summary>
    /// Directory the per-run conversation logs are written to: one JSON
    /// transcript plus one rendered HTML page. Null or empty disables them.
    /// </summary>
    public string? LogPath { get; set; } = "usage-logs";

    /// <summary>
    /// Tool results longer than this are truncated in the stored transcript so
    /// one large file read cannot balloon the log page. 0 keeps everything.
    /// </summary>
    public int MaxTranscriptToolResultChars { get; set; } = 20_000;

    /// <summary>Worker poll interval while the queue is empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>True when the analyzer can run at all.</summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(Model);
}
