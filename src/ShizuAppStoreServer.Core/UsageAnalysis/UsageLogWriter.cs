using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>Persists the model conversation of one run as JSON plus an HTML page.</summary>
public interface IUsageAnalysisLogWriter
{
    /// <summary>
    /// Writes <c>{timestamp}-{slug}-r{runId}.json</c> and <c>.html</c> into the
    /// configured log directory. Returns the HTML file name, or null when log
    /// files are disabled or the write failed (never fails the analysis).
    /// </summary>
    Task<string?> WriteAsync(
        UsageAnalysisRun run,
        App app,
        string? versionName,
        DateTimeOffset finishedAt,
        UsageAgentResult? result,
        string? error,
        CancellationToken ct = default);
}

public sealed class UsageAnalysisLogWriter(
    UsageAnalysisOptions options,
    ILogger<UsageAnalysisLogWriter>? log = null) : IUsageAnalysisLogWriter
{
    public static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<string?> WriteAsync(
        UsageAnalysisRun run,
        App app,
        string? versionName,
        DateTimeOffset finishedAt,
        UsageAgentResult? result,
        string? error,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.LogPath))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(options.LogPath);
            var document = Build(run, app, versionName, finishedAt, result, error);
            var baseName = string.Create(CultureInfo.InvariantCulture,
                $"{finishedAt.UtcDateTime:yyyyMMdd-HHmmss}-{SafeName(app.Slug)}-r{run.Id}");
            await File.WriteAllTextAsync(
                Path.Combine(options.LogPath, baseName + ".json"),
                JsonSerializer.Serialize(document, Json), ct);
            await File.WriteAllTextAsync(
                Path.Combine(options.LogPath, baseName + ".html"),
                UsageLogHtml.Render(document), ct);
            return baseName + ".html";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogWarning(ex, "Could not write the usage analysis log for {Slug}.", app.Slug);
            return null;
        }
    }

    /// <summary>
    /// Re-renders every stored transcript in a directory. Used by the
    /// <c>--render-usage-logs</c> one-shot after renderer changes.
    /// </summary>
    public static int RenderDirectory(string path, ILogger? log = null)
    {
        if (!Directory.Exists(path))
        {
            log?.LogWarning("Usage log directory {Path} does not exist.", path);
            return 0;
        }

        var rendered = 0;
        foreach (var jsonPath in Directory.EnumerateFiles(path, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var document = JsonSerializer.Deserialize<UsageLogDocument>(File.ReadAllText(jsonPath), Json);
                if (document is null)
                {
                    continue;
                }

                File.WriteAllText(Path.ChangeExtension(jsonPath, ".html"), UsageLogHtml.Render(document));
                rendered++;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                log?.LogWarning(ex, "Could not render usage log {File}.", jsonPath);
            }
        }

        return rendered;
    }

    private UsageLogDocument Build(
        UsageAnalysisRun run,
        App app,
        string? versionName,
        DateTimeOffset finishedAt,
        UsageAgentResult? result,
        string? error)
    {
        var transcript = result?.Transcript;
        return new UsageLogDocument(
            Version: 1,
            RunId: run.Id,
            AppId: app.Id,
            Slug: app.Slug,
            AppName: app.Name,
            PackageName: app.PackageName,
            VersionName: versionName,
            RepoUrl: app.SourceUrl ?? app.Url,
            Status: run.Status.ToString().ToLowerInvariant(),
            Attempts: run.Attempts,
            Error: error,
            Forge: run.RepoForge,
            Commit: run.RepoCommit,
            Ref: run.RepoRef,
            Model: run.Model,
            PromptVersion: run.PromptVersion,
            AnalysisVersion: options.AnalysisVersion,
            StartedAt: run.StartedAt ?? run.CreatedAt,
            FinishedAt: finishedAt,
            InputTokens: run.InputTokens,
            CachedInputTokens: run.CachedInputTokens,
            OutputTokens: run.OutputTokens,
            CostUsd: run.CostUsd,
            ToolCalls: run.ToolCalls,
            Report: result?.Report,
            Messages: transcript?.Messages ?? [],
            ModelCalls: transcript?.ModelCalls ?? [],
            Coverage: result?.Coverage);
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "app" : cleaned;
    }
}
