using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record PackageProfile
{
    public string Name { get; init; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public InstallerType InstallerType { get; init; } = InstallerType.Unknown;

    public PackageConfiguration Configuration { get; init; } = new();
}
