namespace IntuneWinPackager.Models.Entities;

public sealed record AppSettings
{
    public string IntuneWinAppUtilPath { get; init; } = string.Empty;

    public string LastSourceFolder { get; init; } = string.Empty;

    public string LastOutputFolder { get; init; } = string.Empty;

    public string WorkspaceRoot { get; init; } = string.Empty;

    public string LastSetupFilePath { get; init; } = string.Empty;

    public bool UseLowImpactMode { get; init; } = false;

    public bool EnableSilentAppUpdates { get; init; }

    public string LastKnownLatestVersion { get; init; } = string.Empty;

    public DateTimeOffset? LastUpdateCheckUtc { get; init; }

    public string UiLanguage { get; init; } = "en";

    public string UiTheme { get; init; } = "light";

    public string UiDensity { get; init; } = "comfortable";

    public bool StoreShowAdvancedDetails { get; init; }
}

