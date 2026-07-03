using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record SandboxProofDetectionResult
{
    public SandboxProofMode Mode { get; init; } = SandboxProofMode.Full;

    public bool Completed { get; init; }

    public bool Failed { get; init; }

    public string Message { get; init; } = string.Empty;

    public string ResultPath { get; init; } = string.Empty;

    public string FailureKind { get; init; } = string.Empty;

    public string InstallCommand { get; init; } = string.Empty;

    public string UninstallCommand { get; init; } = string.Empty;

    public string UninstallCommandSource { get; init; } = string.Empty;

    public bool InstallProven { get; init; }

    public bool DetectionProven { get; init; }

    public bool UninstallProven { get; init; }

    public bool LaunchValidationProven { get; init; }

    public int CandidateCount { get; init; }

    public int ProvenCandidateCount { get; init; }

    public SandboxProofDetectionCandidate? BestCandidate { get; init; }

    public IReadOnlyList<SandboxProofDetectionCandidate> Candidates { get; init; } = [];
}
