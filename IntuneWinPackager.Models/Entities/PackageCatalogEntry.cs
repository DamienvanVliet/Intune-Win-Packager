using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record PackageCatalogEntry
{
    public string CanonicalPackageKey { get; init; } = string.Empty;

    public string CanonicalPublisher { get; init; } = string.Empty;

    public string CanonicalProductName { get; init; } = string.Empty;

    public string ReleaseChannel { get; init; } = "stable";

    public PackageCatalogSource Source { get; init; } = PackageCatalogSource.Winget;

    public string SourceDisplayName { get; init; } = string.Empty;

    public string SourceChannel { get; init; } = string.Empty;

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

    public CatalogProfileConfidence ProfileConfidence { get; init; } = CatalogProfileConfidence.ManualReview;

    public string ConfidenceBadgeText { get; init; } = string.Empty;

    public CatalogReadinessState ReadinessState { get; init; } = CatalogReadinessState.NeedsReview;

    public string ReadinessBadgeText { get; init; } = string.Empty;

    public string ReadinessEvidenceText { get; init; } = string.Empty;

    public bool IsUpgradeAvailable { get; init; }

    public string UpgradeFromVersion { get; init; } = string.Empty;

    public bool HashVerifiedBySource { get; init; }

    public bool VendorSigned { get; init; }

    public bool SilentSwitchProbeDetected { get; init; }

    public bool DetectionReady { get; init; }

    public string LocalInstallerPath { get; init; } = string.Empty;

    public string InstallerSha256 { get; init; } = string.Empty;

    public string CachedIconPath { get; init; } = string.Empty;

    public DateTimeOffset? LastPreparedAtUtc { get; init; }

    public DateTimeOffset? LastVerifiedAtUtc { get; init; }

    public IReadOnlyList<CatalogInstallerVariant> InstallerVariants { get; init; } = [];

    public bool HasPreparedProfile => LastPreparedAtUtc.HasValue;

    public int InstallerVariantCount => InstallerVariants.Count;

    public int SourceVariantCount => InstallerVariants
        .Select(variant => $"{variant.Source}:{variant.SourceChannel}:{variant.PackageId}")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public string EffectiveIconPath => string.IsNullOrWhiteSpace(CachedIconPath)
        ? IconUrl
        : CachedIconPath;

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
