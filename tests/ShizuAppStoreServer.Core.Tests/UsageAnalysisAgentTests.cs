using Microsoft.Extensions.AI;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Enrichment.Repo;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class UsageAnalysisAgentTests : IDisposable
{
    private readonly string _root;
    private readonly RepoSnapshot _snapshot;
    private readonly RepoSearch _search;
    private readonly UsageAnalysisOptions _options = new();
    private readonly App _app = new()
    {
        Slug = "agentapp",
        Name = "AgentApp",
        Url = "https://github.com/example/repo",
        AddedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    public UsageAnalysisAgentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"shizu-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "app"));
        File.WriteAllLines(Path.Combine(_root, "app", "Installer.kt"),
        [
            "package com.example",
            "fun install(pm: PackageManager) {",
            "    pm.installPackage(\"pkg\")",
            "}",
        ]);
        File.WriteAllLines(Path.Combine(_root, "app", "Feature.kt"),
        [
            "package com.example",
            "class Feature {",
            "    fun feature() {",
            "        helper()",
            "    }",
            "}",
        ]);
        File.WriteAllLines(Path.Combine(_root, "app", "Helper.kt"),
        [
            "package com.example",
            "fun helper() {",
            "}",
        ]);
        Directory.CreateDirectory(Path.Combine(_root, "lib", "services"));
        File.WriteAllLines(Path.Combine(_root, "lib", "services", "shizuku_service.dart"),
        [
            "import 'package:shizuku_api/shizuku_api.dart';",
            "",
            "class ShizukuService {",
            "  static const MethodChannel _channel = MethodChannel('helperapp/shizuku');",
            "}",
        ]);
        _snapshot = new RepoSnapshot(RepoForge.GitHub, "example", "repo", "https://github.com/example/repo", "abc1234", null, _root);
        _search = new RepoSearch(_snapshot, _options);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        public int Calls;

        public List<ChatOptions?> Options { get; } = [];

        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Calls++;
            Options.Add(options);
            Requests.Add(messages.ToList());
            return Task.FromResult(responses[Math.Min(Calls - 1, responses.Length - 1)]);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedFactory(IChatClient client) : IUsageAnalysisChatClientFactory
    {
        public string? SessionId;

        public IChatClient Create(string sessionId)
        {
            SessionId = sessionId;
            return new UsageTrackingChatClient(client, null, sessionId, 20_000, TimeSpan.FromMinutes(10));
        }
    }

    private static ChatResponse ToolCall(string name, Dictionary<string, object?> arguments)
    {
        var content = new FunctionCallContent("call-1", name, arguments);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [content]))
        {
            Usage = new UsageDetails { InputTokenCount = 100, CachedInputTokenCount = 10, OutputTokenCount = 20 },
        };
    }

    private static ChatResponse ToolCall(string path) =>
        ToolCall("read_file", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["startLine"] = 1,
            ["endLine"] = 0,
        });

    private static ChatResponse Final(string text, ChatFinishReason? finish = null) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = 200, CachedInputTokenCount = 50, OutputTokenCount = 40 },
            FinishReason = finish,
        };

    private UsageAnalysisAgent NewAgent(IChatClient client) =>
        new(new ScriptedFactory(client), _options, null);

    private const string ValidJson = """{"short":"Can install apps.","markdown_usage":"Installs packages through a user service.\n\n- **Install apps**: packages are installed silently.","markdown_api_usage":"- `IPackageManager.installPackage`"}""";

    [Fact]
    public async Task InvokesToolsAndReturnsTheValidatedReport()
    {
        var client = new ScriptedChatClient(ToolCall("app/Installer.kt"), Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [], [], 1), _search);

        Assert.Equal(2, client.Calls);
        Assert.NotNull(result.Report);
        Assert.Equal("Can install apps.", result.Report.Short);
        Assert.Equal(1, result.ToolCalls);
        Assert.Equal(300, result.InputTokens);
        Assert.Equal(60, result.CachedInputTokens);
        Assert.Equal(60, result.OutputTokens);
    }

    [Fact]
    public async Task ToolSchemasAvoidNullableTypesTheEndpointRejects()
    {
        var client = new ScriptedChatClient(Final(ValidJson));
        var agent = NewAgent(client);

        await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [], [], 1), _search);

        var schemas = client.Options[0]!.Tools!.OfType<AIFunction>()
            .Select(f => f.JsonSchema.GetRawText())
            .ToList();
        Assert.Equal(6, schemas.Count);
        Assert.All(schemas, s => Assert.DoesNotContain("\"null\"", s));
        Assert.Contains(schemas, s => s.Contains("\"integer\""));
    }

    [Fact]
    public async Task TranscriptCapturesMessagesToolCallsResultsAndPerCallStats()
    {
        var client = new ScriptedChatClient(ToolCall("app/Installer.kt"), Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [], [], 1), _search);

        var transcript = Assert.IsType<UsageTranscript>(result.Transcript);
        Assert.Equal(["system", "user"], transcript.Messages.Take(2).Select(m => m.Role));
        var assistant = transcript.Messages.First(m => m.Calls.Count > 0);
        Assert.Equal("read_file", assistant.Calls[0].Name);
        Assert.Contains("Installer.kt", assistant.Calls[0].Arguments);
        var tool = transcript.Messages.First(m => m.ToolResult is not null);
        Assert.Equal("call-1", tool.ToolCallId);
        Assert.Contains("package com.example", tool.ToolResult);
        Assert.Equal(2, transcript.ModelCalls.Count);
        Assert.Equal(100, transcript.ModelCalls[0].InputTokens);
        Assert.Equal(200, transcript.ModelCalls[1].InputTokens);
        Assert.Equal(["read_file"], transcript.ModelCalls[0].Tools);
    }

    [Fact]
    public void TranscriptKeepsEveryResultOfABatchedToolCallMessage()
    {
        var transcript = new UsageTranscript();
        var message = new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("call-1", "first"),
            new FunctionResultContent("call-2", "second"),
        ]);

        transcript.AddMessage(message, 0);

        Assert.Equal(2, transcript.Messages.Count);
        Assert.Equal(["call-1", "call-2"], transcript.Messages.Select(m => m.ToolCallId));
        Assert.Equal(["first", "second"], transcript.Messages.Select(m => m.ToolResult));
    }

    [Fact]
    public async Task RetriesOnceWhenTheFirstAnswerFailsValidation()
    {
        var client = new ScriptedChatClient(
            Final("""{"short":"Bad","markdown_usage":"See `app/Installer.kt:3` for details."}"""),
            Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [], [], 1), _search);

        Assert.Equal(2, client.Calls);
        Assert.NotNull(result.Report);
        Assert.Equal(400, result.InputTokens);
    }

    [Fact]
    public async Task CorrectsWithToolsDisabledWhenTheModelLeaksToolSyntax()
    {
        var client = new ScriptedChatClient(
            Final("<tool_call><function=read_file><parameter=path>app/Installer.kt</parameter></function></tool_call>"),
            Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [], [], 1), _search);

        Assert.NotNull(result.Report);
        Assert.Equal(2, client.Calls);
        Assert.NotNull(client.Options[0]!.Tools);
        Assert.True(client.Options[1]!.Tools is null or { Count: 0 });
    }

    [Fact]
    public async Task AsksForAShorterReportWhenTheAnswerIsTruncated()
    {
        var client = new ScriptedChatClient(
            Final("""{"short":"Can install apps.","markdown_usage":"Installs via `PackageManager` and""", ChatFinishReason.Length),
            Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [], [], 1), _search);

        Assert.NotNull(result.Report);
        Assert.True(client.Options[1]!.Tools is null or { Count: 0 });
        Assert.Contains(result.Transcript!.Messages,
            m => m.Role == "user" && (m.Text ?? string.Empty).Contains("cut off"));
    }

    [Fact]
    public async Task ReturnsNoReportAfterRepeatedInvalidAnswers()
    {
        var client = new ScriptedChatClient(
            Final("nonsense"),
            Final("still nonsense"),
            Final("nonsense again"),
            Final("nonsense once more"),
            Final("nonsense forever"));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [], [], 1), _search);

        Assert.Null(result.Report);
        Assert.Equal(5, client.Calls);
    }

    [Fact]
    public async Task MemoizesIdenticalToolCalls()
    {
        var client = new ScriptedChatClient(
            ToolCall("app/Installer.kt"),
            ToolCall("app/Installer.kt"),
            Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [], [], 1), _search);

        Assert.NotNull(result.Report);
        Assert.Equal(3, client.Calls);
        var second = client.Requests[2].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();
        Assert.Contains(second, r => (r.Result?.ToString() ?? string.Empty).Contains("identical to call #1"));
    }

    [Fact]
    public async Task KeepsToolResultsBelowTheBatchThreshold()
    {
        var client = new ScriptedChatClient(Final("ok"));
        var tracking = new UsageTrackingChatClient(client, null, "test", 20_000, TimeSpan.FromMinutes(10));

        await tracking.GetResponseAsync(BuildToolConversation(40));

        var results = client.Requests[^1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();
        Assert.Equal(40, results.Count);
        Assert.Equal(0, PrunedCount(client.Requests[^1]));
    }

    [Fact]
    public async Task PrunesToolResultsInOneBigBatchAndAddsPacingNote()
    {
        var client = new ScriptedChatClient(Final("ok"));
        var tracking = new UsageTrackingChatClient(client, null, "test", 20_000, TimeSpan.FromMinutes(10));

        await tracking.GetResponseAsync(BuildToolConversation(48));
        Assert.Equal(16, PrunedCount(client.Requests[^1]));

        await tracking.GetResponseAsync(BuildToolConversation(49));
        Assert.Equal(16, PrunedCount(client.Requests[^1]));

        await tracking.GetResponseAsync(BuildToolConversation(64));
        Assert.Equal(32, PrunedCount(client.Requests[^1]));
        Assert.Contains(client.Requests[^1], m => m.Role == ChatRole.System && m.Text.Contains("[pacing]"));
    }

    private static int PrunedCount(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Count(r => (r.Result?.ToString() ?? string.Empty).Contains("(pruned tool result"));

    private static List<ChatMessage> BuildToolConversation(int results)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "start") };
        for (var i = 0; i < results; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"call-{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"app/File{i}.kt" })]));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{i}", $"payload {i}")]));
        }

        return messages;
    }

    private static ChatResponse Callers(string symbol) =>
        ToolCall("find_callers", new Dictionary<string, object?>
        {
            ["symbol"] = symbol,
            ["path"] = string.Empty,
            ["max"] = 0,
        });

    [Fact]
    public async Task KeepsInvestigatingUntilSurfaceEntriesAreTraced()
    {
        var client = new ScriptedChatClient(
            ToolCall("app/Feature.kt"),
            Final(ValidJson),
            Callers("helper"),
            Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [],
            [new UsageSurfaceEntry("entry", "Feature.feature", "helper", "app/Feature.kt", 4, "helper()")], 1), _search);

        Assert.NotNull(result.Report);
        Assert.Equal(4, client.Calls);
        Assert.Contains(client.Requests[2],
            m => m.Role == ChatRole.User
                && (m.Text ?? string.Empty).Contains("Coverage check")
                && (m.Text ?? string.Empty).Contains("Feature.kt:4"));
        Assert.NotNull(result.Coverage);
        Assert.Equal(1, result.Coverage.Rounds);
        Assert.Equal(1, result.Coverage.SurfaceEntries);
        Assert.Equal(1, result.Coverage.SurfaceInspected);
        Assert.Empty(result.Coverage.Uninspected);
        Assert.Equal(1, result.Coverage.SymbolToolCalls);
    }

    [Fact]
    public async Task AReportWithoutAnySymbolToolIsSentBackOnce()
    {
        var client = new ScriptedChatClient(
            ToolCall("app/Feature.kt"),
            Final(ValidJson),
            Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [],
            [new UsageSurfaceEntry("entry", "Feature.feature", "helper", "app/Feature.kt", 4, "helper()")], 1), _search);

        Assert.NotNull(result.Report);
        Assert.Equal(3, client.Calls);
        Assert.Contains(client.Requests[2],
            m => m.Role == ChatRole.User && (m.Text ?? string.Empty).Contains("Coverage check"));
        Assert.NotNull(result.Coverage);
        Assert.Equal(1, result.Coverage.Rounds);
        Assert.Equal(0, result.Coverage.SurfaceInspected);
        Assert.Single(result.Coverage.Uninspected);
    }

    [Fact]
    public async Task AdvisoryModeAcceptsAReadEntryAfterAnySymbolToolUse()
    {
        var client = new ScriptedChatClient(
            ToolCall("app/Feature.kt"),
            Callers("unrelated"),
            Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [],
            [new UsageSurfaceEntry("entry", "Feature.feature", "helper", "app/Feature.kt", 4, "helper()")], 1), _search);

        Assert.NotNull(result.Report);
        Assert.Equal(3, client.Calls);
        Assert.DoesNotContain(client.Requests[2],
            m => m.Role == ChatRole.User && (m.Text ?? string.Empty).Contains("Coverage check"));
        Assert.NotNull(result.Coverage);
        Assert.Equal(1, result.Coverage.SurfaceInspected);
        Assert.Empty(result.Coverage.Uninspected);
        Assert.Equal(1, result.Coverage.SymbolToolCalls);
    }

    [Fact]
    public async Task NarrowTraceRequirementSkipsBridges()
    {
        _options.RequireTracing = true;
        var client = new ScriptedChatClient(
            ToolCall("app/Feature.kt"),
            Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [],
            [new UsageSurfaceEntry("bridge", "Feature.feature", "helper", "app/Feature.kt", 4, "helper()")], 1), _search);

        Assert.NotNull(result.Report);
        Assert.Equal(2, client.Calls);
        Assert.NotNull(result.Coverage);
        Assert.Equal(1, result.Coverage.SurfaceInspected);
        Assert.Empty(result.Coverage.Uninspected);
    }

    [Fact]
    public async Task ServiceEntriesRequireATraceWhenEnabled()
    {
        _options.RequireTracing = true;
        var client = new ScriptedChatClient(
            ToolCall("app/Helper.kt"),
            Final(ValidJson),
            Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [],
            [new UsageSurfaceEntry("service", "Helper.helper", "ShizukuUserService", "app/Helper.kt", 2, "ShizukuUserService()")], 1), _search);

        Assert.NotNull(result.Report);
        Assert.Equal(3, client.Calls);
        Assert.Contains(client.Requests[2],
            m => m.Role == ChatRole.User && (m.Text ?? string.Empty).Contains("Coverage check"));
        Assert.NotNull(result.Coverage);
        Assert.Equal(1, result.Coverage.Rounds);
        Assert.Equal(0, result.Coverage.SurfaceInspected);
        Assert.Single(result.Coverage.Uninspected);
    }

    [Fact]
    public async Task AcceptsTheReportWhenCoverageRoundsAreExhausted()
    {
        _options.MaxCoverageRounds = 1;
        var client = new ScriptedChatClient(
            Final(ValidJson),
            Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [],
            [new UsageSurfaceEntry("entry", "Feature.feature", "helper", "app/Feature.kt", 4, "helper()")], 1), _search);

        Assert.NotNull(result.Report);
        Assert.Equal(2, client.Calls);
        Assert.NotNull(result.Coverage);
        Assert.Equal(1, result.Coverage.Rounds);
        Assert.Equal(0, result.Coverage.SurfaceInspected);
        Assert.Single(result.Coverage.Uninspected);
    }

    [Fact]
    public async Task PackageEntriesDoNotGateTheReport()
    {
        var client = new ScriptedChatClient(Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [],
            [new UsageSurfaceEntry("package", string.Empty, "shizuku_api", "pubspec.yaml", 3, "shizuku_api: ^1.2.3")], 1), _search);

        Assert.NotNull(result.Report);
        Assert.Equal(1, client.Calls);
        Assert.NotNull(result.Coverage);
        Assert.Equal(0, result.Coverage.SurfaceEntries);
        Assert.Empty(result.Coverage.Uninspected);
    }

    [Fact]
    public async Task NonJvmBridgeEntriesAreSatisfiedByARead()
    {
        var client = new ScriptedChatClient(
            Final(ValidJson),
            ToolCall("lib/services/shizuku_service.dart"),
            Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [],
            [new UsageSurfaceEntry("bridge", string.Empty, "MethodChannel", "lib/services/shizuku_service.dart", 4, "MethodChannel('helperapp/shizuku')")], 1), _search);

        Assert.NotNull(result.Report);
        Assert.Equal(3, client.Calls);
        Assert.Contains(client.Requests[1],
            m => m.Role == ChatRole.User && (m.Text ?? string.Empty).Contains("Coverage check"));
        Assert.NotNull(result.Coverage);
        Assert.Equal(1, result.Coverage.Rounds);
        Assert.Equal(1, result.Coverage.SurfaceInspected);
        Assert.Empty(result.Coverage.Uninspected);
    }

    [Fact]
    public async Task BridgeEntriesAcceptAReadOrATrace()
    {
        var client = new ScriptedChatClient(
            Final(ValidJson),
            Callers("ShizukuBinderWrapper"),
            Final(ValidJson));
        var agent = NewAgent(client);

        var result = await agent.AnalyzeAsync(_app, new UsageContext(
            _app.Slug, _app.Name, "com.example", "1.0", "demo", [],
            [new UsageSurfaceEntry("bridge", "ShizukuBridge.bridge", "ShizukuBinderWrapper", "app/src/main/java/com/example/ShizukuBridge.kt", 5, "ShizukuBinderWrapper(binder)")], 1), _search);

        Assert.NotNull(result.Report);
        Assert.Equal(3, client.Calls);
        Assert.Contains(client.Requests[1],
            m => m.Role == ChatRole.User && (m.Text ?? string.Empty).Contains("Coverage check"));
        Assert.NotNull(result.Coverage);
        Assert.Equal(1, result.Coverage.Rounds);
        Assert.Equal(1, result.Coverage.SurfaceInspected);
        Assert.Empty(result.Coverage.Uninspected);
    }
}
