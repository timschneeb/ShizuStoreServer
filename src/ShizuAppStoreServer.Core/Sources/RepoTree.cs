namespace ShizuAppStoreServer.Core.Sources;

/// <summary>One blob in a repository tree; <c>Sha</c>/<c>Size</c> are null when the forge does not report them.</summary>
public sealed record RepoTreeEntry(string Path, string? Sha, long? Size);

/// <summary>Recursive repository tree; <c>Truncated</c> is true when the forge cut the listing short.</summary>
public sealed record RepoTree(IReadOnlyList<RepoTreeEntry> Entries, bool Truncated);
