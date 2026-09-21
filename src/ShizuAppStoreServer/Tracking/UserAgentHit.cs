namespace ShizuAppStoreServer.Tracking;

/// <summary>One tracked request, buffered until the flush worker drains it.</summary>
public readonly record struct UserAgentHit(string UserAgent, string Path, DateTimeOffset SeenAt);
