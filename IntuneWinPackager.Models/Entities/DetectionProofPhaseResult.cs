namespace IntuneWinPackager.Models.Entities;

public sealed record DetectionProofPhaseResult
{
    public string PhaseName { get; init; } = string.Empty;

    public bool Success { get; init; }

    public string Summary { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;
}

