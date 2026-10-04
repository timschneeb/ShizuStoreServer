namespace ShizuAppStoreServer.Core.Data;

/// <summary>Lifecycle of a proposed use case tag.</summary>
public enum UseCaseCandidateStatus
{
    Pending,
    Promoted,
    Merged,
    Dismissed,
}

/// <summary>
/// A use case the classifier proposed because no active tag covered it
/// (table <c>use_case_candidates</c>). A pending candidate is promoted
/// automatically once enough distinct apps propose it, or by an operator;
/// dismissed candidates are never resurrected by later proposals.
/// </summary>
public sealed class UseCaseCandidate
{
    public long Id { get; set; }

    /// <summary>Normalized slug of the proposed name.</summary>
    public required string Slug { get; set; }

    public required string Name { get; set; }

    public UseCaseCandidateStatus Status { get; set; }

    /// <summary>Tag a merged candidate was folded into; null otherwise.</summary>
    public long? MergedIntoUseCaseId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
