using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record CatalogPackageProfile
{
    public PackageCatalogSource Source { get; init; } = PackageCatalogSource.Winget;

    public string PackageId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string BuildVersion { get; init; } = string.Empty;

    public string InstallerPath { get; init; } = string.Empty;

    public string InstallerSha256 { get; init; } = string.Empty;

    public InstallerType InstallerType { get; init; } = InstallerType.Unknown;

    public string InstallCommand { get; init; } = string.Empty;

    public string UninstallCommand { get; init; } = string.Empty;

    public IntuneDetectionRuleType DetectionRuleType { get; init; } = IntuneDetectionRuleType.None;

    public IntuneWin32AppRules IntuneRules { get; init; } = new();

    public bool SilentSwitchesVerified { get; init; }

    public bool HashVerifiedBySource { get; init; }

    public bool VendorSigned { get; init; }

    public bool SilentSwitchProbeDetected { get; init; }

    public bool DetectionReady { get; init; }

    public CatalogProfileConfidence Confidence { get; init; } = CatalogProfileConfidence.ManualReview;

    public string IconPath { get; init; } = string.Empty;

    public DateTimeOffset LastPreparedAtUtc { get; init; }

    public DateTimeOffset? LastVerifiedAtUtc { get; init; }
}
