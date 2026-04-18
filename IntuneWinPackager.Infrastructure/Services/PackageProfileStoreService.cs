using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Infrastructure.Persistence;
using IntuneWinPackager.Infrastructure.Support;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class PackageProfileStoreService : IPackageProfileStoreService
{
    private const int MaxProfiles = 1200;
    private readonly JsonFileStore _store = new();

    public async Task<IReadOnlyList<CatalogPackageProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        DataPathProvider.EnsureBaseDirectory();
        var snapshot = await _store.ReadAsync(DataPathProvider.CatalogProfilesFilePath, new CatalogProfileSnapshot(), cancellationToken);

        return snapshot.Entries
            .OrderByDescending(entry => entry.LastVerifiedAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(entry => entry.LastPreparedAtUtc)
            .ThenBy(entry => entry.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task SaveProfileAsync(CatalogPackageProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile is null ||
            string.IsNullOrWhiteSpace(profile.PackageId) ||
            string.IsNullOrWhiteSpace(profile.Version))
        {
            return;
        }

        DataPathProvider.EnsureBaseDirectory();
        var snapshot = await _store.ReadAsync(DataPathProvider.CatalogProfilesFilePath, new CatalogProfileSnapshot(), cancellationToken);
        var entries = snapshot.Entries.ToList();

        var index = entries.FindIndex(existing => IsSameProfileTarget(existing, profile));

        if (index >= 0)
        {
            var existing = entries[index];
            entries[index] = Merge(existing, profile);
        }
        else
        {
            entries.Add(profile);
        }

        if (entries.Count > MaxProfiles)
        {
            entries = entries
                .OrderByDescending(entry => entry.LastVerifiedAtUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(entry => entry.LastPreparedAtUtc)
                .Take(MaxProfiles)
                .ToList();
        }

        await _store.WriteAsync(
            DataPathProvider.CatalogProfilesFilePath,
            new CatalogProfileSnapshot { Entries = entries },
            cancellationToken);
    }

    public async Task PromoteProfileAsync(
        PackageCatalogSource source,
        string sourceChannel,
        string packageId,
        string version,
        string installerSha256,
        string canonicalPackageKey = "",
        string installerVariantKey = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        DataPathProvider.EnsureBaseDirectory();
        var snapshot = await _store.ReadAsync(DataPathProvider.CatalogProfilesFilePath, new CatalogProfileSnapshot(), cancellationToken);
        var entries = snapshot.Entries.ToList();

        var index = entries.FindIndex(existing =>
            IsProfilePromotionTarget(
                existing,
                source,
                sourceChannel,
                packageId,
                version,
                installerSha256,
                canonicalPackageKey,
                installerVariantKey));

        if (index < 0)
        {
            return;
        }

        var existing = entries[index];
        entries[index] = existing with
        {
            Confidence = CatalogProfileConfidence.Verified,
            LastVerifiedAtUtc = DateTimeOffset.UtcNow,
            DetectionReady = true
        };

        await _store.WriteAsync(
            DataPathProvider.CatalogProfilesFilePath,
            new CatalogProfileSnapshot { Entries = entries },
            cancellationToken);
    }

    private static CatalogPackageProfile Merge(CatalogPackageProfile existing, CatalogPackageProfile incoming)
    {
        var highestConfidence = (CatalogProfileConfidence)Math.Max((int)existing.Confidence, (int)incoming.Confidence);
        var verifiedAt = existing.LastVerifiedAtUtc.HasValue && incoming.LastVerifiedAtUtc.HasValue
            ? (existing.LastVerifiedAtUtc.Value >= incoming.LastVerifiedAtUtc.Value
                ? existing.LastVerifiedAtUtc
                : incoming.LastVerifiedAtUtc)
            : existing.LastVerifiedAtUtc ?? incoming.LastVerifiedAtUtc;

        return existing with
        {
            CanonicalPackageKey = Coalesce(incoming.CanonicalPackageKey, existing.CanonicalPackageKey),
            CanonicalPublisher = Coalesce(incoming.CanonicalPublisher, existing.CanonicalPublisher),
            CanonicalProductName = Coalesce(incoming.CanonicalProductName, existing.CanonicalProductName),
            ReleaseChannel = Coalesce(incoming.ReleaseChannel, existing.ReleaseChannel),
            Name = Coalesce(incoming.Name, existing.Name),
            SourceChannel = Coalesce(incoming.SourceChannel, existing.SourceChannel),
            BuildVersion = Coalesce(incoming.BuildVersion, existing.BuildVersion),
            InstallerPath = Coalesce(incoming.InstallerPath, existing.InstallerPath),
            InstallerSha256 = Coalesce(incoming.InstallerSha256, existing.InstallerSha256),
            InstallerVariantKey = Coalesce(incoming.InstallerVariantKey, existing.InstallerVariantKey),
            InstallerArchitecture = Coalesce(incoming.InstallerArchitecture, existing.InstallerArchitecture),
            InstallerScope = Coalesce(incoming.InstallerScope, existing.InstallerScope),
            InstallerType = incoming.InstallerType == InstallerType.Unknown ? existing.InstallerType : incoming.InstallerType,
            InstallCommand = Coalesce(incoming.InstallCommand, existing.InstallCommand),
            UninstallCommand = Coalesce(incoming.UninstallCommand, existing.UninstallCommand),
            DetectionRuleType = incoming.DetectionRuleType == IntuneDetectionRuleType.None
                ? existing.DetectionRuleType
                : incoming.DetectionRuleType,
            IntuneRules = incoming.DetectionRuleType != IntuneDetectionRuleType.None
                ? incoming.IntuneRules
                : existing.IntuneRules,
            SilentSwitchesVerified = existing.SilentSwitchesVerified || incoming.SilentSwitchesVerified,
            HashVerifiedBySource = existing.HashVerifiedBySource || incoming.HashVerifiedBySource,
            VendorSigned = existing.VendorSigned || incoming.VendorSigned,
            SilentSwitchProbeDetected = existing.SilentSwitchProbeDetected || incoming.SilentSwitchProbeDetected,
            DetectionReady = existing.DetectionReady || incoming.DetectionReady,
            Confidence = highestConfidence,
            IconPath = Coalesce(incoming.IconPath, existing.IconPath),
            LastPreparedAtUtc = incoming.LastPreparedAtUtc > existing.LastPreparedAtUtc
                ? incoming.LastPreparedAtUtc
                : existing.LastPreparedAtUtc,
            LastVerifiedAtUtc = verifiedAt
        };
    }

    private static string Coalesce(string incoming, string fallback)
    {
        return string.IsNullOrWhiteSpace(incoming) ? fallback : incoming;
    }

    private static bool IsSameSourceChannel(string existing, string requested)
    {
        if (string.IsNullOrWhiteSpace(existing) || string.IsNullOrWhiteSpace(requested))
        {
            return true;
        }

        return existing.Equals(requested, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameProfileTarget(CatalogPackageProfile existing, CatalogPackageProfile incoming)
    {
        if (!IsVersionMatch(existing.Version, incoming.Version))
        {
            return false;
        }

        var canonicalMatch =
            !string.IsNullOrWhiteSpace(existing.CanonicalPackageKey) &&
            !string.IsNullOrWhiteSpace(incoming.CanonicalPackageKey) &&
            existing.CanonicalPackageKey.Equals(incoming.CanonicalPackageKey, StringComparison.OrdinalIgnoreCase);

        var sourceMatch =
            existing.Source == incoming.Source &&
            IsSameSourceChannel(existing.SourceChannel, incoming.SourceChannel) &&
            existing.PackageId.Equals(incoming.PackageId, StringComparison.OrdinalIgnoreCase);

        if (!canonicalMatch && !sourceMatch)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(existing.InstallerVariantKey) &&
            !string.IsNullOrWhiteSpace(incoming.InstallerVariantKey))
        {
            return existing.InstallerVariantKey.Equals(incoming.InstallerVariantKey, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(existing.InstallerSha256) &&
            !string.IsNullOrWhiteSpace(incoming.InstallerSha256))
        {
            return existing.InstallerSha256.Equals(incoming.InstallerSha256, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(existing.InstallerArchitecture) &&
            !string.IsNullOrWhiteSpace(incoming.InstallerArchitecture) &&
            !existing.InstallerArchitecture.Equals(incoming.InstallerArchitecture, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(existing.InstallerScope) &&
            !string.IsNullOrWhiteSpace(incoming.InstallerScope) &&
            !existing.InstallerScope.Equals(incoming.InstallerScope, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsProfilePromotionTarget(
        CatalogPackageProfile existing,
        PackageCatalogSource source,
        string sourceChannel,
        string packageId,
        string version,
        string installerSha256,
        string canonicalPackageKey,
        string installerVariantKey)
    {
        if (!IsVersionMatch(existing.Version, version))
        {
            return false;
        }

        var canonicalMatch =
            !string.IsNullOrWhiteSpace(canonicalPackageKey) &&
            !string.IsNullOrWhiteSpace(existing.CanonicalPackageKey) &&
            existing.CanonicalPackageKey.Equals(canonicalPackageKey, StringComparison.OrdinalIgnoreCase);
        var sourceMatch =
            existing.Source == source &&
            IsSameSourceChannel(existing.SourceChannel, sourceChannel) &&
            existing.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase);
        if (!canonicalMatch && !sourceMatch)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(installerVariantKey) &&
            !string.IsNullOrWhiteSpace(existing.InstallerVariantKey))
        {
            return existing.InstallerVariantKey.Equals(installerVariantKey, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(installerSha256) &&
            !string.IsNullOrWhiteSpace(existing.InstallerSha256))
        {
            return existing.InstallerSha256.Equals(installerSha256, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static bool IsVersionMatch(string left, string right)
    {
        return NormalizeVersion(left).Equals(NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        var plusIndex = normalized.IndexOf('+');
        if (plusIndex > 0)
        {
            normalized = normalized[..plusIndex];
        }

        return normalized;
    }

    private sealed record CatalogProfileSnapshot
    {
        public List<CatalogPackageProfile> Entries { get; init; } = [];
    }
}
