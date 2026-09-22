namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// One request whose User-Agent is not a ShizuStore client (table
/// <c>request_logs</c>). Covers every endpoint including <c>/icons</c>,
/// <c>/healthz</c> and <c>/v1/admin</c>, and stores the request line plus all
/// request headers verbatim (no redaction, no truncation). Written only by the
/// in-process request-log worker; no endpoint reads or writes it.
/// </summary>
public sealed class RequestLog
{
    public long Id { get; set; }
    public DateTimeOffset SeenAt { get; set; }

    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string? QueryString { get; set; }
    public string RawTarget { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Scheme { get; set; } = "";
    public string? Host { get; set; }

    /// <summary>Reconstructed request line + headers; no body (no endpoint takes one).</summary>
    public string RawRequest { get; set; } = "";

    /// <summary>All request headers as a JSON object, exactly as received.</summary>
    public string Headers { get; set; } = "{}";

    public short StatusCode { get; set; }
    public int DurationMs { get; set; }

    public string? UserAgent { get; set; }
    public string? Origin { get; set; }

    /// <summary>Socket peer; always the local tunnel since Kestrel binds loopback.</summary>
    public string? RemoteIp { get; set; }

    /// <summary>CF-Connecting-IP when present, else the first X-Forwarded-For hop.</summary>
    public string? ClientIp { get; set; }

    public string? ForwardedFor { get; set; }
    public string? CfRay { get; set; }
    public string? Country { get; set; }
    public string? TraceId { get; set; }
}
