using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record SandboxProofRequest
{
    public InstallerType InstallerType { get; init; } = InstallerType.Unknown;

    public string SourceFolder { get; init; } = string.Empty;

    public string SetupFilePath { get; init; } = string.Empty;

    public string InstallCommand { get; init; } = string.Empty;

    public string UninstallCommand { get; init; } = string.Empty;

    public IntuneDetectionRule DetectionRule { get; init; } = new();

    public int TimeoutMinutes { get; init; } = 20;

    public bool LaunchSandbox { get; init; } = true;
}
