namespace IntuneWinPackager.Models.Entities;

public sealed record SandboxProofDetectionCandidate
{
    public string Type { get; init; } = string.Empty;

    public string Confidence { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public IntuneDetectionRule Rule { get; init; } = new();
}
