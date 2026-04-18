using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record CatalogInstallerVariant
{
    public string VariantKey { get; init; } = string.Empty;

    public PackageCatalogSource Source { get; init; } = PackageCatalogSource.Winget;

    public string SourceDisplayName { get; init; } = string.Empty;

    public string SourceChannel { get; init; } = string.Empty;

    public string PackageId { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string BuildVersion { get; init; } = string.Empty;

    public InstallerType InstallerType { get; init; } = InstallerType.Unknown;

    public string InstallerTypeRaw { get; init; } = string.Empty;

    public string Architecture { get; init; } = string.Empty;

    public string Scope { get; init; } = string.Empty;

    public string InstallerDownloadUrl { get; init; } = string.Empty;

    public string InstallerSha256 { get; init; } = string.Empty;

    public bool HashVerifiedBySource { get; init; }

    public bool VendorSigned { get; init; }

    public string SignerSubject { get; init; } = string.Empty;

    public string SuggestedInstallCommand { get; init; } = string.Empty;

    public string SuggestedUninstallCommand { get; init; } = string.Empty;

    public IntuneDetectionRule DetectionRule { get; init; } = new();

    public string DetectionGuidance { get; init; } = string.Empty;

    public bool IsDeterministicDetection { get; init; }

    public int ConfidenceScore { get; init; }

    public DateTimeOffset? PublishedAtUtc { get; init; }
}
