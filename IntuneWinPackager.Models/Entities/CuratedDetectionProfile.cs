using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record CuratedDetectionProfile
{
    public string ProfileId { get; init; } = string.Empty;

    public string PackageId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Publisher { get; init; } = string.Empty;

    public InstallerType InstallerType { get; init; } = InstallerType.Unknown;

    public string VersionPattern { get; init; } = string.Empty;

    public IntuneWin32AppRules Rules { get; init; } = new();

    public int ConfidenceScore { get; init; } = 90;

    public string ConfidenceLabel { get; init; } = "verified";

    public bool IsSignedProfile { get; init; } = true;
}
