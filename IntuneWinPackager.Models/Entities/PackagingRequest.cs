using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record PackagingRequest
{
    public string IntuneWinAppUtilPath { get; init; } = string.Empty;

    public InstallerType InstallerType { get; init; } = InstallerType.Unknown;

    public bool UseLowImpactMode { get; init; } = true;

    public PackageConfiguration Configuration { get; init; } = new();
}
