using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Markdig;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>
/// Renders a stored analysis transcript into one standalone, interactive HTML
/// page: conversation bubbles, expandable tool calls with arguments and
/// results, and token/cost stats. No external assets, safe to open from disk.
/// </summary>
public static class UsageLogHtml
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private const int MaxUninspectedShown = 30;

    public static string Render(UsageLogDocument doc)
    {
        var sb = new StringBuilder(96 * 1024);
        var results = doc.Messages
            .Where(m => m.Role == "tool" && m.ToolCallId is not null)
            .GroupBy(m => m.ToolCallId!)
            .ToDictionary(g => g.Key, g => g.First());
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var toolCalls = doc.Messages.Sum(m => m.Calls.Count);

        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<meta name=\"robots\" content=\"noindex\">\n");
        sb.Append($"<title>{Escape(doc.AppName)} usage analysis run {doc.RunId}</title>\n");
        sb.Append(Style);
        sb.Append("</head>\n<body>\n<div class=\"wrap\">\n");

        sb.Append("<header class=\"card head\">\n");
        sb.Append($"<div class=\"headline\"><h1>{Escape(doc.AppName)}</h1>");
        sb.Append($"<span class=\"badge {StatusClass(Outcome(doc))}\">{Escape(Outcome(doc))}</span></div>\n");
        sb.Append("<div class=\"meta\">");
        sb.Append($"<code>{Escape(doc.Slug)}</code>");
        if (!string.IsNullOrWhiteSpace(doc.PackageName))
        {
            sb.Append($"<span>{Escape(doc.PackageName)}</span>");
        }

        if (!string.IsNullOrWhiteSpace(doc.VersionName))
        {
            sb.Append($"<span>v{Escape(doc.VersionName)}</span>");
        }

        sb.Append($"<span>run {doc.RunId}</span>");
        sb.Append($"<span>attempt {doc.Attempts}</span>");
        sb.Append("</div>\n");
        sb.Append("<div class=\"meta\">");
        sb.Append($"<span>model <code>{Escape(doc.Model ?? "-")}</code></span>");
        sb.Append($"<span>prompt p{doc.PromptVersion}</span>");
        sb.Append($"<span>pipeline a{doc.AnalysisVersion}</span>");
        sb.Append($"<span>{Escape(FormatTimestamp(doc.FinishedAt))} UTC</span>");
        sb.Append("</div>\n");
        if (!string.IsNullOrWhiteSpace(doc.RepoUrl))
        {
            sb.Append("<div class=\"meta\">");
            sb.Append($"<span>repo <code>{Escape(Shorten(doc.RepoUrl, 90))}</code></span>");
            if (!string.IsNullOrWhiteSpace(doc.Forge))
            {
                sb.Append($"<span>{Escape(doc.Forge)}</span>");
            }

            if (!string.IsNullOrWhiteSpace(doc.Commit))
            {
                sb.Append($"<span>commit <code>{Escape(doc.Commit)}</code></span>");
            }

            if (!string.IsNullOrWhiteSpace(doc.Ref))
            {
                sb.Append($"<span>tag <code>{Escape(doc.Ref)}</code></span>");
            }

            sb.Append("</div>\n");
        }

        if (!string.IsNullOrWhiteSpace(doc.Error))
        {
            sb.Append($"<div class=\"error\">{Escape(doc.Error)}</div>\n");
        }

        sb.Append("</header>\n");

        RenderReport(sb, doc);
        RenderStats(sb, doc, toolCalls);
        RenderCoverage(sb, doc);
        RenderModelCalls(sb, doc);

        sb.Append("<section class=\"card toolbar\">\n");
        sb.Append("<button type=\"button\" id=\"expand\">Expand all</button>\n");
        sb.Append("<button type=\"button\" id=\"collapse\">Collapse all</button>\n");
        sb.Append("<label class=\"toggle\"><input type=\"checkbox\" id=\"only-tools\"> only messages with tool calls</label>\n");
        sb.Append($"<span class=\"muted spacer\">{doc.Messages.Count} messages, {toolCalls} tool calls</span>\n");
        sb.Append("</section>\n");

        sb.Append("<section id=\"conv\">\n");
        foreach (var message in doc.Messages)
        {
            RenderMessage(sb, message, results, consumed);
        }

        if (doc.Messages.Count == 0)
        {
            sb.Append("<div class=\"card muted\">No conversation was captured for this run.</div>\n");
        }

        sb.Append("</section>\n");
        sb.Append("</div>\n");
        sb.Append(Script);
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>
    /// The final user-facing report, rendered as formatted markdown so the log
    /// page shows exactly what the client will display.
    /// </summary>
    private static void RenderReport(StringBuilder sb, UsageLogDocument doc)
    {
        if (doc.Report is null)
        {
            return;
        }

        sb.Append("<section class=\"card report\">\n<h2>Report</h2>\n");
        if (!string.IsNullOrWhiteSpace(doc.Report.Short))
        {
            sb.Append($"<p class=\"lead\">{Escape(doc.Report.Short)}</p>\n");
        }

        var markdown = doc.Report.ComposedMarkdown;
        if (!string.IsNullOrWhiteSpace(markdown))
        {
            sb.Append("<div class=\"md\">\n");
            sb.Append(Markdown.ToHtml(markdown));
            sb.Append("\n</div>\n");
        }

        sb.Append("</section>\n");
    }

    private static void RenderStats(StringBuilder sb, UsageLogDocument doc, int toolCalls)
    {
        var duration = doc.FinishedAt - doc.StartedAt;
        sb.Append("<section class=\"card\">\n<h2>Stats</h2>\n<div class=\"stats\">\n");
        Stat(sb, "Outcome", Outcome(doc));
        Stat(sb, "Queue status", doc.Status);
        Stat(sb, "Duration", FormatDuration(duration));
        Stat(sb, "Model calls", doc.ModelCalls.Count.ToString(Invariant));
        Stat(sb, "Tool calls", toolCalls.ToString(Invariant));
        Stat(sb, "Input tokens", doc.InputTokens.ToString("N0", Invariant));
        Stat(sb, "Cached input", CachedShare(doc));
        Stat(sb, "Output tokens", doc.OutputTokens.ToString("N0", Invariant));
        Stat(sb, "Cost (est.)", "$" + doc.CostUsd.ToString("0.000000", Invariant));
        Stat(sb, "Tools used", ToolNames(doc));
        if (doc.Coverage is { } coverage)
        {
            Stat(sb, "Surface inspected", Share(coverage.SurfaceInspected, coverage.SurfaceEntries));
            Stat(sb, "Call sites read", Share(coverage.TracedCallSitesRead, coverage.TracedCallSites));
            Stat(sb, "Coverage rounds", coverage.Rounds.ToString(Invariant));
            Stat(sb, "Symbol tool calls", coverage.SymbolToolCalls.ToString(Invariant));
        }

        sb.Append("</div>\n</section>\n");
    }

    /// <summary>
    /// Lists what the coverage gate could not get the agent to read before it
    /// accepted the report. These are the likeliest places for a missed
    /// capability, so the log shows them even though the report shipped.
    /// </summary>
    private static void RenderCoverage(StringBuilder sb, UsageLogDocument doc)
    {
        if (doc.Coverage is not { } coverage || coverage.Uninspected.Count == 0)
        {
            return;
        }

        sb.Append("<section class=\"card\">\n<h2>Coverage</h2>\n");
        sb.Append("<p class=\"muted\">The report was accepted with these locations still uninspected; "
            + "treat them as the likeliest places for a missed capability.</p>\n");
        sb.Append("<ul class=\"coverage\">\n");
        foreach (var item in coverage.Uninspected.Take(MaxUninspectedShown))
        {
            sb.Append($"<li>{Escape(item)}</li>\n");
        }

        var more = coverage.Uninspected.Count - MaxUninspectedShown;
        if (more > 0)
        {
            sb.Append($"<li>... and {more.ToString(Invariant)} more</li>\n");
        }

        sb.Append("</ul>\n</section>\n");
    }

    private static void RenderModelCalls(StringBuilder sb, UsageLogDocument doc)
    {
        if (doc.ModelCalls.Count == 0)
        {
            return;
        }

        sb.Append("<section class=\"card\">\n<h2>Model calls</h2>\n<div class=\"table-scroll\">\n<table>\n");
        sb.Append("<thead><tr><th>#</th><th>Duration</th><th>Input</th><th>Cached</th><th>Output</th><th>Tools</th><th>Finish</th></tr></thead>\n<tbody>\n");
        foreach (var call in doc.ModelCalls)
        {
            sb.Append("<tr>");
            sb.Append($"<td>{call.Index}</td>");
            sb.Append($"<td>{call.Seconds.ToString("F1", Invariant)}s</td>");
            sb.Append($"<td>{call.InputTokens.ToString("N0", Invariant)}</td>");
            sb.Append($"<td>{call.CachedInputTokens.ToString("N0", Invariant)}</td>");
            sb.Append($"<td>{call.OutputTokens.ToString("N0", Invariant)}</td>");
            sb.Append($"<td>{Escape(call.Tools.Count == 0 ? "-" : string.Join(", ", call.Tools))}</td>");
            sb.Append($"<td>{Escape(call.FinishReason ?? "-")}</td>");
            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n</section>\n");
    }

    private static void RenderMessage(
        StringBuilder sb,
        UsageTranscriptMessage message,
        IReadOnlyDictionary<string, UsageTranscriptMessage> results,
        HashSet<string> consumed)
    {
        if (message.Role == "tool")
        {
            if (message.ToolCallId is not null && consumed.Contains(message.ToolCallId))
            {
                return;
            }

            sb.Append("<div class=\"msg tool\" data-role=\"tool\">\n<div class=\"role\">tool result</div>\n");
            sb.Append("<details class=\"call\" data-tool=\"?\"><summary><span class=\"tool\">?</span>"
                + "<span class=\"arg\">unmatched result</span></summary><div class=\"io\">");
            AppendIo(sb, null, message.ToolResult);
            sb.Append("</div></details>\n</div>\n");
            return;
        }

        var cls = message.Role switch
        {
            "system" => "msg system",
            "assistant" => "msg assistant",
            _ => "msg user",
        };
        sb.Append($"<div class=\"{cls}\" data-role=\"{Escape(message.Role)}\">\n");
        sb.Append($"<div class=\"role\">{Escape(message.Role)}</div>\n");

        if (!string.IsNullOrEmpty(message.Text))
        {
            if (message.Role == "system")
            {
                Collapsible(sb, "System prompt", message.Text);
            }
            else
            {
                Collapsible(sb, "Message", message.Text);
            }
        }

        foreach (var call in message.Calls)
        {
            consumed.Add(call.CallId);
            results.TryGetValue(call.CallId, out var result);
            sb.Append($"<details class=\"call\" data-tool=\"{Escape(call.Name)}\">\n<summary>");
            sb.Append($"<span class=\"tool\">{Escape(call.Name)}</span>");
            sb.Append($"<span class=\"arg\">{Escape(CallSummary(call))}</span>");
            sb.Append($"<span class=\"pill {(result is null ? "miss" : "hit")}\">{(result is null ? "no result" : "result")}</span>");
            sb.Append("</summary>\n<div class=\"io\">\n");
            AppendIo(sb, call, result?.ToolResult);
            sb.Append("</div>\n</details>\n");
        }

        sb.Append("</div>\n");
    }

    private static void AppendIo(StringBuilder sb, UsageTranscriptCall? call, string? result)
    {
        if (call is not null)
        {
            sb.Append("<div class=\"io-title\">Arguments</div>\n");
            sb.Append($"<pre class=\"block\">{Escape(Pretty(call.Arguments))}</pre>\n");
        }

        sb.Append("<div class=\"io-title\">Result</div>\n");
        sb.Append(result is null
            ? "<pre class=\"block muted\">(not captured)</pre>\n"
            : $"<pre class=\"block\">{Escape(result)}</pre>\n");
    }

    private static void Collapsible(StringBuilder sb, string label, string text)
    {
        var open = text.Length <= 1200 ? " open" : string.Empty;
        sb.Append($"<details class=\"content\"{open}>\n<summary>{Escape(label)} <span class=\"muted\">({text.Length.ToString("N0", Invariant)} chars)</span></summary>\n");
        sb.Append($"<pre class=\"block\">{Escape(text)}</pre>\n</details>\n");
    }

    private static void Stat(StringBuilder sb, string label, string value)
    {
        sb.Append($"<div class=\"stat\"><div class=\"stat-label\">{Escape(label)}</div><div class=\"stat-value\">{Escape(value)}</div></div>\n");
    }

    private static string CachedShare(UsageLogDocument doc)
    {
        if (doc.InputTokens <= 0)
        {
            return "-";
        }

        var share = (double)doc.CachedInputTokens / doc.InputTokens * 100;
        return $"{doc.CachedInputTokens.ToString("N0", Invariant)} ({share.ToString("F0", Invariant)}%)";
    }

    private static string Share(int read, int total) =>
        total <= 0 ? "-" : $"{read} / {total} ({read * 100 / total}%)";

    private static string ToolNames(UsageLogDocument doc)
    {
        var names = doc.Messages
            .SelectMany(m => m.Calls)
            .Select(c => c.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return names.Length == 0 ? "-" : string.Join(", ", names);
    }

    /// <summary>Short label for a collapsed tool call, taken from the arguments.</summary>
    private static string CallSummary(UsageTranscriptCall call)
    {
        try
        {
            using var json = JsonDocument.Parse(call.Arguments);
            foreach (var key in new[] { "path", "pattern" })
            {
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    return Shorten(value.GetString() ?? string.Empty, 90);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through to the raw prefix.
        }

        return Shorten(call.Arguments.Replace('\n', ' '), 90);
    }

    private static string Pretty(string raw)
    {
        try
        {
            using var json = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
            : $"{duration.TotalSeconds.ToString("F1", Invariant)}s";

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", Invariant);

    private static string StatusClass(string status) => status switch
    {
        "succeeded" => "ok",
        "failed" => "err",
        "running" => "warn",
        _ => "muted",
    };

    /// <summary>
    /// Whether this attempt produced a report. The queue status can be
    /// <c>pending</c> for a failed attempt that still has retries left.
    /// </summary>
    private static string Outcome(UsageLogDocument doc) => doc.Error is null ? "succeeded" : "failed";

    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private const string Style = """
        <style>
        :root {
          --bg: #0f1115; --panel: #171a21; --border: #262b36; --text: #e6e9ef;
          --muted: #9aa3b2; --accent: #7c9cff; --ok: #43c383; --warn: #f0b35b;
          --err: #ef6a6a; --code: #11141a;
          --mono: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        }
        @media (prefers-color-scheme: light) {
          :root {
            --bg: #f4f5f7; --panel: #ffffff; --border: #e1e4ea; --text: #1b1f27;
            --muted: #5d6675; --accent: #3056d3; --ok: #1e8e5a; --warn: #a96c12;
            --err: #c33f3f; --code: #f0f2f5;
          }
        }
        * { box-sizing: border-box; }
        body {
          margin: 0; background: var(--bg); color: var(--text);
          font: 15px/1.55 system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
        }
        .wrap { max-width: 1100px; margin: 0 auto; padding: 24px 16px 64px; }
        .card {
          background: var(--panel); border: 1px solid var(--border);
          border-radius: 12px; padding: 16px 18px; margin-bottom: 14px;
        }
        h1 { font-size: 22px; margin: 0; }
        h2 { font-size: 15px; margin: 0 0 12px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); }
        .headline { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
        .meta { display: flex; flex-wrap: wrap; gap: 6px 16px; color: var(--muted); font-size: 13px; margin-top: 8px; }
        .meta code, code { font-family: var(--mono); font-size: 12.5px; }
        .badge {
          font-size: 12px; font-weight: 600; text-transform: uppercase;
          padding: 2px 9px; border-radius: 999px; border: 1px solid currentColor;
        }
        .badge.ok { color: var(--ok); } .badge.err { color: var(--err); }
        .badge.warn { color: var(--warn); } .badge.muted { color: var(--muted); }
        .error {
          margin-top: 12px; padding: 10px 12px; border-radius: 8px;
          background: color-mix(in srgb, var(--err) 12%, transparent);
          border: 1px solid color-mix(in srgb, var(--err) 40%, transparent);
          font-family: var(--mono); font-size: 13px; white-space: pre-wrap;
        }
        .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 10px; }
        .stat { background: var(--code); border: 1px solid var(--border); border-radius: 9px; padding: 9px 11px; }
        .stat-label { color: var(--muted); font-size: 11.5px; text-transform: uppercase; letter-spacing: .04em; }
        .stat-value { font-size: 15px; margin-top: 2px; word-break: break-word; }
        .table-scroll { overflow-x: auto; }
        table { border-collapse: collapse; width: 100%; font-size: 13px; }
        th, td { text-align: right; padding: 6px 10px; border-bottom: 1px solid var(--border); white-space: nowrap; }
        th:first-child, td:first-child { text-align: left; }
        th { color: var(--muted); font-weight: 600; }
        .toolbar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; position: sticky; top: 0; z-index: 5; }
        .toolbar button {
          background: var(--code); color: var(--text); border: 1px solid var(--border);
          border-radius: 8px; padding: 6px 12px; cursor: pointer; font: inherit; font-size: 13px;
        }
        .toolbar button:hover { border-color: var(--accent); }
        .toggle { font-size: 13px; color: var(--muted); display: flex; gap: 6px; align-items: center; }
        .spacer { margin-left: auto; font-size: 12.5px; }
        .muted { color: var(--muted); }
        .msg { margin-bottom: 14px; }
        .msg .role {
          font-size: 11.5px; text-transform: uppercase; letter-spacing: .06em;
          color: var(--muted); margin: 0 0 5px 4px;
        }
        .msg.tool .role { color: var(--warn); }
        .block {
          margin: 0; padding: 12px 14px; background: var(--code);
          border: 1px solid var(--border); border-radius: 10px;
          font-family: var(--mono); font-size: 12.5px; line-height: 1.5;
          white-space: pre-wrap; word-break: break-word; overflow-x: auto;
        }
        .msg.user > .block, .msg.user details.content[open] > .block { border-left: 3px solid var(--accent); }
        .msg.assistant > .block { border-left: 3px solid var(--ok); }
        details.content > summary {
          cursor: pointer; color: var(--muted); font-size: 13px; margin-bottom: 6px;
        }
        details.call {
          border: 1px solid var(--border); border-radius: 10px;
          margin: 8px 0 0; background: color-mix(in srgb, var(--accent) 5%, transparent);
        }
        details.call > summary {
          cursor: pointer; padding: 8px 12px; display: flex; gap: 10px;
          align-items: center; flex-wrap: wrap; font-size: 13px;
        }
        details.call > summary::marker { color: var(--accent); }
        .tool {
          font-family: var(--mono); font-weight: 600; color: var(--accent);
          background: color-mix(in srgb, var(--accent) 14%, transparent);
          padding: 1px 8px; border-radius: 999px; font-size: 12px;
        }
        .arg { font-family: var(--mono); font-size: 12px; color: var(--text); }
        .pill { margin-left: auto; font-size: 11px; color: var(--muted); }
        .pill.hit { color: var(--ok); }
        .io { padding: 0 12px 12px; }
        .io-title { font-size: 11.5px; text-transform: uppercase; letter-spacing: .05em; color: var(--muted); margin: 10px 0 5px; }
        .report .lead { font-size: 16px; font-weight: 600; margin: 0 0 10px; }
        .md { font-size: 14.5px; }
        .md p { margin: 8px 0; }
        .md ul, .md ol { margin: 8px 0; padding-left: 22px; }
        .md li { margin: 4px 0; }
        .md h2, .md h3, .md h4 {
          font-size: 15px; margin: 14px 0 6px; text-transform: none;
          letter-spacing: 0; color: var(--text);
        }
        .md code {
          background: var(--code); border: 1px solid var(--border); border-radius: 5px;
          padding: 1px 5px; font-family: var(--mono); font-size: 12.5px;
        }
        .md pre {
          background: var(--code); border: 1px solid var(--border); border-radius: 8px;
          padding: 10px 12px; overflow-x: auto;
        }
        .md pre code { background: transparent; border: 0; padding: 0; }
        .md a { color: var(--accent); }
        .coverage li { font-family: var(--mono); font-size: 12.5px; margin: 4px 0; }
        .md blockquote {
          margin: 8px 0; padding: 2px 12px; color: var(--muted);
          border-left: 3px solid var(--border);
        }
        </style>
        """;

    private const string Script = """
        <script>
        (function () {
          var all = function (selector) { return Array.prototype.slice.call(document.querySelectorAll(selector)); };
          document.getElementById('expand').addEventListener('click', function () {
            all('details').forEach(function (d) { d.open = true; });
          });
          document.getElementById('collapse').addEventListener('click', function () {
            all('details').forEach(function (d) { d.open = false; });
          });
          var only = document.getElementById('only-tools');
          only.addEventListener('change', function () {
            var on = only.checked;
            all('#conv .msg').forEach(function (m) {
              var keep = !on || m.getAttribute('data-role') === 'tool' || m.querySelector('.call') !== null;
              m.style.display = keep ? '' : 'none';
            });
          });
        })();
        </script>
        """;
}
