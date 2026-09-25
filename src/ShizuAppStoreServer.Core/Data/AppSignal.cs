namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// One piece of Shizuku usage evidence found for an app, keyed by
/// <c>(app, kind, value)</c>. Evidence is deliberately generic: declared
/// permissions, manifest components, DEX markers, shell command strings and
/// manager markers all fit, so the classifier can grow without schema churn.
/// </summary>
public sealed class AppSignal
{
    public long Id { get; set; }

    public long AppId { get; set; }
    public App? App { get; set; }

    /// <summary>Evidence kind: permission, component, marker, command, manager or fallback.</summary>
    public required string Kind { get; set; }

    /// <summary>Matched value, e.g. the permission name or the command string.</summary>
    public required string Value { get; set; }

    /// <summary>Where the evidence came from: <c>apk</c> or <c>source</c>.</summary>
    public required string Source { get; set; }

    /// <summary>Detection strength: <c>strong</c> or <c>weak</c>.</summary>
    public required string Confidence { get; set; }

    public DateTimeOffset DetectedAt { get; set; }
}
