using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record DetectionProofRequest
{
    public InstallerType InstallerType { get; init; } = InstallerType.Unknown;

    public IntuneDetectionRule DetectionRule { get; init; } = new();

    public DetectionProofMode Mode { get; init; } = DetectionProofMode.PassiveRuleControl;

    public bool RequirePositiveDetection { get; init; }

    public string InstallCommand { get; init; } = string.Empty;

    public string UninstallCommand { get; init; } = string.Empty;

    public string WorkingDirectory { get; init; } = string.Empty;
}
