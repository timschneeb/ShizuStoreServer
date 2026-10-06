namespace ShizuAppStoreServer.Core.Overrides;

/// <summary>
/// Who owns the column when no override exists. Drives what the applier does
/// when an override is removed: enrichment-owned fields force one re-check so
/// the derived value is rebuilt, list-owned fields wait for the next list sync.
/// Source entries steer the source seam instead of writing a column.
/// </summary>
public enum AppOverrideOwnership
{
    List,
    Enrichment,
    Visibility,
    Source,
}

/// <summary>
/// One overridable field as exposed to operator tooling: snake_case name and
/// the group it belongs to. Validate values with
/// <see cref="AppOverrideFields.Validate"/>.
/// </summary>
public sealed record OverrideFieldInfo(string Name, AppOverrideOwnership Ownership);

/// <summary>
/// One overridable App column: name in snake_case, how to read the current
/// value as text and how to parse and write an override value.
/// </summary>
internal sealed record AppOverrideField(
    string Name,
    AppOverrideOwnership Ownership,
    Func<Data.App, string> Read,
    Func<Data.App, string, string?> Write);
