using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record EvidenceScoringRequest
{
    public InstallerType InstallerType { get; init; } = InstallerType.Unknown;

    public DetectionDeploymentIntent DetectionIntent { get; init; } = DetectionDeploymentIntent.Install;

    public bool PreferProvenWhenProofExists { get; init; } = true;

    public IReadOnlyList<EvidenceCandidate> Candidates { get; init; } = [];
}
