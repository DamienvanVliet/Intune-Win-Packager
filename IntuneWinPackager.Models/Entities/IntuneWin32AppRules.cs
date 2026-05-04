using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record IntuneWin32AppRules
{
    public IntuneInstallContext InstallContext { get; init; } = IntuneInstallContext.System;

    public IntuneRestartBehavior RestartBehavior { get; init; } = IntuneRestartBehavior.DetermineBehaviorBasedOnReturnCodes;

    public int MaxRunTimeMinutes { get; init; } = 60;

    public bool RequireSilentSwitchReview { get; init; }

    public bool SilentSwitchesVerified { get; init; }

    public string AppliedTemplateName { get; init; } = string.Empty;

    public string TemplateGuidance { get; init; } = string.Empty;

    public IntuneRequirementRules Requirements { get; init; } = new();

    public IntuneDetectionRule DetectionRule { get; init; } = new();

    public DetectionDeploymentIntent DetectionIntent { get; init; } = DetectionDeploymentIntent.Install;

    public IReadOnlyList<IntuneDetectionRule> AdditionalDetectionRules { get; init; } = [];

    public IReadOnlyList<DetectionFieldProvenance> DetectionProvenance { get; init; } = [];

    public bool StrictDetectionProvenanceMode { get; init; }

    public bool ExeIdentityLockEnabled { get; init; } = true;

    public bool ExeFallbackApproved { get; init; }

    public bool EnforceStrictScriptPolicy { get; init; } = true;

    public string SourceChannelHint { get; init; } = string.Empty;

    public string InstallerArchitectureHint { get; init; } = string.Empty;

    public string InstallerSignerThumbprintHint { get; init; } = string.Empty;
}
