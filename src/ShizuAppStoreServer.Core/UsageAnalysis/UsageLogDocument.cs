namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>
/// Serializable snapshot of one analysis run. Written next to the rendered
/// HTML page so a log can be re-rendered or inspected by tooling without
/// touching the database.
/// </summary>
public sealed record UsageLogDocument(
    int Version,
    long RunId,
    long AppId,
    string Slug,
    string AppName,
    string? PackageName,
    string? VersionName,
    string? RepoUrl,
    string Status,
    int Attempts,
    string? Error,
    string? Forge,
    string? Commit,
    string? Ref,
    string? Model,
    int PromptVersion,
    int AnalysisVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    decimal CostUsd,
    int ToolCalls,
    UsageReport? Report,
    IReadOnlyList<UsageTranscriptMessage> Messages,
    IReadOnlyList<UsageTranscriptModelCall> ModelCalls,
    UsageCoverage? Coverage = null);
