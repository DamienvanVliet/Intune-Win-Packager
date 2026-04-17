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

        var index = entries.FindIndex(existing =>
            existing.Source == profile.Source &&
            IsSameSourceChannel(existing.SourceChannel, profile.SourceChannel) &&
            existing.PackageId.Equals(profile.PackageId, StringComparison.OrdinalIgnoreCase) &&
            existing.Version.Equals(profile.Version, StringComparison.OrdinalIgnoreCase));

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
            existing.Source == source &&
            IsSameSourceChannel(existing.SourceChannel, sourceChannel) &&
            existing.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase) &&
            existing.Version.Equals(version, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(installerSha256) ||
             string.IsNullOrWhiteSpace(existing.InstallerSha256) ||
             existing.InstallerSha256.Equals(installerSha256, StringComparison.OrdinalIgnoreCase)));

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
            Name = Coalesce(incoming.Name, existing.Name),
            SourceChannel = Coalesce(incoming.SourceChannel, existing.SourceChannel),
            BuildVersion = Coalesce(incoming.BuildVersion, existing.BuildVersion),
            InstallerPath = Coalesce(incoming.InstallerPath, existing.InstallerPath),
            InstallerSha256 = Coalesce(incoming.InstallerSha256, existing.InstallerSha256),
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

    private sealed record CatalogProfileSnapshot
    {
        public List<CatalogPackageProfile> Entries { get; init; } = [];
    }
}
