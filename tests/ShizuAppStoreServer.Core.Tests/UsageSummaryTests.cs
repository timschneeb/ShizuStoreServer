using System.Net;
using System.Net.Http;
using ShizuAppStoreServer.Core.Enrichment;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class UsageSummaryTests
{
    private static UsageSummaryBundle Bundle(
        IReadOnlyList<string>? managers = null,
        string? apiForm = null,
        IReadOnlyList<string>? capabilities = null,
        bool optional = false,
        IReadOnlyList<UsageEvidence>? evidence = null) =>
        new("com.example.app", "1.2.3", 42, "abc123",
            managers ?? ["shizuku"], apiForm, capabilities ?? [], optional, evidence ?? []);

    [Fact]
    public void TemplateNamesManagersAndCapabilities()
    {
        var result = UsageSummaryTemplate.Build(Bundle(capabilities: ["install", "freeze"]));

        Assert.Equal("template-v1", result.Model);
        Assert.Equal("This app can use Shizuku to install or update apps or freeze or disable apps.", result.Text);
    }

    [Fact]
    public void TemplateHandlesEmptyAndOptionalUsage()
    {
        Assert.Equal("No Shizuku usage was found.", UsageSummaryTemplate.Build(Bundle(managers: [])).Text);

        var optional = UsageSummaryTemplate.Build(Bundle(optional: true, capabilities: ["uninstall"]));
        Assert.Contains("optional", optional.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void HashIsIndependentOfEvidenceOrder()
    {
        var first = Bundle(evidence: [UsageEvidence.Strong("command", "pm install", "apk")]);
        var second = Bundle(evidence: [UsageEvidence.Strong("command", "pm install", "apk")]);

        Assert.Equal(first.Hash(), second.Hash());
        Assert.Equal(64, first.Hash().Length);
    }

    [Fact]
    public void HashChangesWithEvidence()
    {
        var first = Bundle(evidence: [UsageEvidence.Strong("command", "pm install", "apk")]);
        var second = Bundle(evidence: [UsageEvidence.Strong("command", "pm uninstall", "apk")]);

        Assert.NotEqual(first.Hash(), second.Hash());
    }

    [Fact]
    public void ValidatorKeepsOnlyGroundedClaims()
    {
        var bundle = Bundle(evidence: [UsageEvidence.Strong("command", "pm install", "apk")]);
        const string content = """
            {"summary":"Can install apps.","claims":[
              {"text":"Installs APKs.","evidence":["e1"]},
              {"text":"Reboots the device.","evidence":["e9"]}]}
            """;

        var result = UsageSummaryValidator.Validate(content, bundle);

        Assert.NotNull(result);
        Assert.Contains("Can install apps.", result, StringComparison.Ordinal);
        Assert.Contains("- Installs APKs.", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Reboots", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorSanitizesEmDashAndTruncates()
    {
        var bundle = Bundle(evidence: [UsageEvidence.Strong("command", "pm install", "apk")]);
        var longText = new string('a', 300);
        var content = $$"""{"summary":"Uses Shizuku — maybe. {{longText}}"}""";

        var result = UsageSummaryValidator.Validate(content, bundle);

        Assert.NotNull(result);
        Assert.DoesNotContain("—", result, StringComparison.Ordinal);
        Assert.True(result.Length <= 600);
    }

    [Fact]
    public void ValidatorRejectsUnparseableAndUngroundedContent()
    {
        var bundle = Bundle(evidence: [UsageEvidence.Strong("command", "pm install", "apk")]);

        Assert.Null(UsageSummaryValidator.Validate("not json", bundle));
        Assert.Null(UsageSummaryValidator.Validate("""{"claims":[{"text":"x","evidence":["e7"]}]}""", bundle));
        Assert.Null(UsageSummaryValidator.Validate(null, bundle));
    }

    [Fact]
    public async Task GeneratorStaysDisabledWithoutConfiguration()
    {
        var generator = new OpenAiUsageSummaryGenerator(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            new EnrichmentOptions());

        Assert.False(generator.Enabled);
        Assert.Null(await generator.GenerateAsync(Bundle()));
    }

    [Fact]
    public async Task GeneratorReturnsValidatedSummary()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":"{\"summary\":\"Can install apps.\",\"claims\":[]}"}}]}"""),
        });
        var options = new EnrichmentOptions
        {
            UsageSummaryBaseUrl = "https://ai.example/v1",
            UsageSummaryModel = "small-model",
            UsageSummaryApiKey = "secret",
        };
        var generator = new OpenAiUsageSummaryGenerator(new HttpClient(handler), options);
        var bundle = Bundle(evidence: [UsageEvidence.Strong("command", "pm install", "apk")]);

        var result = await generator.GenerateAsync(bundle);

        Assert.NotNull(result);
        Assert.Equal("Can install apps.", result.Text);
        Assert.Equal("small-model", result.Model);
        Assert.Equal("https://ai.example/v1/chat/completions", handler.Uris.Single());
        Assert.Equal("Bearer secret", handler.Authorization);
    }

    [Fact]
    public async Task GeneratorReturnsNullWithoutStrongEvidence()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var options = new EnrichmentOptions
        {
            UsageSummaryBaseUrl = "https://ai.example/v1",
            UsageSummaryModel = "small-model",
        };
        var generator = new OpenAiUsageSummaryGenerator(new HttpClient(handler), options);
        var weakOnly = Bundle(evidence: [UsageEvidence.Weak("fallback", "ACTION_INSTALL_PACKAGE", "apk")]);

        Assert.Null(await generator.GenerateAsync(weakOnly));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GeneratorStopsAtTheDailyBudget()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":"{\"summary\":\"Can install apps.\",\"claims\":[]}"}}]}"""),
        });
        var options = new EnrichmentOptions
        {
            UsageSummaryBaseUrl = "https://ai.example/v1",
            UsageSummaryModel = "small-model",
            UsageSummaryMaxPerDay = 1,
        };
        var generator = new OpenAiUsageSummaryGenerator(new HttpClient(handler), options);
        var bundle = Bundle(evidence: [UsageEvidence.Strong("command", "pm install", "apk")]);

        Assert.NotNull(await generator.GenerateAsync(bundle));
        Assert.Null(await generator.GenerateAsync(bundle));
        Assert.Equal(1, handler.Calls);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<string> Uris { get; } = [];

        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Uris.Add(request.RequestUri!.ToString());
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(handler(request));
        }
    }
}
