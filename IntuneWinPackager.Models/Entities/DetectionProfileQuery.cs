using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record DetectionProfileQuery
{
    public string PackageId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Publisher { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public InstallerType InstallerType { get; init; } = InstallerType.Unknown;
}

