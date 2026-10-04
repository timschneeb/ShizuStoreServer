using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Jobs;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>Classifier outcome plus token accounting for the run record.</summary>
public sealed record UseCaseTagResult(
    UseCaseTaggingReport? Report,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    int ModelCalls,
    string? Error = null);

public interface IUseCaseClassifier
{
    Task<UseCaseTagResult> ClassifyAsync(
        App app, IReadOnlyList<UseCase> vocabulary, CancellationToken ct = default);
}

/// <summary>
/// Maps one stored usage report onto the use-case vocabulary with a single
/// cheap model call. Runs separately from the repo analysis so a vocabulary
/// or prompt change re-tags every app without cloning repositories again.
/// </summary>
public sealed class UseCaseClassifier(
    UsageAnalysisOptions options,
    IUsageAnalysisChatClientFactory clients,
    ILogger<UseCaseClassifier>? log = null) : IUseCaseClassifier
{
    private const int MaxCorrections = 1;

    private const string SystemPrompt = """
        You classify how an Android app uses Shizuku into a fixed vocabulary of use case tags.

        You receive a JSON object: the app identity, the app's already validated Shizuku usage report (summary, capability text, Android APIs or commands used, notable details) and the use case vocabulary as slug, name and definition entries.

        Rules:
        - Reply with JSON only, no prose outside it: {"use_cases": ["slug", ...], "proposed_use_cases": [{"name": "...", "reason": "..."}]}
        - Assign a tag only when the report proves the app performs that capability itself. A tag always means the app acts on other apps or on system state, never on itself.
        - Self-scoped behavior never qualifies: installing or updating only the app's own package, backing up or restoring only its own data, changing only its own settings, or otherwise managing itself. For example an app whose only silent-install behavior is updating itself must not receive install-apps.
        - Choose tags only from useCaseVocabulary and order them by relevance. Never invent, translate or pluralize slugs.
        - Use an empty array when no vocabulary entry fits; never stretch a tag to fill the list.
        - proposed_use_cases covers only a significant Shizuku capability the report proves that no vocabulary entry covers. Name it in plain English with two to four words and give a one sentence reason grounded in the report. Do not propose near synonyms of vocabulary entries, setup or permission steps, helper mechanics, or self-scoped behavior. Use an empty array when unsure.
        """;

    public async Task<UseCaseTagResult> ClassifyAsync(
        App app, IReadOnlyList<UseCase> vocabulary, CancellationToken ct = default)
    {
        if (!options.TaggingEnabled || !options.IsConfigured || vocabulary.Count == 0)
        {
            return new UseCaseTagResult(null, 0, 0, 0, 0, "tagging is not configured");
        }

        var allowed = vocabulary.Select(v => v.Slug).ToArray();
        var commit = app.UsageCommit is { Length: > 12 } c ? c[..12] : app.UsageCommit;
        var session = $"shizu-tags-{app.Slug}-{commit ?? "0"}-p{options.TagPromptVersion}";
        var tracking = clients.Create(session);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User,
                "App usage report (JSON):\n" + BuildPayload(app, vocabulary).ToJsonString()
                + $"\n\nAssign at most {options.MaxTagsPerApp} tags and propose at most {options.MaxProposalsPerApp} new ones, then reply with the JSON object."),
        };

        var chatOptions = new ChatOptions
        {
            Temperature = 0,
            // Reasoning models spend output tokens on hidden reasoning; give the
            // classifier the full configured budget like the analysis agent.
            MaxOutputTokens = options.MaxOutputTokens,
        };

        if (string.Equals(options.Protocol, "responses", StringComparison.OrdinalIgnoreCase))
        {
            // The Responses API stores responses by default and chains follow-ups
            // with previous_response_id; an endpoint that does not retain them
            // rejects the second call. Disable storage so every request stands alone.
#pragma warning disable OPENAI001
            chatOptions.RawRepresentationFactory = static _ => new CreateResponseOptions { StoredOutputEnabled = false };
#pragma warning restore OPENAI001
        }

        string? rejection = null;
        for (var call = 0; call <= MaxCorrections; call++)
        {
            if (rejection is not null)
            {
                messages.Add(new ChatMessage(ChatRole.User,
                    $"Your previous answer was rejected: {rejection}. Reply with the JSON object only, fixing that issue. "
                    + "Use only slugs from useCaseVocabulary in use_cases, and never repeat a vocabulary slug in proposed_use_cases."));
            }

            ChatResponse response;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.RequestTimeout);
            try
            {
                response = await tracking.GetResponseAsync(messages, chatOptions, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log?.LogDebug("Use case classification for {Slug} exceeded {Timeout}.", app.Slug, options.RequestTimeout);
                return Result(null, tracking, $"classification timed out after {options.RequestTimeout}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.LogWarning(ex, "Use case classification model call failed for {Slug}.", app.Slug);
                return Result(null, tracking, $"model call failed: {ex.Message}");
            }

            messages.AddRange(response.Messages);
            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                rejection = "the response was empty";
                continue;
            }

            var report = UseCaseTagValidator.Validate(
                text, allowed, options.MaxTagsPerApp, options.MaxProposalsPerApp, out var error);
            if (report is not null)
            {
                JobContext.Current?.Event(
                    JobEventLevel.Debug,
                    JobEventType.AiResult,
                    $"classified {report.UseCases.Count} use case tag(s), {report.Proposals.Count} proposal(s)",
                    data: new { tags = report.UseCases, proposals = report.Proposals.Select(p => p.Slug) });
                return Result(report, tracking, null);
            }

            rejection = error;
            JobContext.Current?.Event(
                JobEventLevel.Debug,
                JobEventType.AiValidation,
                $"use case answer rejected: {error}",
                data: new { error });
        }

        return Result(null, tracking, rejection ?? "classification failed");
    }

    private static JsonObject BuildPayload(App app, IReadOnlyList<UseCase> vocabulary)
    {
        var terms = new JsonArray();
        foreach (var term in vocabulary.OrderBy(v => v.Slug, StringComparer.Ordinal))
        {
            terms.Add(new JsonObject
            {
                ["slug"] = term.Slug,
                ["name"] = term.Name,
                ["definition"] = term.Definition,
            });
        }

        return new JsonObject
        {
            ["app"] = new JsonObject
            {
                ["slug"] = app.Slug,
                ["name"] = app.Name,
                ["package"] = app.PackageName,
            },
            ["usageSummary"] = app.UsageShort,
            ["usage"] = app.UsageMarkdownUsage ?? app.UsageMarkdown,
            ["androidApis"] = app.UsageMarkdownApiUsage,
            ["notableDetails"] = app.UsageMarkdownNotableDetails,
            ["useCaseVocabulary"] = terms,
        };
    }

    private static UseCaseTagResult Result(UseCaseTaggingReport? report, IChatClient tracking, string? error)
    {
        var input = tracking is UsageTrackingChatClient t ? t.InputTokens : 0;
        var cached = tracking is UsageTrackingChatClient t2 ? t2.CachedInputTokens : 0;
        var output = tracking is UsageTrackingChatClient t3 ? t3.OutputTokens : 0;
        var calls = tracking is UsageTrackingChatClient t4 ? t4.ModelCalls : 0;
        return new UseCaseTagResult(report, input, cached, output, calls, error);
    }
}
