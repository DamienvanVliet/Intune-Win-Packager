namespace IntuneWinPackager.Models.Entities;

public sealed record AppUpdateInfo
{
    public bool IsUpdateAvailable { get; init; }

    public bool CheckSucceeded { get; init; } = true;

    public string CurrentVersion { get; init; } = string.Empty;

    public string LatestVersion { get; init; } = string.Empty;

    public string ReleaseTag { get; init; } = string.Empty;

    public string ReleaseName { get; init; } = string.Empty;

    public string ReleaseNotes { get; init; } = string.Empty;

    public string InstallerDownloadUrl { get; init; } = string.Empty;

    public string InstallerSha256 { get; init; } = string.Empty;

    public DateTimeOffset? PublishedAtUtc { get; init; }

    public string Message { get; init; } = string.Empty;
}
