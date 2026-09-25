using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// Everything the summarizer is allowed to know about an app's Shizuku usage.
/// The bundle is deterministic and carries stable evidence ids so generated
/// claims can be validated against it.
/// </summary>
public sealed record UsageSummaryBundle(
    string Package,
    string? VersionName,
    long? VersionCode,
    string? Sha256,
    IReadOnlyList<string> Managers,
    string? ApiForm,
    IReadOnlyList<string> Capabilities,
    bool Optional,
    IReadOnlyList<UsageEvidence> Evidence)
{
    public IReadOnlyList<UsageEvidence> OrderedEvidence() => Evidence
        .OrderBy(e => e.Kind, StringComparer.Ordinal)
        .ThenBy(e => e.Value, StringComparer.Ordinal)
        .ThenBy(e => e.Source, StringComparer.Ordinal)
        .ThenBy(e => e.Confidence, StringComparer.Ordinal)
        .ToList();

    public string CanonicalJson()
    {
        var ordered = OrderedEvidence();
        var evidence = new JsonArray();
        for (var i = 0; i < ordered.Count; i++)
        {
            evidence.Add(new JsonObject
            {
                ["id"] = $"e{i + 1}",
                ["kind"] = ordered[i].Kind,
                ["value"] = ordered[i].Value,
                ["source"] = ordered[i].Source,
                ["confidence"] = ordered[i].Confidence,
            });
        }

        var root = new JsonObject
        {
            ["package"] = Package,
            ["versionName"] = VersionName,
            ["versionCode"] = VersionCode,
            ["sha256"] = Sha256,
            ["managers"] = new JsonArray(Managers.Select(m => (JsonNode?)m).ToArray()),
            ["apiForm"] = ApiForm,
            ["capabilities"] = new JsonArray(Capabilities.Select(c => (JsonNode?)c).ToArray()),
            ["optional"] = Optional,
            ["evidence"] = evidence,
        };
        return root.ToJsonString();
    }

    public string Hash()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson()));
        return Convert.ToHexStringLower(bytes);
    }
}

public sealed record UsageSummaryResult(string Text, string Model);

public interface IUsageSummaryGenerator
{
    bool Enabled { get; }

    Task<UsageSummaryResult?> GenerateAsync(UsageSummaryBundle bundle, CancellationToken ct = default);
}

/// <summary>
/// Deterministic fallback summary. Always available, no network, no model.
/// </summary>
public static class UsageSummaryTemplate
{
    public const string ModelId = "template-v1";

    private static readonly Dictionary<string, string> ManagerNames = new(StringComparer.Ordinal)
    {
        ["shizuku"] = "Shizuku",
        ["dhizuku"] = "Dhizuku",
        ["sui"] = "Sui",
        ["root"] = "root",
    };

    private static readonly Dictionary<string, string> CapabilityPhrases = new(StringComparer.Ordinal)
    {
        ["install"] = "install or update apps",
        ["uninstall"] = "uninstall apps",
        ["freeze"] = "freeze or disable apps",
        ["appops"] = "change app permissions",
        ["system_settings"] = "change system and power settings",
        ["process"] = "control running processes",
        ["diagnostics"] = "read diagnostic system information",
        ["reboot"] = "reboot the device",
        ["wireless_adb"] = "pair over wireless ADB",
        ["compile"] = "recompile installed apps",
    };

    public static UsageSummaryResult Build(UsageSummaryBundle bundle)
    {
        var managers = bundle.Managers
            .Select(m => ManagerNames.TryGetValue(m, out var name) ? name : m)
            .ToList();
        var capabilities = bundle.Capabilities
            .Select(c => CapabilityPhrases.TryGetValue(c, out var phrase) ? phrase : c)
            .ToList();

        var text = new StringBuilder();
        if (managers.Count == 0)
        {
            text.Append("No Shizuku usage was found.");
        }
        else
        {
            text.Append("This app can use ");
            text.Append(JoinList(managers));
            if (capabilities.Count > 0)
            {
                text.Append(" to ");
                text.Append(JoinList(capabilities));
            }
            text.Append('.');
            if (bundle.Optional)
            {
                text.Append(" Shizuku appears to be optional; the app ships fallbacks.");
            }
        }

        return new UsageSummaryResult(text.ToString(), ModelId);
    }

    private static string JoinList(IReadOnlyList<string> values) => values.Count switch
    {
        1 => values[0],
        2 => $"{values[0]} or {values[1]}",
        _ => string.Join(", ", values.Take(values.Count - 1)) + " or " + values[^1],
    };
}

/// <summary>
/// Validates model output against the evidence bundle. Claims without a known
/// evidence id are dropped; output without any grounded claim is rejected.
/// </summary>
public static class UsageSummaryValidator
{
    private const int MaxSummaryChars = 400;
    private const int MaxClaimChars = 240;
    private const int MaxTotalChars = 600;

    public static string? Validate(string? content, UsageSummaryBundle bundle)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var known = bundle.OrderedEvidence()
            .Select((e, i) => $"e{i + 1}")
            .ToHashSet(StringComparer.Ordinal);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject root)
        {
            return null;
        }

        var summary = Sanitize(root["summary"]?.GetValue<string>());
        if (summary is { Length: > MaxSummaryChars })
        {
            summary = summary[..MaxSummaryChars];
        }

        var claims = new List<string>();
        if (root["claims"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonObject claim)
                {
                    continue;
                }

                var claimText = Sanitize(claim["text"]?.GetValue<string>());
                if (string.IsNullOrWhiteSpace(claimText) || claimText.Length > MaxClaimChars)
                {
                    continue;
                }

                var evidence = claim["evidence"] as JsonArray;
                var grounded = evidence is not null && evidence.Any(id =>
                    id is not null && known.Contains(id.GetValue<string>()));
                if (!grounded)
                {
                    continue;
                }

                claims.Add(claimText);
            }
        }

        if (string.IsNullOrWhiteSpace(summary) && claims.Count == 0)
        {
            return null;
        }

        var result = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            result.Append(summary);
        }

        foreach (var claim in claims)
        {
            if (result.Length > 0)
            {
                result.Append('\n');
            }

            result.Append("- ").Append(claim);
        }

        var text = result.ToString();
        if (text.Length > MaxTotalChars)
        {
            text = text[..MaxTotalChars];
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? Sanitize(string? value) => value?
        .Replace('\u2014', '-')
        .ReplaceLineEndings(" ")
        .Trim();
}

/// <summary>
/// OpenAI-compatible chat completions client. Disabled (returns null) when no
/// base URL or model is configured, over the daily budget, or on any failure;
/// callers fall back to <see cref="UsageSummaryTemplate"/>.
/// </summary>
public sealed class OpenAiUsageSummaryGenerator(
    HttpClient http,
    EnrichmentOptions options,
    ILogger<OpenAiUsageSummaryGenerator>? log = null) : IUsageSummaryGenerator
{
    private const string SystemPrompt =
        "You summarize how an Android app uses Shizuku, Dhizuku, Sui or root from an evidence bundle. " +
        "Reply with JSON only: {\"summary\": string, \"claims\": [{\"text\": string, \"evidence\": [string]}]}. " +
        "Every claim must cite one or more evidence ids from the bundle. " +
        "Only state what the evidence supports; say that the app can do something, never that it does. " +
        "Do not speculate about intent or safety. Keep the summary under 400 characters.";

    private readonly object _budgetLock = new();
    private DateOnly _budgetDay;
    private int _usedToday;

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(options.UsageSummaryBaseUrl) &&
        !string.IsNullOrWhiteSpace(options.UsageSummaryModel);

    public async Task<UsageSummaryResult?> GenerateAsync(
        UsageSummaryBundle bundle,
        CancellationToken ct = default)
    {
        if (!Enabled || !bundle.Evidence.Any(e => e.IsStrong) || !TryConsumeBudget())
        {
            return null;
        }

        try
        {
            var payload = new JsonObject
            {
                ["model"] = options.UsageSummaryModel,
                ["temperature"] = 0,
                ["max_tokens"] = 400,
                ["response_format"] = new JsonObject { ["type"] = "json_object" },
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = bundle.CanonicalJson() },
                },
            };

            var url = options.UsageSummaryBaseUrl!.TrimEnd('/') + "/chat/completions";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(options.UsageSummaryApiKey))
            {
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.UsageSummaryApiKey);
            }

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                log?.LogDebug("usage summary request failed with {Status}", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var content = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            var text = UsageSummaryValidator.Validate(content, bundle);
            if (text is null)
            {
                log?.LogDebug("usage summary rejected: {Package}", bundle.Package);
                return null;
            }

            return new UsageSummaryResult(text, options.UsageSummaryModel!);
        }
        catch (HttpRequestException ex)
        {
            log?.LogDebug(ex, "usage summary request failed");
            return null;
        }
        catch (JsonException ex)
        {
            log?.LogDebug(ex, "usage summary response was not valid JSON");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log?.LogDebug("usage summary request timed out");
            return null;
        }
    }

    private bool TryConsumeBudget()
    {
        lock (_budgetLock)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (today != _budgetDay)
            {
                _budgetDay = today;
                _usedToday = 0;
            }

            if (_usedToday >= options.UsageSummaryMaxPerDay)
            {
                return false;
            }

            _usedToday++;
            return true;
        }
    }
}
