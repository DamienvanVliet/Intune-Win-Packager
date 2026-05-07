namespace IntuneWinPackager.Models.Entities;

public sealed record SandboxProofDetectionCandidate
{
    public string Type { get; init; } = string.Empty;

    public string Confidence { get; init; } = string.Empty;

    public int Score { get; init; }

    public string Reason { get; init; } = string.Empty;

    public bool ProofAvailable { get; init; }

    public bool IsProven { get; init; }

    public string ProofSummary { get; init; } = string.Empty;

    public string NegativeProofSummary { get; init; } = string.Empty;

    public string PositiveProofSummary { get; init; } = string.Empty;

    public IntuneDetectionRule Rule { get; init; } = new();

    public IReadOnlyList<IntuneDetectionRule> AdditionalRules { get; init; } = [];
}
