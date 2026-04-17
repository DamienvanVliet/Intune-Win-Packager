namespace IntuneWinPackager.Models.Entities;

public sealed record PackageCatalogDownloadResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public string InstallerPath { get; init; } = string.Empty;

    public string WorkingFolderPath { get; init; } = string.Empty;

    public string InstallerSha256 { get; init; } = string.Empty;

    public bool HashVerifiedBySource { get; init; }

    public bool VendorSigned { get; init; }

    public string SignerSubject { get; init; } = string.Empty;
}
