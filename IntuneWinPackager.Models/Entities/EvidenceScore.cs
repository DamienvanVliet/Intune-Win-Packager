using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record EvidenceScore
{
    public int Value { get; init; }

    public EvidenceDecisionStatus Status { get; init; } = EvidenceDecisionStatus.NeedsReview;

    public string RejectionReason { get; init; } = string.Empty;

    public string RecommendedAction { get; init; } = string.Empty;

    public IReadOnlyList<string> Factors { get; init; } = [];
}
