namespace IntuneWinPackager.Models.Entities;

public sealed record AppSettings
{
    public string IntuneWinAppUtilPath { get; init; } = string.Empty;

    public string LastSourceFolder { get; init; } = string.Empty;

    public string LastOutputFolder { get; init; } = string.Empty;

    public string LastSetupFilePath { get; init; } = string.Empty;

    public bool UseLowImpactMode { get; init; } = true;

    public bool EnableSilentAppUpdates { get; init; }
}
