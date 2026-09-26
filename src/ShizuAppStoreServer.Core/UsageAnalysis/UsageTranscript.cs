using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>One message of the model conversation, including tool traffic.</summary>
public sealed record UsageTranscriptMessage(
    string Role,
    string? Text,
    string? ToolCallId,
    string? ToolResult,
    IReadOnlyList<UsageTranscriptCall> Calls);

/// <summary>A tool invocation requested by the model.</summary>
public sealed record UsageTranscriptCall(string CallId, string Name, string Arguments);

/// <summary>Accounting of a single model HTTP call inside the tool loop.</summary>
public sealed record UsageTranscriptModelCall(
    int Index,
    double Seconds,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    string? FinishReason,
    IReadOnlyList<string> Tools);

/// <summary>
/// Append-only record of everything exchanged with the model: messages, tool
/// calls, tool results and per-call token accounting. The tracking chat client
/// fills it; the log writer turns it into a JSON file plus a static HTML page.
/// </summary>
public sealed class UsageTranscript
{
    public List<UsageTranscriptMessage> Messages { get; } = [];

    public List<UsageTranscriptModelCall> ModelCalls { get; } = [];

    public void AddMessage(ChatMessage message, int maxToolResultChars)
    {
        var calls = message.Contents
            .OfType<FunctionCallContent>()
            .Select(c => new UsageTranscriptCall(c.CallId ?? string.Empty, c.Name, Serialize(c.Arguments)))
            .ToList();

        // A batch of parallel tool calls comes back as one message holding
        // every result, so each result becomes its own transcript entry.
        var results = message.Contents.OfType<FunctionResultContent>().ToList();
        if (results.Count == 0)
        {
            Messages.Add(new UsageTranscriptMessage(message.Role.Value, message.Text, null, null, calls));
            return;
        }

        if (calls.Count > 0)
        {
            Messages.Add(new UsageTranscriptMessage(message.Role.Value, null, null, null, calls));
        }

        foreach (var result in results)
        {
            Messages.Add(new UsageTranscriptMessage(
                message.Role.Value,
                null,
                result.CallId,
                Clamp(Serialize(result.Result), maxToolResultChars),
                []));
        }
    }

    public void AddModelCall(UsageTranscriptModelCall call) => ModelCalls.Add(call);

    private static string Clamp(string value, int max)
    {
        if (max <= 0 || value.Length <= max)
        {
            return value;
        }

        return value[..max] + $"\n... [truncated {value.Length - max} chars]";
    }

    /// <summary>
    /// Tool arguments arrive as loosely typed dictionaries; strings pass
    /// through, everything else is serialized so the log stays readable.
    /// </summary>
    private static string Serialize(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text;
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (NotSupportedException)
        {
            return value.ToString() ?? string.Empty;
        }
    }
}
