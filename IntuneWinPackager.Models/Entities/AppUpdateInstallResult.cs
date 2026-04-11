namespace IntuneWinPackager.Models.Entities;

public sealed record AppUpdateInstallResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public string InstallerPath { get; init; } = string.Empty;
}
