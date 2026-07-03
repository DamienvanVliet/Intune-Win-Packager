using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record SandboxProofRequest
{
    public SandboxProofMode Mode { get; init; } = SandboxProofMode.Full;

    public InstallerType InstallerType { get; init; } = InstallerType.Unknown;

    public IntuneInstallContext InstallContext { get; init; } = IntuneInstallContext.System;

    public string SourceFolder { get; init; } = string.Empty;

    public string SetupFilePath { get; init; } = string.Empty;

    public string InstallCommand { get; init; } = string.Empty;

    public string UninstallCommand { get; init; } = string.Empty;

    public IntuneDetectionRule DetectionRule { get; init; } = new();

    public string PrecheckSummary { get; init; } = string.Empty;

    public bool PrecheckDetectionRuleAvailable { get; init; }

    public int PrecheckAdditionalDetectionRuleCount { get; init; }

    public int TimeoutMinutes { get; init; } = 20;

    public bool LaunchSandbox { get; init; } = true;
}
