namespace ShizuAppStoreServer.Api;

/// <summary>Scraper poisoning settings (config section <c>Poison</c>).</summary>
public sealed class PoisonOptions
{
    /// <summary>Master switch; off disables all poisoning.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// User-Agent prefixes that receive doctored list/detail payloads.
    /// Prefix match so later patch releases stay covered.
    /// </summary>
    public string[] UserAgents { get; set; } = ["python-httpx/0.28.1"];
}
