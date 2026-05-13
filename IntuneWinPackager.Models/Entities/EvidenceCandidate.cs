using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record EvidenceCandidate
{
    public string CandidateId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public EvidenceCandidateKind Kind { get; init; } = EvidenceCandidateKind.Unknown;

    public EvidenceSourceType Source { get; init; } = EvidenceSourceType.Unknown;

    public IntuneDetectionRule DetectionRule { get; init; } = new();

    public IReadOnlyList<IntuneDetectionRule> AdditionalDetectionRules { get; init; } = [];

    public IReadOnlyList<DetectionFieldProvenance> Provenance { get; init; } = [];

    public int BaseScore { get; init; }

    public bool ProofAvailable { get; init; }

    public bool IsProven { get; init; }

    public bool RequiresUserReview { get; init; }

    public string Reason { get; init; } = string.Empty;
}
