namespace IntuneWinPackager.Models.Entities;

public sealed record PackageCatalogDownloadResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public string InstallerPath { get; init; } = string.Empty;

    public string WorkingFolderPath { get; init; } = string.Empty;
}
