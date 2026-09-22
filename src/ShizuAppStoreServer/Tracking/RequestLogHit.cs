namespace ShizuAppStoreServer.Tracking;

/// <summary>One non-client request, buffered until the flush worker drains it.</summary>
public readonly record struct RequestLogHit(
    DateTimeOffset SeenAt,
    string Method,
    string Path,
    string? QueryString,
    string RawTarget,
    string Protocol,
    string Scheme,
    string? Host,
    string RawRequest,
    string Headers,
    short StatusCode,
    int DurationMs,
    string? UserAgent,
    string? Origin,
    string? RemoteIp,
    string? ClientIp,
    string? ForwardedFor,
    string? CfRay,
    string? Country,
    string? TraceId);
