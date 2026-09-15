namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// Enricher settings. Binds to the <c>Enrichment</c> config section; the GitHub
/// PAT additionally falls back to <c>SHIZU_GITHUB_TOKEN</c> (see Program.cs).
/// </summary>
public sealed class EnrichmentOptions
{
    /// <summary>Path to the <c>aapt2</c> binary (Android SDK build-tools).</summary>
    public string Aapt2Path { get; set; } = "aapt2";
    /// <summary>
    /// Path to the <c>apksigner</c> binary (Android SDK build-tools, needs a
    /// JRE). Best-effort: without it, signing-cert fingerprints stay null.
    /// </summary>
    public string ApksignerPath { get; set; } = "apksigner";

    /// <summary>Path to the <c>gradle</c> binary (XML icon rendering).</summary>
    public string GradlePath { get; set; } = "gradle";

    /// <summary>Directory of the Paparazzi icon-render Gradle tool.</summary>
    public string IconToolDir { get; set; } = "tools/icon-render";

    /// <summary>Timeout for one Paparazzi icon render (warm daemon).</summary>
    public TimeSpan PaparazziTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Optional CPU affinity for the render JVM, as a <c>taskset -c</c> list
    /// (for example <c>"0"</c> pins render builds to one core). Null renders
    /// on every core. Linux only; a missing <c>taskset</c> fails the render,
    /// which falls back to the letter avatar.
    /// </summary>
    public string? IconRenderCpuAffinity { get; set; }

    /// <summary>Directory for <c>{sha256}.png</c> icons (deployed to <c>/opt/shizuappstore/icons/</c>).</summary>
    public string IconStorePath { get; set; } = "icons";

    /// <summary>
    /// Append-only human-readable log with one line per app per sync pass
    /// (see <c>docs/SPEC.md</c>). Null disables it, so tests and library use
    /// stay side-effect free.
    /// </summary>
    public string? RunLogPath { get; set; }

    /// <summary>GitHub PAT for the Releases API (higher rate limits). Null = anonymous.</summary>
    public string? GitHubToken { get; set; }

    /// <summary>GitLab token for the Releases API (<c>PRIVATE-TOKEN</c>). Null = anonymous.</summary>
    public string? GitLabToken { get; set; }

    /// <summary>Max parallel enrichments.</summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>Skip re-enriching healthy apps checked within this window.</summary>
    public TimeSpan SuccessRecheckInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Backoff for failed apps (per-app <c>last_error</c>).</summary>
    public TimeSpan FailedRecheckInterval { get; set; } = TimeSpan.FromHours(12);

    /// <summary>HTTP timeout for APK downloads (can be 100 MB+).</summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);
}
