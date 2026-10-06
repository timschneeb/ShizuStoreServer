namespace ShizuAppStoreServer.Core.Overrides;

/// <summary>
/// Who owns the column when no override exists. Drives what the applier does
/// when an override is removed: enrichment-owned fields force one re-check so
/// the derived value is rebuilt, list-owned fields wait for the next list sync.
/// </summary>
internal enum AppOverrideOwnership
{
    List,
    Enrichment,
    Visibility,
}

/// <summary>
/// One overridable App column: name in snake_case, how to read the current
/// value as text and how to parse and write an override value.
/// </summary>
internal sealed record AppOverrideField(
    string Name,
    AppOverrideOwnership Ownership,
    Func<Data.App, string> Read,
    Func<Data.App, string, string?> Write);
