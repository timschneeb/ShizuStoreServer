namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// One app's classifier proposal for a candidate tag (table
/// <c>app_use_case_proposals</c>). Rewritten with each successful tagging run,
/// so promotion counts always reflect the current reports.
/// </summary>
public sealed class AppUseCaseProposal
{
    public long AppId { get; set; }
    public App? App { get; set; }

    public long CandidateId { get; set; }
    public UseCaseCandidate? Candidate { get; set; }

    /// <summary>Short classifier rationale, kept for operator review.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
