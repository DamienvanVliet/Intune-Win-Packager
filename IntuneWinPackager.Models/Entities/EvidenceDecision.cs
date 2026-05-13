namespace IntuneWinPackager.Models.Entities;

public sealed record EvidenceDecision
{
    public ScoredEvidenceCandidate? BestCandidate { get; init; }

    public IReadOnlyList<ScoredEvidenceCandidate> Candidates { get; init; } = [];

    public bool HasRecommendedCandidate => BestCandidate is not null;

    public string Summary { get; init; } = string.Empty;
}
