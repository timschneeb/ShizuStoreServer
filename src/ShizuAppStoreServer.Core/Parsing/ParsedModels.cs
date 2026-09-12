namespace ShizuAppStoreServer.Core.Parsing;

/// <summary>
/// Structured representation of one awesome-list markdown file
/// (e.g. README.md or pages/CLOSED_SOURCE.md).
/// Structure comes from the Markdig AST; per-item fields are parsed
/// from the raw item text using the same grammar as scripts/lint.py.
/// </summary>
public sealed class ParsedDocument
{
    public required string ListingName { get; init; }
    public List<ParsedCategory> Categories { get; } = [];
    public List<ParseWarning> Warnings { get; } = [];
}

/// <summary>
/// One (section, ### category, #### subcategory?) combination.
/// Subcategory is null for entries placed directly under a ### heading.
/// </summary>
public sealed class ParsedCategory
{
    public required string Section { get; init; }
    public required string Name { get; init; }
    public string? Subcategory { get; init; }
    public required string Slug { get; init; }
    public List<ParsedEntry> Entries { get; } = [];
}

public sealed class ParsedEntry
{
    public required string Name { get; init; }
    public required string Slug { get; init; }
    public required string Url { get; init; }
    public string Description { get; init; } = string.Empty;

    /// <summary>SPDX-ish license tag, normalized ("Propietary" typo fixed). Null when missing.</summary>
    public string? License { get; init; }

    public string? SourceUrl { get; init; }
    public bool IsRecommended { get; init; }
    public bool HasPaid { get; init; }
    public bool HasIap { get; init; }
    public bool HasAds { get; init; }
    public int? TrialDays { get; init; }
    public bool RequiresRoot { get; init; }

    /// <summary>Nested bullets (e.g. "aShell You" under "aShell").</summary>
    public List<ParsedEntry> Children { get; } = [];
}

/// <summary>
/// Non-fatal parse problem. The sync must never crash on bad input;
/// offenders are collected here and reported via sync_runs.
/// </summary>
public sealed record ParseWarning(string Location, string Message);
