using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record MsiDetectionRule
{
    public string ProductCode { get; init; } = string.Empty;

    public string ProductVersion { get; init; } = string.Empty;

    public IntuneDetectionOperator ProductVersionOperator { get; init; } = IntuneDetectionOperator.Equals;
}
