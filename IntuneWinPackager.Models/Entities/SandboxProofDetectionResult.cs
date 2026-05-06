namespace IntuneWinPackager.Models.Entities;

public sealed record SandboxProofDetectionResult
{
    public bool Completed { get; init; }

    public bool Failed { get; init; }

    public string Message { get; init; } = string.Empty;

    public string ResultPath { get; init; } = string.Empty;

    public int CandidateCount { get; init; }

    public SandboxProofDetectionCandidate? BestCandidate { get; init; }

    public IReadOnlyList<SandboxProofDetectionCandidate> Candidates { get; init; } = [];
}
