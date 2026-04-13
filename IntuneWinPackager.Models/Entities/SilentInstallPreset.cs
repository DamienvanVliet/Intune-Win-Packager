namespace IntuneWinPackager.Models.Entities;

public sealed record SilentInstallPreset
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string InstallArguments { get; init; } = string.Empty;

    public string UninstallArguments { get; init; } = string.Empty;

    public bool RequiresVerification { get; init; } = true;

    public string Guidance { get; init; } = string.Empty;
}
