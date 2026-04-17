using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record PackageCatalogEntry
{
    public PackageCatalogSource Source { get; init; } = PackageCatalogSource.Winget;

    public string SourceDisplayName { get; init; } = string.Empty;

    public string PackageId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string BuildVersion { get; init; } = string.Empty;

    public string Publisher { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string HomepageUrl { get; init; } = string.Empty;

    public string IconUrl { get; init; } = string.Empty;

    public string InstallerDownloadUrl { get; init; } = string.Empty;

    public InstallerType InstallerType { get; init; } = InstallerType.Unknown;

    public string InstallerTypeRaw { get; init; } = string.Empty;

    public string SuggestedInstallCommand { get; init; } = string.Empty;

    public string SuggestedUninstallCommand { get; init; } = string.Empty;

    public string DetectionGuidance { get; init; } = string.Empty;

    public string MetadataNotes { get; init; } = string.Empty;

    public int ConfidenceScore { get; init; }

    public bool HasDetailedMetadata { get; init; }

    public DateTimeOffset? PublishedAtUtc { get; init; }

    public string Monogram
    {
        get
        {
            var candidate = !string.IsNullOrWhiteSpace(Name)
                ? Name.Trim()
                : PackageId.Trim();

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return "?";
            }

            var first = candidate[0];
            return char.ToUpperInvariant(first).ToString();
        }
    }
}
