namespace IntuneWinPackager.Models.Entities;

public sealed record ScoredEvidenceCandidate
{
    public EvidenceCandidate Candidate { get; init; } = new();

    public EvidenceScore Score { get; init; } = new();
}
