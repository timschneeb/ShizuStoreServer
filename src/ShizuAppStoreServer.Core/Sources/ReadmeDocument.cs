namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Fetched README markdown plus the direct raw URL clients can use to
/// refetch the current file. <c>RawUrl</c> is null when the source exposes no
/// stable raw route.
/// </summary>
public sealed record ReadmeDocument(string Markdown, string? RawUrl);
