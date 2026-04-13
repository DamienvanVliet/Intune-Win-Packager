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
}
