namespace IntuneWinPackager.Models.Entities;

public sealed record DetectionProofResult
{
    public bool Success { get; init; }

    public DetectionProofMode Mode { get; init; } = DetectionProofMode.PassiveRuleControl;

    public DetectionProofPhaseResult NegativePhase { get; init; } = new();

    public DetectionProofPhaseResult PositivePhase { get; init; } = new();

    public string Summary { get; init; } = string.Empty;
}

