using System.Globalization;
using System.IO;
using System.Text.Json;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Infrastructure.Persistence;
using IntuneWinPackager.Infrastructure.Support;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using Microsoft.Data.Sqlite;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class PackageProfileStoreService : IPackageProfileStoreService
{
    private const int MaxProfiles = 1200;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonFileStore _legacyStore = new();
    private bool _databaseInitialized;
    private bool _legacyProfilesMigrated;

    public async Task<IReadOnlyList<CatalogPackageProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureDatabaseReadyAsync(cancellationToken);
            var rows = await ReadProfileRowsAsync(cancellationToken);
            return rows
                .Select(row => row.Profile)
                .OrderByDescending(entry => entry.LastVerifiedAtUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(entry => entry.LastPreparedAtUtc)
                .ThenBy(entry => entry.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveProfileAsync(CatalogPackageProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile is null ||
            string.IsNullOrWhiteSpace(profile.PackageId) ||
            string.IsNullOrWhiteSpace(profile.Version))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureDatabaseReadyAsync(cancellationToken);
            var rows = await ReadProfileRowsAsync(cancellationToken);
            var index = rows.FindIndex(existing => IsSameProfileTarget(existing.Profile, profile));

            var profileToPersist = profile;
            string? staleKeyToRemove = null;
            if (index >= 0)
            {
                var existing = rows[index];
                profileToPersist = Merge(existing.Profile, profile);
                if (!existing.ProfileKey.Equals(BuildProfileKey(profileToPersist), StringComparison.OrdinalIgnoreCase))
                {
                    staleKeyToRemove = existing.ProfileKey;
                }
            }

            if (!string.IsNullOrWhiteSpace(staleKeyToRemove))
            {
                await DeleteProfileByKeyAsync(staleKeyToRemove, cancellationToken);
            }

            await UpsertProfileAsync(profileToPersist, cancellationToken);
            await TrimProfilesAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
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

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureDatabaseReadyAsync(cancellationToken);
            var rows = await ReadProfileRowsAsync(cancellationToken);
            var target = rows.FirstOrDefault(existing =>
                IsProfilePromotionTarget(
                    existing.Profile,
                    source,
                    sourceChannel,
                    packageId,
                    version,
                    installerSha256,
                    canonicalPackageKey,
                    installerVariantKey));
            if (target is null)
            {
                return;
            }

            var promoted = target.Profile with
            {
                Confidence = CatalogProfileConfidence.Verified,
                LastVerifiedAtUtc = DateTimeOffset.UtcNow,
                DetectionReady = true
            };

            if (!target.ProfileKey.Equals(BuildProfileKey(promoted), StringComparison.OrdinalIgnoreCase))
            {
                await DeleteProfileByKeyAsync(target.ProfileKey, cancellationToken);
            }

            await UpsertProfileAsync(promoted, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureDatabaseReadyAsync(CancellationToken cancellationToken)
    {
        if (_databaseInitialized && _legacyProfilesMigrated)
        {
            return;
        }

        DataPathProvider.EnsureBaseDirectory();
        await using var connection = await OpenConnectionAsync(cancellationToken);

        if (!_databaseInitialized)
        {
            await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken);
            await ExecuteNonQueryAsync(connection,
                """
                CREATE TABLE IF NOT EXISTS catalog_profiles (
                    profile_key TEXT PRIMARY KEY,
                    canonical_package_key TEXT NOT NULL DEFAULT '',
                    source INTEGER NOT NULL,
                    source_channel TEXT NOT NULL DEFAULT '',
                    package_id TEXT NOT NULL,
                    version TEXT NOT NULL,
                    installer_variant_key TEXT NOT NULL DEFAULT '',
                    installer_sha256 TEXT NOT NULL DEFAULT '',
                    confidence INTEGER NOT NULL,
                    prepared_at_utc TEXT NOT NULL,
                    verified_at_utc TEXT,
                    payload_json TEXT NOT NULL
                );
                """,
                cancellationToken);
            await ExecuteNonQueryAsync(connection,
                "CREATE INDEX IF NOT EXISTS idx_catalog_profiles_canonical ON catalog_profiles(canonical_package_key, version);",
                cancellationToken);
            await ExecuteNonQueryAsync(connection,
                "CREATE INDEX IF NOT EXISTS idx_catalog_profiles_source ON catalog_profiles(source, source_channel, package_id, version);",
                cancellationToken);
            await ExecuteNonQueryAsync(connection,
                "CREATE INDEX IF NOT EXISTS idx_catalog_profiles_prepared ON catalog_profiles(prepared_at_utc DESC);",
                cancellationToken);
            _databaseInitialized = true;
        }

        if (_legacyProfilesMigrated)
        {
            return;
        }

        var hasRows = await ScalarIntAsync(connection, "SELECT COUNT(1) FROM catalog_profiles;", cancellationToken) > 0;
        if (!hasRows && File.Exists(DataPathProvider.CatalogProfilesFilePath))
        {
            var legacy = await _legacyStore.ReadAsync(
                DataPathProvider.CatalogProfilesFilePath,
                new CatalogProfileSnapshot(),
                cancellationToken);

            foreach (var entry in legacy.Entries)
            {
                await UpsertProfileAsync(connection, entry, cancellationToken);
            }
        }

        _legacyProfilesMigrated = true;
    }

    private async Task<List<ProfileRow>> ReadProfileRowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = new List<ProfileRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT profile_key, payload_json FROM catalog_profiles;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var json = reader.GetString(1);
            CatalogPackageProfile? profile = null;
            try
            {
                profile = JsonSerializer.Deserialize<CatalogPackageProfile>(json, SerializerOptions);
            }
            catch
            {
                // Ignore invalid rows.
            }

            if (profile is not null)
            {
                rows.Add(new ProfileRow(key, profile));
            }
        }

        return rows;
    }

    private async Task UpsertProfileAsync(CatalogPackageProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await UpsertProfileAsync(connection, profile, cancellationToken);
    }

    private async Task UpsertProfileAsync(SqliteConnection connection, CatalogPackageProfile profile, CancellationToken cancellationToken)
    {
        var key = BuildProfileKey(profile);
        var payload = JsonSerializer.Serialize(profile, SerializerOptions);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO catalog_profiles (
                profile_key,
                canonical_package_key,
                source,
                source_channel,
                package_id,
                version,
                installer_variant_key,
                installer_sha256,
                confidence,
                prepared_at_utc,
                verified_at_utc,
                payload_json
            ) VALUES (
                $profile_key,
                $canonical_package_key,
                $source,
                $source_channel,
                $package_id,
                $version,
                $installer_variant_key,
                $installer_sha256,
                $confidence,
                $prepared_at_utc,
                $verified_at_utc,
                $payload_json
            )
            ON CONFLICT(profile_key) DO UPDATE SET
                canonical_package_key = excluded.canonical_package_key,
                source = excluded.source,
                source_channel = excluded.source_channel,
                package_id = excluded.package_id,
                version = excluded.version,
                installer_variant_key = excluded.installer_variant_key,
                installer_sha256 = excluded.installer_sha256,
                confidence = excluded.confidence,
                prepared_at_utc = excluded.prepared_at_utc,
                verified_at_utc = excluded.verified_at_utc,
                payload_json = excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$profile_key", key);
        command.Parameters.AddWithValue("$canonical_package_key", profile.CanonicalPackageKey ?? string.Empty);
        command.Parameters.AddWithValue("$source", (int)profile.Source);
        command.Parameters.AddWithValue("$source_channel", profile.SourceChannel ?? string.Empty);
        command.Parameters.AddWithValue("$package_id", profile.PackageId ?? string.Empty);
        command.Parameters.AddWithValue("$version", profile.Version ?? string.Empty);
        command.Parameters.AddWithValue("$installer_variant_key", profile.InstallerVariantKey ?? string.Empty);
        command.Parameters.AddWithValue("$installer_sha256", profile.InstallerSha256 ?? string.Empty);
        command.Parameters.AddWithValue("$confidence", (int)profile.Confidence);
        command.Parameters.AddWithValue("$prepared_at_utc", profile.LastPreparedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$verified_at_utc",
            profile.LastVerifiedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$payload_json", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteProfileByKeyAsync(string profileKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileKey))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM catalog_profiles WHERE profile_key = $profile_key;";
        command.Parameters.AddWithValue("$profile_key", profileKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task TrimProfilesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var count = await ScalarIntAsync(connection, "SELECT COUNT(1) FROM catalog_profiles;", cancellationToken);
        if (count <= MaxProfiles)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM catalog_profiles
            WHERE profile_key IN (
                SELECT profile_key FROM catalog_profiles
                ORDER BY COALESCE(verified_at_utc, '') DESC, prepared_at_utc DESC
                LIMIT $delete_count OFFSET $keep_count
            );
            """;
        command.Parameters.AddWithValue("$delete_count", count - MaxProfiles);
        command.Parameters.AddWithValue("$keep_count", MaxProfiles);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static string BuildProfileKey(CatalogPackageProfile profile)
    {
        var version = NormalizeVersion(profile.Version);
        var canonicalKey = NormalizeSegment(profile.CanonicalPackageKey);
        var variantKey = NormalizeSegment(profile.InstallerVariantKey);

        if (!string.IsNullOrWhiteSpace(canonicalKey))
        {
            if (!string.IsNullOrWhiteSpace(variantKey))
            {
                return $"canon|{canonicalKey}|{version}|{variantKey}";
            }

            return string.Join("|",
                "canon",
                canonicalKey,
                version,
                (int)profile.Source,
                NormalizeSegment(profile.SourceChannel),
                NormalizeSegment(profile.PackageId));
        }

        return string.Join("|",
            "source",
            (int)profile.Source,
            NormalizeSegment(profile.SourceChannel),
            NormalizeSegment(profile.PackageId),
            version,
            variantKey);
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

    private static bool IsSameSourceChannel(string existing, string requested)
    {
        if (string.IsNullOrWhiteSpace(existing) || string.IsNullOrWhiteSpace(requested))
        {
            return true;
        }

        return existing.Equals(requested, StringComparison.OrdinalIgnoreCase);
    }

    private static string Coalesce(string incoming, string fallback)
    {
        return string.IsNullOrWhiteSpace(incoming) ? fallback : incoming;
    }

    private static string NormalizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant();
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar is DBNull)
        {
            return 0;
        }

        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={DataPathProvider.CatalogDatabaseFilePath};Cache=Shared");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private sealed record ProfileRow(string ProfileKey, CatalogPackageProfile Profile);

    private sealed record CatalogProfileSnapshot
    {
        public List<CatalogPackageProfile> Entries { get; init; } = [];
    }
}
