using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class UsageLogWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shizu-logs-{Guid.NewGuid():N}");
    private readonly UsageAnalysisOptions _options;

    public UsageLogWriterTests()
    {
        _options = new UsageAnalysisOptions { Enabled = true, LogPath = _dir };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static UsageTranscript Transcript()
    {
        var transcript = new UsageTranscript();
        transcript.AddMessage(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>
            {
                ["path"] = "app/Installer.kt",
                ["startLine"] = 1,
                ["endLine"] = 0,
            }),
        ]), 20_000);
        transcript.AddMessage(new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("call-1", "<script>alert(1)</script> package com.example"),
        ]), 20_000);
        transcript.AddModelCall(new UsageTranscriptModelCall(1, 1.5, 1000, 200, 300, "tool_calls", ["read_file"]));
        return transcript;
    }

    private static UsageAnalysisRun NewRun() => new()
    {
        Id = 42,
        AppId = 7,
        Status = UsageAnalysisStatus.Succeeded,
        Attempts = 1,
        Model = "mimo-v2.6-flash",
        PromptVersion = 1,
        CostUsd = 0.0123m,
        ToolCalls = 1,
        CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2),
        StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2),
        FinishedAt = DateTimeOffset.UtcNow,
    };

    private static App NewApp() => new()
    {
        Id = 7,
        Slug = "demo",
        Name = "Demo",
        PackageName = "com.demo",
        Url = "https://github.com/example/repo",
    };

    [Fact]
    public async Task WritesJsonAndInteractiveHtmlWithEscapedContent()
    {
        var writer = new UsageAnalysisLogWriter(_options, NullLogger<UsageAnalysisLogWriter>.Instance);

        var file = await writer.WriteAsync(
            NewRun(), NewApp(), "1.2.3", DateTimeOffset.UtcNow,
            new UsageAgentResult(null, 1000, 200, 300, 3, 2, false, Transcript()), "boom");

        Assert.NotNull(file);
        Assert.EndsWith(".html", file);
        Assert.True(File.Exists(Path.Combine(_dir, file)));
        var html = await File.ReadAllTextAsync(Path.Combine(_dir, file));
        Assert.Contains("Demo", html);
        Assert.Contains("boom", html);
        Assert.Contains("read_file", html);
        Assert.Contains("$0.012300", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);

        var jsonPath = Path.Combine(_dir, Path.ChangeExtension(file, ".json"));
        var document = JsonSerializer.Deserialize<UsageLogDocument>(
            await File.ReadAllTextAsync(jsonPath), UsageAnalysisLogWriter.Json);
        Assert.NotNull(document);
        Assert.Equal("demo", document.Slug);
        Assert.Equal("1.2.3", document.VersionName);
        Assert.Equal("succeeded", document.Status);
        Assert.Equal(2, document.Messages.Count);
        Assert.Single(document.ModelCalls);
    }

    [Fact]
    public async Task RendersCoverageStatsAndUninspectedLocations()
    {
        var writer = new UsageAnalysisLogWriter(_options, NullLogger<UsageAnalysisLogWriter>.Instance);
        var coverage = new UsageCoverage(5, 3, 4, 2, 1, ["call site app/Feature.kt:4: feature calls helper"]);

        var file = await writer.WriteAsync(
            NewRun(), NewApp(), "1.2.3", DateTimeOffset.UtcNow,
            new UsageAgentResult(null, 1000, 200, 300, 3, 2, false, Transcript(), coverage), null);

        var html = await File.ReadAllTextAsync(Path.Combine(_dir, file!));
        Assert.Contains("Surface inspected", html);
        Assert.Contains("3 / 5 (60%)", html);
        Assert.Contains("Call sites read", html);
        Assert.Contains("2 / 4 (50%)", html);
        Assert.Contains("Coverage rounds", html);
        Assert.Contains("still uninspected", html);
        Assert.Contains("Feature.kt:4", html);

        var jsonPath = Path.Combine(_dir, Path.ChangeExtension(file!, ".json"));
        var document = JsonSerializer.Deserialize<UsageLogDocument>(
            await File.ReadAllTextAsync(jsonPath), UsageAnalysisLogWriter.Json);
        Assert.NotNull(document);
        Assert.NotNull(document.Coverage);
        Assert.Equal(5, document.Coverage.SurfaceEntries);
        Assert.Equal(3, document.Coverage.SurfaceInspected);
        Assert.Equal(1, document.Coverage.Rounds);
        Assert.Single(document.Coverage.Uninspected);
    }

    [Fact]
    public async Task RendersTheReportAsMarkdown()
    {
        var writer = new UsageAnalysisLogWriter(_options, NullLogger<UsageAnalysisLogWriter>.Instance);
        var report = new UsageReport(
            "Can toggle Wi-Fi using shell commands.",
            "The app uses Shizuku to run shell commands.\n\n"
            + "- **Toggle Wi-Fi** using `svc wifi`\n"
            + "- Change density using `wm density`\n",
            "- `svc wifi`\n- `wm density`",
            "Wi-Fi toggling needs the location permission on some devices.");

        var file = await writer.WriteAsync(
            NewRun(), NewApp(), "1.2.3", DateTimeOffset.UtcNow,
            new UsageAgentResult(report, 1000, 200, 300, 3, 2, false, Transcript()), null);

        var html = await File.ReadAllTextAsync(Path.Combine(_dir, file!));
        Assert.Contains("<p class=\"lead\">Can toggle Wi-Fi using shell commands.</p>", html);
        Assert.Contains("<div class=\"md\">", html);
        Assert.Contains("<li><strong>Toggle Wi-Fi</strong>", html);
        Assert.Contains("<code>svc wifi</code>", html);
        Assert.Contains("Android APIs or commands used", html);
        Assert.Contains("Notable details", html);

        File.Delete(Path.Combine(_dir, Path.ChangeExtension(file!, ".html")));
        Assert.Equal(1, UsageAnalysisLogWriter.RenderDirectory(_dir, NullLogger.Instance));
        var regenerated = await File.ReadAllTextAsync(Path.Combine(_dir, Path.ChangeExtension(file!, ".html")));
        Assert.Contains("<li><strong>Toggle Wi-Fi</strong>", regenerated);
        Assert.Contains("Android APIs or commands used", regenerated);
    }

    [Fact]
    public async Task RendersLegacyStoredReportsWithTheOldMarkdownMember()
    {
        var json = """
            {"version":1,"runId":7,"appId":3,"slug":"demo","appName":"Demo","status":"Succeeded",
             "error":null,"startedAt":"2026-09-01T00:00:00+00:00","finishedAt":"2026-09-01T00:01:00+00:00",
             "toolCalls":2,"messages":[],"modelCalls":[],
             "report":{"short":"Can toggle Wi-Fi.","markdown":"The app runs `svc wifi`."}}
            """;
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "20260901-000000-demo-r7.json"), json);

        Assert.Equal(1, UsageAnalysisLogWriter.RenderDirectory(_dir, NullLogger.Instance));

        var html = await File.ReadAllTextAsync(Path.Combine(_dir, "20260901-000000-demo-r7.html"));
        Assert.Contains("Can toggle Wi-Fi.", html);
        Assert.Contains("svc wifi", html);
    }

    [Fact]
    public async Task RenderDirectoryRegeneratesPagesFromStoredJson()
    {
        var writer = new UsageAnalysisLogWriter(_options, NullLogger<UsageAnalysisLogWriter>.Instance);
        var file = await writer.WriteAsync(
            NewRun(), NewApp(), null, DateTimeOffset.UtcNow,
            new UsageAgentResult(null, 10, 0, 5, 0, 1, false, Transcript()), null);
        var htmlPath = Path.Combine(_dir, Path.ChangeExtension(file!, ".html"));
        File.Delete(htmlPath);

        Assert.Equal(1, UsageAnalysisLogWriter.RenderDirectory(_dir, NullLogger.Instance));
        Assert.True(File.Exists(htmlPath));
    }

    [Fact]
    public async Task DisabledLogPathWritesNothing()
    {
        _options.LogPath = null;
        var writer = new UsageAnalysisLogWriter(_options, NullLogger<UsageAnalysisLogWriter>.Instance);

        var file = await writer.WriteAsync(
            NewRun(), NewApp(), null, DateTimeOffset.UtcNow, null, "failed");

        Assert.Null(file);
    }
}
