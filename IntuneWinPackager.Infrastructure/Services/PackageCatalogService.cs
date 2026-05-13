using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Core.Utilities;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;
using IntuneWinPackager.Infrastructure.Support;
using Microsoft.Data.Sqlite;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class PackageCatalogService : IPackageCatalogService
{
    private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://[^\s'""`]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GitHubRepoIdRegex = new(@"^(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)$", RegexOptions.Compiled);
    private static readonly Regex ScoopSearchRegex = new(@"^(?<name>[A-Za-z0-9_.+\-]+)\s+(?<version>[0-9A-Za-z.\-+]+)\s+(?<bucket>[A-Za-z0-9_.+\-]+)?", RegexOptions.Compiled);
    private static readonly Regex GuidInTextRegex = new(@"\{[0-9A-Fa-f\-]{36}\}", RegexOptions.Compiled);
    private static readonly Regex GuidCodeRegex = new(@"^\{[0-9A-Fa-f\-]{36}\}$", RegexOptions.Compiled);
    private static readonly Regex Sha256Regex = new(@"^[0-9A-Fa-f]{64}$", RegexOptions.Compiled);
    private static readonly char[] IdentitySeparators = ['.', '-', '_', '/', '\\', ' '];
    private static readonly string[] SupportedInstallerExtensions =
    [
        ".msi",
        ".exe",
        ".appx",
        ".appxbundle",
        ".msix",
        ".msixbundle",
        ".ps1",
        ".cmd",
        ".bat",
        ".vbs",
        ".wsf"
    ];

    private readonly IProcessRunner _processRunner;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
    private readonly ConcurrentDictionary<string, byte> _backgroundRefreshState = new(StringComparer.OrdinalIgnoreCase);
    private bool _databaseInitialized;

    private const int MaxSearchRetries = 3;
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan CacheFreshWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CacheExpirationWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan ProcessCaptureTimeout = TimeSpan.FromSeconds(75);
    private const string WingetManifestRawBaseUrl = "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests";

    public PackageCatalogService(IProcessRunner processRunner, HttpClient? httpClient = null)
    {
        _processRunner = processRunner;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("IntuneWinPackager/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
    }

    public async Task<IReadOnlyList<PackageCatalogEntry>> SearchAsync(PackageCatalogQuery query, CancellationToken cancellationToken = default)
    {
        query ??= new PackageCatalogQuery();
        var term = query.SearchTerm?.Trim() ?? string.Empty;
        if (term.Length < 2 || !HasAnySourceEnabled(query))
        {
            return [];
        }

        var max = Math.Clamp(query.MaxResults, 1, 50);
        var enabledSourceCount =
            (query.IncludeWinget ? 1 : 0) +
            (query.IncludeChocolatey ? 1 : 0) +
            (query.IncludeGitHubReleases ? 1 : 0) +
            (query.IncludeScoop ? 1 : 0) +
            (query.IncludeNuGet ? 1 : 0);
        var perSource = enabledSourceCount > 1 ? Math.Max(6, max) : max;

        var normalizedQuery = query with
        {
            SearchTerm = term,
            MaxResults = max,
            IncludeNuGet = query.IncludeNuGet,
            IncludeScoop = query.IncludeScoop
        };
        var cacheKey = BuildSearchCacheKey(normalizedQuery);
        var cached = await TryReadCachedSearchAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            if (!cached.IsFresh)
            {
                TriggerBackgroundRefresh(cacheKey, normalizedQuery, perSource);
            }

            return cached.Results
                .OrderByDescending(entry => Relevance(entry, term))
                .ThenByDescending(entry => entry.InstallerVariantCount)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();
        }

        var liveResults = await SearchLiveAsync(normalizedQuery, perSource, cancellationToken);
        await WriteCachedSearchAsync(cacheKey, liveResults, cancellationToken);
        return liveResults
            .OrderByDescending(entry => Relevance(entry, term))
            .ThenByDescending(entry => entry.InstallerVariantCount)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    private async Task<IReadOnlyList<PackageCatalogEntry>> SearchLiveAsync(
        PackageCatalogQuery query,
        int perSource,
        CancellationToken cancellationToken)
    {
        var term = query.SearchTerm?.Trim() ?? string.Empty;
        var wingetTask = query.IncludeWinget
            ? SearchProviderSafeAsync("winget", "configured", () => SearchWingetAsync(term, perSource, cancellationToken), cancellationToken)
            : Task.FromResult<IReadOnlyList<PackageCatalogEntry>>([]);
        var chocolateyTask = query.IncludeChocolatey
            ? SearchProviderSafeAsync("chocolatey", "configured", () => SearchChocolateyAsync(term, perSource, cancellationToken), cancellationToken)
            : Task.FromResult<IReadOnlyList<PackageCatalogEntry>>([]);
        var githubTask = query.IncludeGitHubReleases
            ? SearchProviderSafeAsync("github", "public", () => SearchGitHubReleasesAsync(term, perSource, cancellationToken), cancellationToken)
            : Task.FromResult<IReadOnlyList<PackageCatalogEntry>>([]);
        var scoopTask = query.IncludeScoop
            ? SearchProviderSafeAsync("scoop", "configured", () => SearchScoopAsync(term, perSource, cancellationToken), cancellationToken)
            : Task.FromResult<IReadOnlyList<PackageCatalogEntry>>([]);
        var nugetTask = query.IncludeNuGet
            ? SearchProviderSafeAsync("nuget", "configured", () => SearchNuGetAsync(term, perSource, cancellationToken), cancellationToken)
            : Task.FromResult<IReadOnlyList<PackageCatalogEntry>>([]);

        await Task.WhenAll(wingetTask, chocolateyTask, githubTask, scoopTask, nugetTask);
        var sourceEntries = wingetTask.Result
            .Concat(chocolateyTask.Result)
            .Concat(githubTask.Result)
            .Concat(scoopTask.Result)
            .Concat(nugetTask.Result)
            .Select(NormalizeCatalogEntry)
            .ToList();

        if (sourceEntries.Count == 0)
        {
            return [];
        }

        return MergeCanonicalEntries(sourceEntries);
    }

    private async Task<IReadOnlyList<PackageCatalogEntry>> SearchProviderSafeAsync(
        string providerId,
        string sourceChannel,
        Func<Task<IReadOnlyList<PackageCatalogEntry>>> searchFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await searchFactory();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                await RecordProviderFailureAsync(
                    providerId,
                    sourceChannel,
                    Truncate(ex.Message ?? string.Empty, 240),
                    isTimeout: false,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Provider health logging must never fail catalog search.
            }

            return [];
        }
    }

    private void TriggerBackgroundRefresh(string cacheKey, PackageCatalogQuery query, int perSource)
    {
        if (!_backgroundRefreshState.TryAdd(cacheKey, 1))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var refreshed = await SearchLiveAsync(query, perSource, CancellationToken.None);
                await WriteCachedSearchAsync(cacheKey, refreshed, CancellationToken.None);
            }
            catch
            {
                // Non-blocking background refresh.
            }
            finally
            {
                _backgroundRefreshState.TryRemove(cacheKey, out _);
            }
        });
    }

    public async Task<PackageCatalogEntry?> GetDetailsAsync(PackageCatalogEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.PackageId))
        {
            return null;
        }

        try
        {
            var detailed = entry.Source switch
            {
                PackageCatalogSource.Winget => await GetWingetDetailsAsync(entry, cancellationToken),
                PackageCatalogSource.Chocolatey => await GetChocolateyDetailsAsync(entry, cancellationToken),
                PackageCatalogSource.GitHubReleases => await GetGitHubReleaseDetailsAsync(entry, cancellationToken),
                PackageCatalogSource.Scoop => await GetScoopDetailsAsync(entry, cancellationToken),
                PackageCatalogSource.NuGet => await GetNuGetDetailsAsync(entry, cancellationToken),
                _ => entry
            };

            var normalizedDetailed = NormalizeCatalogEntry(detailed);
            if (entry.InstallerVariants.Count == 0)
            {
                return normalizedDetailed;
            }

            var merged = MergeCanonicalEntries([entry, normalizedDetailed]);
            return merged.FirstOrDefault() ?? normalizedDetailed;
        }
        catch
        {
            return NormalizeCatalogEntry(entry);
        }
    }

    public async Task<PackageCatalogDownloadResult> DownloadInstallerAsync(
        PackageCatalogEntry entry,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.PackageId))
        {
            return new PackageCatalogDownloadResult
            {
                Success = false,
                Message = "Package entry is missing.",
            };
        }

        var packageSegment = SanitizePathSegment(entry.PackageId);
        var versionSegment = SanitizePathSegment(string.IsNullOrWhiteSpace(entry.Version) ? "latest" : entry.Version);
        var workingFolder = Path.Combine(DataPathProvider.CatalogDownloadsDirectory, packageSegment, versionSegment);
        Directory.CreateDirectory(workingFolder);

        progress?.Report($"Downloading package '{entry.Name}' from {entry.SourceDisplayName}...");

        try
        {
            var normalizedEntry = NormalizeCatalogEntry(entry);
            var primaryResult = await DownloadFromEntryBySourceAsync(normalizedEntry, workingFolder, progress, cancellationToken);
            if (primaryResult.Success || normalizedEntry.InstallerVariants.Count <= 1)
            {
                return primaryResult;
            }

            var attemptedSourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                BuildSourceEntryKey(normalizedEntry.Source, normalizedEntry.SourceChannel, normalizedEntry.PackageId, normalizedEntry.Version)
            };

            foreach (var variant in normalizedEntry.InstallerVariants
                         .OrderByDescending(candidate => candidate.ConfidenceScore)
                         .ThenBy(candidate => candidate.SourceDisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var sourceKey = BuildSourceEntryKey(variant.Source, variant.SourceChannel, variant.PackageId, variant.Version);
                if (!attemptedSourceKeys.Add(sourceKey))
                {
                    continue;
                }

                var fallbackEntry = BuildEntryFromVariant(normalizedEntry, variant);
                if (fallbackEntry.Source == normalizedEntry.Source &&
                    fallbackEntry.PackageId.Equals(normalizedEntry.PackageId, StringComparison.OrdinalIgnoreCase) &&
                    IsVersionEquivalent(fallbackEntry.Version, normalizedEntry.Version))
                {
                    continue;
                }

                progress?.Report($"Primary source failed; trying fallback source '{fallbackEntry.SourceDisplayName}'.");
                var fallbackResult = await DownloadFromEntryBySourceAsync(fallbackEntry, workingFolder, progress, cancellationToken);
                if (fallbackResult.Success)
                {
                    return fallbackResult with
                    {
                        Message = $"{fallbackResult.Message} (fallback source: {fallbackEntry.SourceDisplayName})"
                    };
                }
            }

            return primaryResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PackageCatalogDownloadResult
            {
                Success = false,
                Message = $"Package download failed: {ex.Message}",
                WorkingFolderPath = workingFolder
            };
        }
    }

    private async Task<PackageCatalogDownloadResult> DownloadFromEntryBySourceAsync(
        PackageCatalogEntry entry,
        string workingFolder,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        return entry.Source switch
        {
            PackageCatalogSource.Winget => await DownloadWingetInstallerAsync(entry, workingFolder, progress, cancellationToken),
            PackageCatalogSource.Chocolatey => await DownloadChocolateyInstallerAsync(entry, workingFolder, progress, cancellationToken),
            PackageCatalogSource.GitHubReleases => await DownloadGitHubReleaseInstallerAsync(entry, workingFolder, progress, cancellationToken),
            PackageCatalogSource.Scoop => await DownloadScoopInstallerAsync(entry, workingFolder, progress, cancellationToken),
            PackageCatalogSource.NuGet => await DownloadNuGetInstallerAsync(entry, workingFolder, progress, cancellationToken),
            _ => new PackageCatalogDownloadResult
            {
                Success = false,
                Message = $"Source {entry.SourceDisplayName} is not supported for download.",
                WorkingFolderPath = workingFolder
            }
        };
    }

    private static string BuildSourceEntryKey(PackageCatalogSource source, string sourceChannel, string packageId, string version)
    {
        return $"{source}:{sourceChannel}:{packageId}:{NormalizeVersionSegment(version)}";
    }

    private static PackageCatalogEntry BuildEntryFromVariant(PackageCatalogEntry entry, CatalogInstallerVariant variant)
    {
        return entry with
        {
            Source = variant.Source,
            SourceDisplayName = Coalesce(variant.SourceDisplayName, entry.SourceDisplayName),
            SourceChannel = Coalesce(variant.SourceChannel, entry.SourceChannel),
            PackageId = Coalesce(variant.PackageId, entry.PackageId),
            Version = Coalesce(variant.Version, entry.Version),
            BuildVersion = Coalesce(variant.BuildVersion, entry.BuildVersion),
            InstallerType = variant.InstallerType,
            InstallerTypeRaw = Coalesce(variant.InstallerTypeRaw, entry.InstallerTypeRaw),
            InstallerDownloadUrl = Coalesce(variant.InstallerDownloadUrl, entry.InstallerDownloadUrl),
            InstallerSha256 = Coalesce(variant.InstallerSha256, entry.InstallerSha256),
            HashVerifiedBySource = variant.HashVerifiedBySource || entry.HashVerifiedBySource,
            SuggestedInstallCommand = Coalesce(variant.SuggestedInstallCommand, entry.SuggestedInstallCommand),
            SuggestedUninstallCommand = Coalesce(variant.SuggestedUninstallCommand, entry.SuggestedUninstallCommand),
            DetectionGuidance = Coalesce(variant.DetectionGuidance, entry.DetectionGuidance),
            ConfidenceScore = Math.Max(entry.ConfidenceScore, variant.ConfidenceScore)
        };
    }

    public async Task<string> ResolveCachedIconPathAsync(
        PackageCatalogEntry entry,
        string? installerPath = null,
        CancellationToken cancellationToken = default)
    {
        if (entry is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(entry.CachedIconPath) && File.Exists(entry.CachedIconPath))
        {
            return entry.CachedIconPath;
        }

        var iconUrl = entry.IconUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(iconUrl) || !Uri.TryCreate(iconUrl, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        DataPathProvider.EnsureBaseDirectory();
        Directory.CreateDirectory(DataPathProvider.CatalogIconsDirectory);

        var fileExtension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileExtension) || fileExtension.Length > 5)
        {
            fileExtension = ".png";
        }

        var cacheName = $"{SanitizePathSegment(entry.PackageId)}-{ComputeStableHash(iconUrl)}{fileExtension}";
        var cachePath = Path.Combine(DataPathProvider.CatalogIconsDirectory, cacheName);
        if (File.Exists(cachePath))
        {
            return cachePath;
        }

        try
        {
            using var response = await _httpClient.GetAsync(iconUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);

            var info = new FileInfo(cachePath);
            if (!info.Exists || info.Length < 32)
            {
                TryDelete(cachePath);
                return string.Empty;
            }

            return cachePath;
        }
        catch
        {
            TryDelete(cachePath);
            return string.Empty;
        }
    }

    public async Task<IReadOnlyList<CatalogProviderDiagnostic>> GetProviderDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCatalogDatabaseReadyAsync(cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenCatalogDatabaseConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT provider_id,
                       source_channel,
                       total_requests,
                       total_failures,
                       consecutive_failures,
                       timeout_count,
                       last_duration_ms,
                       last_error,
                       last_success_utc,
                       last_failure_utc
                FROM catalog_provider_health
                ORDER BY provider_id COLLATE NOCASE, source_channel COLLATE NOCASE;
                """;

            var diagnostics = new List<CatalogProviderDiagnostic>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                diagnostics.Add(new CatalogProviderDiagnostic
                {
                    ProviderId = reader.GetString(0),
                    SourceChannel = reader.GetString(1),
                    TotalRequests = reader.GetInt32(2),
                    TotalFailures = reader.GetInt32(3),
                    ConsecutiveFailures = reader.GetInt32(4),
                    TimeoutCount = reader.GetInt32(5),
                    LastDurationMs = reader.GetInt64(6),
                    LastError = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    LastSuccessAtUtc = ParseUtc(reader.IsDBNull(8) ? string.Empty : reader.GetString(8)),
                    LastFailureAtUtc = ParseUtc(reader.IsDBNull(9) ? string.Empty : reader.GetString(9)),
                    IsHealthy = reader.GetInt32(4) == 0
                });
            }

            return diagnostics;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private static bool HasAnySourceEnabled(PackageCatalogQuery query)
    {
        return query.IncludeWinget ||
               query.IncludeChocolatey ||
               query.IncludeGitHubReleases ||
               query.IncludeScoop ||
               query.IncludeNuGet;
    }

    private static string BuildSearchCacheKey(PackageCatalogQuery query)
    {
        var normalizedTerm = (query.SearchTerm ?? string.Empty).Trim().ToLowerInvariant();
        var keyPayload = string.Join("|",
            "v3",
            normalizedTerm,
            query.MaxResults,
            query.IncludeWinget ? "w1" : "w0",
            query.IncludeChocolatey ? "c1" : "c0",
            query.IncludeGitHubReleases ? "g1" : "g0",
            query.IncludeScoop ? "s1" : "s0",
            query.IncludeNuGet ? "n1" : "n0");
        return ComputeStableHash(keyPayload);
    }

    private async Task<CachedSearchResult?> TryReadCachedSearchAsync(string cacheKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return null;
        }

        await EnsureCatalogDatabaseReadyAsync(cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenCatalogDatabaseConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT payload_json,
                       updated_utc
                FROM catalog_search_cache
                WHERE cache_key = @cache_key
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@cache_key", cacheKey);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var payloadJson = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var updatedRaw = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var updatedUtc = ParseUtc(updatedRaw) ?? DateTimeOffset.MinValue;
            var age = DateTimeOffset.UtcNow - updatedUtc;

            if (string.IsNullOrWhiteSpace(payloadJson) || age > CacheExpirationWindow)
            {
                await reader.DisposeAsync();
                await using var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = "DELETE FROM catalog_search_cache WHERE cache_key = @cache_key;";
                deleteCommand.Parameters.AddWithValue("@cache_key", cacheKey);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
                return null;
            }

            List<PackageCatalogEntry>? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<List<PackageCatalogEntry>>(payloadJson, _serializerOptions);
            }
            catch
            {
                await reader.DisposeAsync();
                await using var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = "DELETE FROM catalog_search_cache WHERE cache_key = @cache_key;";
                deleteCommand.Parameters.AddWithValue("@cache_key", cacheKey);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
                return null;
            }

            var results = (parsed ?? [])
                .Select(NormalizeCatalogEntry)
                .ToList();
            return new CachedSearchResult(results, age <= CacheFreshWindow);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task WriteCachedSearchAsync(
        string cacheKey,
        IReadOnlyList<PackageCatalogEntry> results,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }

        await EnsureCatalogDatabaseReadyAsync(cancellationToken);
        var normalizedResults = (results ?? [])
            .Select(NormalizeCatalogEntry)
            .ToList();
        var payloadJson = JsonSerializer.Serialize(normalizedResults, _serializerOptions);
        var nowIso = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var expirationIso = DateTimeOffset.UtcNow
            .Subtract(CacheExpirationWindow)
            .ToString("O", CultureInfo.InvariantCulture);

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenCatalogDatabaseConnectionAsync(cancellationToken);

            await using (var upsertCommand = connection.CreateCommand())
            {
                upsertCommand.CommandText =
                    """
                    INSERT INTO catalog_search_cache (
                        cache_key,
                        payload_json,
                        created_utc,
                        updated_utc
                    )
                    VALUES (
                        @cache_key,
                        @payload_json,
                        @now_utc,
                        @now_utc
                    )
                    ON CONFLICT(cache_key) DO UPDATE SET
                        payload_json = excluded.payload_json,
                        updated_utc = excluded.updated_utc;
                    """;
                upsertCommand.Parameters.AddWithValue("@cache_key", cacheKey);
                upsertCommand.Parameters.AddWithValue("@payload_json", payloadJson);
                upsertCommand.Parameters.AddWithValue("@now_utc", nowIso);
                await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var expireCommand = connection.CreateCommand())
            {
                expireCommand.CommandText =
                    """
                    DELETE FROM catalog_search_cache
                    WHERE updated_utc < @expiration_utc;
                    """;
                expireCommand.Parameters.AddWithValue("@expiration_utc", expirationIso);
                await expireCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var trimCommand = connection.CreateCommand())
            {
                trimCommand.CommandText =
                    """
                    DELETE FROM catalog_search_cache
                    WHERE cache_key IN (
                        SELECT cache_key
                        FROM catalog_search_cache
                        ORDER BY updated_utc DESC
                        LIMIT -1 OFFSET 320
                    );
                    """;
                await trimCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task EnsureCatalogDatabaseReadyAsync(CancellationToken cancellationToken)
    {
        if (_databaseInitialized)
        {
            return;
        }

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            if (_databaseInitialized)
            {
                return;
            }

            DataPathProvider.EnsureBaseDirectory();
            await using var connection = await OpenCatalogDatabaseConnectionAsync(cancellationToken);
            await using (var pragmaCommand = connection.CreateCommand())
            {
                pragmaCommand.CommandText = "PRAGMA journal_mode=WAL;";
                await pragmaCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var pragmaCommand = connection.CreateCommand())
            {
                pragmaCommand.CommandText = "PRAGMA synchronous=NORMAL;";
                await pragmaCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS catalog_search_cache (
                        cache_key TEXT PRIMARY KEY,
                        payload_json TEXT NOT NULL,
                        created_utc TEXT NOT NULL,
                        updated_utc TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE INDEX IF NOT EXISTS idx_catalog_search_cache_updated ON catalog_search_cache(updated_utc DESC);";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS catalog_provider_health (
                        provider_id TEXT NOT NULL,
                        source_channel TEXT NOT NULL,
                        total_requests INTEGER NOT NULL DEFAULT 0,
                        total_failures INTEGER NOT NULL DEFAULT 0,
                        consecutive_failures INTEGER NOT NULL DEFAULT 0,
                        timeout_count INTEGER NOT NULL DEFAULT 0,
                        last_duration_ms INTEGER NOT NULL DEFAULT 0,
                        last_error TEXT,
                        last_success_utc TEXT,
                        last_failure_utc TEXT,
                        PRIMARY KEY(provider_id, source_channel)
                    );
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE INDEX IF NOT EXISTS idx_catalog_provider_health_failures ON catalog_provider_health(total_failures DESC, consecutive_failures DESC);";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            _databaseInitialized = true;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private static async Task<SqliteConnection> OpenCatalogDatabaseConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={DataPathProvider.CatalogDatabaseFilePath};Cache=Shared");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static DateTimeOffset? ParseUtc(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private async Task<HttpResponseMessage> SendHttpWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        string providerId,
        string sourceChannel,
        CancellationToken cancellationToken)
    {
        _ = providerId;
        _ = sourceChannel;

        if (requestFactory is null)
        {
            throw new ArgumentNullException(nameof(requestFactory));
        }

        Exception? lastException = null;
        HttpResponseMessage? fallbackResponse = null;

        for (var attempt = 1; attempt <= MaxSearchRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = requestFactory();
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (attempt < MaxSearchRetries && IsTransientStatusCode(response.StatusCode))
                {
                    response.Dispose();
                    await Task.Delay(ComputeRetryDelay(attempt), cancellationToken);
                    continue;
                }

                return response;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
                if (attempt < MaxSearchRetries)
                {
                    await Task.Delay(ComputeRetryDelay(attempt), cancellationToken);
                    continue;
                }

                fallbackResponse?.Dispose();
                fallbackResponse = new HttpResponseMessage(System.Net.HttpStatusCode.RequestTimeout)
                {
                    ReasonPhrase = "HTTP request timed out."
                };
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                if (attempt < MaxSearchRetries)
                {
                    await Task.Delay(ComputeRetryDelay(attempt), cancellationToken);
                    continue;
                }

                fallbackResponse?.Dispose();
                fallbackResponse = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    ReasonPhrase = Truncate(ex.Message, 120)
                };
            }
        }

        return fallbackResponse ?? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
        {
            ReasonPhrase = Truncate(lastException?.Message ?? "Request failed.", 120)
        };
    }

    private static bool IsTransientStatusCode(System.Net.HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 500 ||
               statusCode == System.Net.HttpStatusCode.RequestTimeout ||
               (int)statusCode == 429;
    }

    private static TimeSpan ComputeRetryDelay(int attempt)
    {
        var factor = Math.Max(1, attempt);
        var delayMs = RetryBaseDelay.TotalMilliseconds * factor;
        return TimeSpan.FromMilliseconds(Math.Min(1600, delayMs));
    }

    private async Task RecordProviderSuccessAsync(
        string providerId,
        string sourceChannel,
        long durationMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        var gateHeld = false;
        try
        {
            await EnsureCatalogDatabaseReadyAsync(cancellationToken);
            await _databaseGate.WaitAsync(cancellationToken);
            gateHeld = true;
            await using var connection = await OpenCatalogDatabaseConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO catalog_provider_health (
                    provider_id,
                    source_channel,
                    total_requests,
                    total_failures,
                    consecutive_failures,
                    timeout_count,
                    last_duration_ms,
                    last_error,
                    last_success_utc,
                    last_failure_utc
                )
                VALUES (
                    @provider_id,
                    @source_channel,
                    1,
                    0,
                    0,
                    0,
                    @last_duration_ms,
                    '',
                    @last_success_utc,
                    NULL
                )
                ON CONFLICT(provider_id, source_channel) DO UPDATE SET
                    total_requests = catalog_provider_health.total_requests + 1,
                    consecutive_failures = 0,
                    last_duration_ms = @last_duration_ms,
                    last_error = '',
                    last_success_utc = @last_success_utc;
                """;
            command.Parameters.AddWithValue("@provider_id", providerId.Trim().ToLowerInvariant());
            command.Parameters.AddWithValue("@source_channel", string.IsNullOrWhiteSpace(sourceChannel) ? "default" : sourceChannel.Trim().ToLowerInvariant());
            command.Parameters.AddWithValue("@last_duration_ms", Math.Max(0, durationMs));
            command.Parameters.AddWithValue("@last_success_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Provider diagnostics are best-effort only.
        }
        finally
        {
            if (gateHeld)
            {
                _databaseGate.Release();
            }
        }
    }

    private async Task RecordProviderFailureAsync(
        string providerId,
        string sourceChannel,
        string errorMessage,
        bool isTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        var gateHeld = false;
        try
        {
            await EnsureCatalogDatabaseReadyAsync(cancellationToken);
            await _databaseGate.WaitAsync(cancellationToken);
            gateHeld = true;
            await using var connection = await OpenCatalogDatabaseConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO catalog_provider_health (
                    provider_id,
                    source_channel,
                    total_requests,
                    total_failures,
                    consecutive_failures,
                    timeout_count,
                    last_duration_ms,
                    last_error,
                    last_success_utc,
                    last_failure_utc
                )
                VALUES (
                    @provider_id,
                    @source_channel,
                    1,
                    1,
                    1,
                    @timeout_increment,
                    0,
                    @last_error,
                    NULL,
                    @last_failure_utc
                )
                ON CONFLICT(provider_id, source_channel) DO UPDATE SET
                    total_requests = catalog_provider_health.total_requests + 1,
                    total_failures = catalog_provider_health.total_failures + 1,
                    consecutive_failures = catalog_provider_health.consecutive_failures + 1,
                    timeout_count = catalog_provider_health.timeout_count + @timeout_increment,
                    last_error = @last_error,
                    last_failure_utc = @last_failure_utc;
                """;
            command.Parameters.AddWithValue("@provider_id", providerId.Trim().ToLowerInvariant());
            command.Parameters.AddWithValue("@source_channel", string.IsNullOrWhiteSpace(sourceChannel) ? "default" : sourceChannel.Trim().ToLowerInvariant());
            command.Parameters.AddWithValue("@timeout_increment", isTimeout ? 1 : 0);
            command.Parameters.AddWithValue("@last_error", Truncate(errorMessage ?? string.Empty, 420));
            command.Parameters.AddWithValue("@last_failure_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Provider diagnostics are best-effort only.
        }
        finally
        {
            if (gateHeld)
            {
                _databaseGate.Release();
            }
        }
    }

    private async Task<IReadOnlyList<PackageCatalogEntry>> SearchWingetAsync(string term, int limit, CancellationToken cancellationToken)
    {
        var sources = await GetWingetSearchSourcesAsync(cancellationToken);
        if (sources.Count == 0)
        {
            return [];
        }

        var perSourceLimit = Math.Clamp(limit + 4, 1, 1000);
        var searchTasks = sources
            .Select(source => SearchWingetSourceAsync(term, source.Name, perSourceLimit, cancellationToken))
            .ToArray();
        await Task.WhenAll(searchTasks);

        var entries = searchTasks
            .SelectMany(task => task.Result)
            .ToList();
        if (entries.Count == 0)
        {
            return [];
        }

        var ordered = entries
            .OrderByDescending(entry => Relevance(entry, term))
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit * 2, limit, 90))
            .ToList();

        var enrichCount = Math.Min(ordered.Count, 6);
        for (var i = 0; i < enrichCount; i++)
        {
            ordered[i] = await GetWingetDetailsAsync(ordered[i], cancellationToken);
        }

        return ordered;
    }

    private async Task<IReadOnlyList<PackageCatalogEntry>> SearchWingetSourceAsync(
        string term,
        string sourceName,
        int limit,
        CancellationToken cancellationToken)
    {
        var (exitCode, lines) = await RunProcessCaptureAsync(
            "winget",
            $"search --query {QuoteArgument(term)} --count {Math.Clamp(limit, 1, 1000)} --source {QuoteArgument(sourceName)} --accept-source-agreements --disable-interactivity",
            cancellationToken);
        if (exitCode != 0 || lines.Count == 0)
        {
            return [];
        }

        var rows = ParseWingetRows(lines);
        if (rows.Count == 0)
        {
            return [];
        }

        var displayName = sourceName.Equals("winget", StringComparison.OrdinalIgnoreCase)
            ? "WinGet"
            : $"WinGet ({sourceName})";

        return rows.Select(row => new PackageCatalogEntry
        {
            Source = PackageCatalogSource.Winget,
            SourceDisplayName = displayName,
            SourceChannel = sourceName,
            PackageId = row.Id,
            Name = row.Name,
            Version = row.Version,
            BuildVersion = row.Version,
            Description = row.Match,
            IconUrl = ResolveIconUrl(null, null, row.Id),
            MetadataNotes = $"Basic result from WinGet source '{sourceName}'.",
            ConfidenceScore = 25
        }).ToList();
    }

    private async Task<PackageCatalogEntry> GetWingetDetailsAsync(PackageCatalogEntry entry, CancellationToken cancellationToken)
    {
        var sourceName = string.IsNullOrWhiteSpace(entry.SourceChannel)
            ? "winget"
            : entry.SourceChannel;
        var (exitCode, lines) = await RunProcessCaptureAsync(
            "winget",
            $"show --id {QuoteArgument(entry.PackageId)} --exact --source {QuoteArgument(sourceName)} --accept-source-agreements --disable-interactivity --locale en-US",
            cancellationToken);
        if (exitCode != 0 || lines.Count == 0)
        {
            return entry;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var detailName = string.Empty;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("Found ", StringComparison.OrdinalIgnoreCase))
            {
                var start = line.LastIndexOf('[');
                if (start > 6)
                {
                    detailName = line[6..start].Trim();
                }

                continue;
            }

            var normalized = raw.TrimStart();
            var idx = normalized.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            var key = normalized[..idx].Trim();
            if (!map.ContainsKey(key))
            {
                map[key] = normalized[(idx + 1)..].Trim();
            }
        }

        var packageId = Coalesce(map.GetValueOrDefault("Package Identifier"), entry.PackageId);
        var version = Coalesce(map.GetValueOrDefault("Version"), entry.Version, entry.BuildVersion);
        var manifest = await TryGetWingetManifestInfoAsync(packageId, version, cancellationToken);
        var preferredManifestInstaller = manifest?.Installers
            .OrderByDescending(WingetManifestInstallerPreferenceScore)
            .FirstOrDefault();
        var packageName = Coalesce(detailName, map.GetValueOrDefault("Package Name"), manifest?.PackageName, entry.Name);
        var publisher = Coalesce(map.GetValueOrDefault("Publisher"), manifest?.Publisher, entry.Publisher);
        version = Coalesce(manifest?.PackageVersion, version);
        var productName = Coalesce(
            map.GetValueOrDefault("ProductName"),
            map.GetValueOrDefault("Product Name"),
            manifest?.PackageName,
            packageName);
        var companyName = Coalesce(
            map.GetValueOrDefault("CompanyName"),
            map.GetValueOrDefault("Company Name"),
            manifest?.Publisher,
            publisher);
        var productVersion = Coalesce(
            map.GetValueOrDefault("ProductVersion"),
            map.GetValueOrDefault("Product Version"),
            map.GetValueOrDefault("DisplayVersion"),
            map.GetValueOrDefault("Display Version"),
            manifest?.PackageVersion,
            version);
        var signer = Coalesce(
            map.GetValueOrDefault("Signer"),
            map.GetValueOrDefault("Signer Subject"),
            map.GetValueOrDefault("Signature"));
        var installerRaw = Coalesce(preferredManifestInstaller?.InstallerTypeRaw, map.GetValueOrDefault("Installer Type"), entry.InstallerTypeRaw);
        var installerType = InferInstallerType(installerRaw, map.GetValueOrDefault("Description"));
        if (preferredManifestInstaller is not null)
        {
            installerType = InferInstallerType(installerRaw, preferredManifestInstaller.InstallerUrl);
        }

        var architecture = Coalesce(preferredManifestInstaller?.Architecture, map.GetValueOrDefault("Architecture"), InferArchitecture(packageName, packageId, version, installerRaw));
        var scope = Coalesce(preferredManifestInstaller?.Scope, map.GetValueOrDefault("Scope"), InferScope(sourceName, packageName, packageId));
        var installerSha256 = NormalizeSha256(Coalesce(preferredManifestInstaller?.InstallerSha256, map.GetValueOrDefault("Installer SHA256")));
        var rawProductCode = Coalesce(preferredManifestInstaller?.ProductCode, map.GetValueOrDefault("ProductCode"), map.GetValueOrDefault("Product Code"));
        var msiProductCode = NormalizeMsiProductCode(rawProductCode);
        var appxIdentity = Coalesce(map.GetValueOrDefault("Package Family Name"), map.GetValueOrDefault("Package Name"));
        var uninstallRegistryKeyPath = Coalesce(
            map.GetValueOrDefault("UninstallRegistryKey"),
            map.GetValueOrDefault("Uninstall Registry Key"),
            map.GetValueOrDefault("RegistryKey"),
            map.GetValueOrDefault("Registry Key"),
            installerType == InstallerType.Exe ? BuildUninstallRegistryPathFromProductCode(rawProductCode) : string.Empty);
        var uninstallDisplayName = Coalesce(
            map.GetValueOrDefault("DisplayName"),
            map.GetValueOrDefault("Display Name"),
            map.GetValueOrDefault("AppsAndFeaturesEntries.DisplayName"),
            preferredManifestInstaller?.DisplayName,
            productName);
        var uninstallPublisher = Coalesce(
            map.GetValueOrDefault("AppsAndFeaturesEntries.Publisher"),
            map.GetValueOrDefault("Publisher"),
            preferredManifestInstaller?.Publisher,
            companyName);
        var uninstallDisplayVersion = Coalesce(
            map.GetValueOrDefault("DisplayVersion"),
            map.GetValueOrDefault("Display Version"),
            map.GetValueOrDefault("AppsAndFeaturesEntries.DisplayVersion"),
            preferredManifestInstaller?.DisplayVersion,
            productVersion);
        var installLocation = Coalesce(
            preferredManifestInstaller?.DefaultInstallLocation,
            map.GetValueOrDefault("InstallLocation"),
            map.GetValueOrDefault("Install Location"));
        var displayIcon = Coalesce(
            map.GetValueOrDefault("DisplayIcon"),
            map.GetValueOrDefault("Display Icon"));
        var fileDetectionPath = installLocation;
        var fileDetectionName = string.Empty;
        if (TryParseFileDetectionFromDisplayIcon(displayIcon, out var parsedIconPath, out var parsedIconName))
        {
            fileDetectionPath = parsedIconPath;
            fileDetectionName = parsedIconName;
        }
        var template = BuildTemplate(packageId, installerType, installerRaw);
        var installCommand = Coalesce(
            BuildManifestInstallCommand(installerType, preferredManifestInstaller),
            template.InstallCommand);
        var uninstallCommand = ResolveUninstallTemplate(template.UninstallCommand, installerType, msiProductCode, appxIdentity);
        var detection = BuildDeterministicDetectionRule(
            installerType,
            packageId,
            packageName,
            publisher,
            version,
            msiProductCode,
            appxIdentity,
            uninstallRegistryKeyPath: uninstallRegistryKeyPath,
            uninstallDisplayName: uninstallDisplayName,
            uninstallPublisher: uninstallPublisher,
            uninstallDisplayVersion: uninstallDisplayVersion,
            fileDetectionPath: fileDetectionPath,
            fileDetectionName: fileDetectionName,
            fileDetectionVersion: uninstallDisplayVersion,
            appxPublisher: publisher);
        var homepage = Coalesce(map.GetValueOrDefault("Homepage"), map.GetValueOrDefault("Publisher Url"), manifest?.HomepageUrl, entry.HomepageUrl);
        var installerUrl = Coalesce(preferredManifestInstaller?.InstallerUrl, map.GetValueOrDefault("Installer Url"), entry.InstallerDownloadUrl);
        IReadOnlyList<CatalogInstallerVariant> variants = manifest is null
            ? []
            : BuildWingetManifestInstallerVariants(
                manifest,
                entry.Source,
                entry.SourceDisplayName,
                sourceName,
                packageId,
                packageName,
                publisher,
                version,
                appxIdentity);
        if (variants.Count == 0)
        {
            variants =
            [
                BuildInstallerVariant(
                    entry.Source,
                    entry.SourceDisplayName,
                    sourceName,
                    packageId,
                    version,
                    Coalesce(version, entry.BuildVersion),
                    installerType,
                    installerRaw,
                    architecture,
                    scope,
                    installerUrl,
                    installerSha256,
                    hashVerifiedBySource: false,
                    vendorSigned: false,
                    signerSubject: string.Empty,
                    suggestedInstallCommand: installCommand,
                    suggestedUninstallCommand: uninstallCommand,
                    detectionRule: detection.Rule,
                    detectionGuidance: detection.Guidance,
                    isDeterministicDetection: detection.IsDeterministic,
                    confidenceScore: template.ConfidenceScore,
                    publishedAtUtc: null)
            ];
        }

        var preferredVariant = variants
            .OrderByDescending(VariantPreferenceScore)
            .First();

        DateTimeOffset? releaseDate = null;
        if (DateTimeOffset.TryParse(map.GetValueOrDefault("Release Date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            releaseDate = parsed.ToUniversalTime();
        }

        return entry with
        {
            Name = packageName,
            PackageId = packageId,
            Version = version,
            BuildVersion = Coalesce(version, entry.BuildVersion),
            Publisher = publisher,
            Description = Coalesce(map.GetValueOrDefault("Description"), entry.Description),
            HomepageUrl = homepage,
            IconUrl = ResolveIconUrl(map.GetValueOrDefault("Icon"), homepage, packageId),
            InstallerDownloadUrl = Coalesce(preferredVariant.InstallerDownloadUrl, installerUrl),
            InstallerSha256 = Coalesce(preferredVariant.InstallerSha256, installerSha256),
            InstallerType = preferredVariant.InstallerType,
            InstallerTypeRaw = Coalesce(preferredVariant.InstallerTypeRaw, installerRaw),
            SuggestedInstallCommand = Coalesce(preferredVariant.SuggestedInstallCommand, installCommand),
            SuggestedUninstallCommand = Coalesce(preferredVariant.SuggestedUninstallCommand, uninstallCommand),
            DetectionGuidance = Coalesce(preferredVariant.DetectionGuidance, detection.Guidance),
            ConfidenceScore = Math.Max(template.ConfidenceScore, preferredVariant.ConfidenceScore),
            MetadataNotes = string.IsNullOrWhiteSpace(map.GetValueOrDefault("Installer Url"))
                ? manifest is null
                    ? $"Detailed metadata from WinGet source '{sourceName}'."
                    : $"Detailed metadata from WinGet source '{sourceName}' and winget-pkgs manifest."
                : $"Installer URL: {map["Installer Url"]}. Metadata: ProductName='{productName}', CompanyName='{companyName}', ProductVersion='{productVersion}'" +
                  (string.IsNullOrWhiteSpace(signer) ? string.Empty : $", Signer='{signer}'."),
            PublishedAtUtc = releaseDate,
            HasDetailedMetadata = true,
            InstallerVariants = variants
        };
    }

    private async Task<WingetManifestInfo?> TryGetWingetManifestInfoAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        try
        {
            var installerYaml = await TryDownloadTextAsync(
                BuildWingetManifestRawUrl(packageId, version, "installer"),
                providerId: "winget-manifest",
                sourceChannel: "winget-pkgs",
                cancellationToken);
            if (string.IsNullOrWhiteSpace(installerYaml))
            {
                return null;
            }

            var localeYaml = await TryDownloadTextAsync(
                BuildWingetManifestRawUrl(packageId, version, "locale.en-US"),
                providerId: "winget-manifest",
                sourceChannel: "winget-pkgs",
                cancellationToken);
            var versionYaml = await TryDownloadTextAsync(
                BuildWingetManifestRawUrl(packageId, version, string.Empty),
                providerId: "winget-manifest",
                sourceChannel: "winget-pkgs",
                cancellationToken);

            return ParseWingetManifestInfo(installerYaml, localeYaml, versionYaml);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> TryDownloadTextAsync(
        string url,
        string providerId,
        string sourceChannel,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        using var response = await SendHttpWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            providerId,
            sourceChannel,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string BuildWingetManifestRawUrl(string packageId, string version, string manifestSuffix)
    {
        var trimmedId = packageId.Trim();
        var trimmedVersion = version.Trim();
        if (trimmedId.Length == 0 || trimmedVersion.Length == 0)
        {
            return string.Empty;
        }

        var first = char.ToLowerInvariant(trimmedId[0]).ToString();
        var pathSegments = trimmedId
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.EscapeDataString);
        var fileName = string.IsNullOrWhiteSpace(manifestSuffix)
            ? $"{trimmedId}.yaml"
            : $"{trimmedId}.{manifestSuffix}.yaml";
        return string.Join(
            "/",
            WingetManifestRawBaseUrl,
            Uri.EscapeDataString(first),
            string.Join("/", pathSegments),
            Uri.EscapeDataString(trimmedVersion),
            Uri.EscapeDataString(fileName));
    }

    private static WingetManifestInfo ParseWingetManifestInfo(
        string installerYaml,
        string localeYaml,
        string versionYaml)
    {
        var installerRoot = ParseYamlTopLevelScalars(installerYaml, stopAtInstallers: true);
        var localeRoot = ParseYamlTopLevelScalars(localeYaml, stopAtInstallers: false);
        var versionRoot = ParseYamlTopLevelScalars(versionYaml, stopAtInstallers: false);
        var globalSwitches = ParseWingetGlobalInstallerSwitches(installerYaml);
        var installers = ParseWingetManifestInstallers(installerYaml, installerRoot, globalSwitches);

        return new WingetManifestInfo(
            PackageIdentifier: Coalesce(
                installerRoot.GetValueOrDefault("PackageIdentifier"),
                versionRoot.GetValueOrDefault("PackageIdentifier")),
            PackageVersion: Coalesce(
                installerRoot.GetValueOrDefault("PackageVersion"),
                versionRoot.GetValueOrDefault("PackageVersion")),
            PackageName: Coalesce(localeRoot.GetValueOrDefault("PackageName"), installerRoot.GetValueOrDefault("PackageName")),
            Publisher: Coalesce(localeRoot.GetValueOrDefault("Publisher"), installerRoot.GetValueOrDefault("Publisher")),
            HomepageUrl: Coalesce(
                localeRoot.GetValueOrDefault("PackageUrl"),
                localeRoot.GetValueOrDefault("PublisherUrl"),
                localeRoot.GetValueOrDefault("PublisherSupportUrl")),
            ReleaseDate: Coalesce(installerRoot.GetValueOrDefault("ReleaseDate"), versionRoot.GetValueOrDefault("ReleaseDate")),
            Installers: installers);
    }

    private static IReadOnlyList<WingetManifestInstallerInfo> ParseWingetManifestInstallers(
        string yaml,
        IReadOnlyDictionary<string, string> root,
        IReadOnlyDictionary<string, string> globalSwitches)
    {
        var builders = new List<WingetManifestInstallerBuilder>();
        var inInstallers = false;
        var installersIndent = -1;
        WingetManifestInstallerBuilder? current = null;
        var section = string.Empty;
        var sectionIndent = -1;

        foreach (var raw in SplitYamlLines(yaml))
        {
            if (IsIgnorableYamlLine(raw))
            {
                continue;
            }

            var indent = CountLeadingSpaces(raw);
            var trimmed = raw.Trim();
            if (!inInstallers)
            {
                if (trimmed.Equals("Installers:", StringComparison.OrdinalIgnoreCase))
                {
                    inInstallers = true;
                    installersIndent = indent;
                }

                continue;
            }

            if (indent <= installersIndent && !trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(section) || indent <= sectionIndent))
            {
                current = new WingetManifestInstallerBuilder();
                builders.Add(current);
                section = string.Empty;
                sectionIndent = -1;
                var inline = trimmed[2..].Trim();
                if (TryReadYamlKeyValue(inline, out var inlineKey, out var inlineValue))
                {
                    current.Values[inlineKey] = inlineValue;
                }

                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(section) && indent > sectionIndent)
            {
                var nested = trimmed.StartsWith("- ", StringComparison.Ordinal)
                    ? trimmed[2..].Trim()
                    : trimmed;
                if (TryReadYamlKeyValue(nested, out var nestedKey, out var nestedValue))
                {
                    var target = section switch
                    {
                        "switches" => current.Switches,
                        "apps" => current.AppsAndFeatures,
                        "metadata" => current.InstallationMetadata,
                        _ => current.Values
                    };
                    if (!target.ContainsKey(nestedKey))
                    {
                        target[nestedKey] = nestedValue;
                    }
                }

                continue;
            }

            section = string.Empty;
            sectionIndent = -1;
            if (!TryReadYamlKeyValue(trimmed, out var key, out var value))
            {
                continue;
            }

            if (key.Equals("InstallerSwitches", StringComparison.OrdinalIgnoreCase))
            {
                section = "switches";
                sectionIndent = indent;
                continue;
            }

            if (key.Equals("AppsAndFeaturesEntries", StringComparison.OrdinalIgnoreCase))
            {
                section = "apps";
                sectionIndent = indent;
                continue;
            }

            if (key.Equals("InstallationMetadata", StringComparison.OrdinalIgnoreCase))
            {
                section = "metadata";
                sectionIndent = indent;
                continue;
            }

            current.Values[key] = value;
        }

        if (builders.Count == 0)
        {
            var rootInstaller = new WingetManifestInstallerBuilder();
            foreach (var pair in root)
            {
                rootInstaller.Values[pair.Key] = pair.Value;
            }

            if (!string.IsNullOrWhiteSpace(rootInstaller.Values.GetValueOrDefault("InstallerUrl")))
            {
                builders.Add(rootInstaller);
            }
        }

        return builders
            .Select(builder => BuildWingetManifestInstallerInfo(builder, root, globalSwitches))
            .Where(installer => !string.IsNullOrWhiteSpace(installer.InstallerUrl))
            .ToList();
    }

    private static WingetManifestInstallerInfo BuildWingetManifestInstallerInfo(
        WingetManifestInstallerBuilder builder,
        IReadOnlyDictionary<string, string> root,
        IReadOnlyDictionary<string, string> globalSwitches)
    {
        var switches = new Dictionary<string, string>(globalSwitches, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in builder.Switches)
        {
            switches[pair.Key] = pair.Value;
        }

        return new WingetManifestInstallerInfo(
            InstallerTypeRaw: Coalesce(builder.Values.GetValueOrDefault("InstallerType"), root.GetValueOrDefault("InstallerType")),
            Architecture: Coalesce(builder.Values.GetValueOrDefault("Architecture"), root.GetValueOrDefault("Architecture")),
            Scope: Coalesce(builder.Values.GetValueOrDefault("Scope"), root.GetValueOrDefault("Scope")),
            InstallerUrl: Coalesce(builder.Values.GetValueOrDefault("InstallerUrl"), root.GetValueOrDefault("InstallerUrl")),
            InstallerSha256: NormalizeSha256(Coalesce(builder.Values.GetValueOrDefault("InstallerSha256"), root.GetValueOrDefault("InstallerSha256"))),
            ProductCode: Coalesce(
                builder.Values.GetValueOrDefault("ProductCode"),
                builder.AppsAndFeatures.GetValueOrDefault("ProductCode"),
                root.GetValueOrDefault("ProductCode")),
            SilentSwitch: Coalesce(switches.GetValueOrDefault("Silent")),
            SilentWithProgressSwitch: Coalesce(switches.GetValueOrDefault("SilentWithProgress")),
            InstallLocationSwitch: Coalesce(switches.GetValueOrDefault("InstallLocation")),
            DefaultInstallLocation: Coalesce(
                builder.InstallationMetadata.GetValueOrDefault("DefaultInstallLocation"),
                builder.Values.GetValueOrDefault("DefaultInstallLocation"),
                root.GetValueOrDefault("DefaultInstallLocation")),
            DisplayName: Coalesce(builder.AppsAndFeatures.GetValueOrDefault("DisplayName")),
            Publisher: Coalesce(builder.AppsAndFeatures.GetValueOrDefault("Publisher")),
            DisplayVersion: Coalesce(builder.AppsAndFeatures.GetValueOrDefault("DisplayVersion")));
    }

    private static IReadOnlyDictionary<string, string> ParseYamlTopLevelScalars(string yaml, bool stopAtInstallers)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in SplitYamlLines(yaml))
        {
            if (IsIgnorableYamlLine(raw))
            {
                continue;
            }

            if (CountLeadingSpaces(raw) != 0)
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (stopAtInstallers && trimmed.Equals("Installers:", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (TryReadYamlKeyValue(trimmed, out var key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                map[key] = value;
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> ParseWingetGlobalInstallerSwitches(string yaml)
    {
        var switches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSwitches = false;
        var switchesIndent = -1;
        foreach (var raw in SplitYamlLines(yaml))
        {
            if (IsIgnorableYamlLine(raw))
            {
                continue;
            }

            var indent = CountLeadingSpaces(raw);
            var trimmed = raw.Trim();
            if (trimmed.Equals("Installers:", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (inSwitches && indent > switchesIndent)
            {
                if (TryReadYamlKeyValue(trimmed, out var key, out var value))
                {
                    switches[key] = value;
                }

                continue;
            }

            inSwitches = false;
            if (trimmed.Equals("InstallerSwitches:", StringComparison.OrdinalIgnoreCase))
            {
                inSwitches = true;
                switchesIndent = indent;
            }
        }

        return switches;
    }

    private static IReadOnlyList<CatalogInstallerVariant> BuildWingetManifestInstallerVariants(
        WingetManifestInfo manifest,
        PackageCatalogSource source,
        string sourceDisplayName,
        string sourceChannel,
        string packageId,
        string packageName,
        string publisher,
        string version,
        string appxIdentity)
    {
        var variants = new List<CatalogInstallerVariant>();
        foreach (var installer in manifest.Installers)
        {
            if (!IsPotentialInstallerAsset(installer.InstallerUrl))
            {
                continue;
            }

            var installerType = InferInstallerType(installer.InstallerTypeRaw, installer.InstallerUrl);
            var template = BuildTemplate(packageId, installerType, installer.InstallerTypeRaw);
            var rawProductCode = installer.ProductCode;
            var msiProductCode = NormalizeMsiProductCode(rawProductCode);
            var uninstallRegistryKeyPath = installerType == InstallerType.Exe
                ? BuildUninstallRegistryPathFromProductCode(rawProductCode)
                : string.Empty;
            var detection = BuildDeterministicDetectionRule(
                installerType,
                packageId,
                packageName,
                publisher,
                version,
                msiProductCode,
                appxIdentity,
                uninstallRegistryKeyPath: uninstallRegistryKeyPath,
                uninstallDisplayName: Coalesce(installer.DisplayName, packageName),
                uninstallPublisher: Coalesce(installer.Publisher, publisher),
                uninstallDisplayVersion: Coalesce(installer.DisplayVersion, version),
                fileDetectionPath: installer.DefaultInstallLocation,
                fileDetectionName: string.Empty,
                fileDetectionVersion: Coalesce(installer.DisplayVersion, version),
                appxPublisher: publisher);
            var installCommand = Coalesce(BuildManifestInstallCommand(installerType, installer), template.InstallCommand);
            var uninstallCommand = ResolveUninstallTemplate(template.UninstallCommand, installerType, msiProductCode, appxIdentity);
            var confidence = Math.Min(100, template.ConfidenceScore + 8);

            variants.Add(BuildInstallerVariant(
                source,
                sourceDisplayName,
                sourceChannel,
                packageId,
                version,
                version,
                installerType,
                installer.InstallerTypeRaw,
                installer.Architecture,
                installer.Scope,
                installer.InstallerUrl,
                installer.InstallerSha256,
                hashVerifiedBySource: false,
                vendorSigned: false,
                signerSubject: string.Empty,
                suggestedInstallCommand: installCommand,
                suggestedUninstallCommand: uninstallCommand,
                detectionRule: detection.Rule,
                detectionGuidance: detection.Guidance,
                isDeterministicDetection: detection.IsDeterministic,
                confidenceScore: confidence,
                publishedAtUtc: DateTimeOffset.TryParse(manifest.ReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var releaseDate)
                    ? releaseDate.ToUniversalTime()
                    : null));
        }

        return variants
            .GroupBy(variant => variant.VariantKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(VariantPreferenceScore).First())
            .OrderByDescending(VariantPreferenceScore)
            .ToList();
    }

    private static string BuildManifestInstallCommand(InstallerType installerType, WingetManifestInstallerInfo? installer)
    {
        if (installer is null || installerType == InstallerType.Msi)
        {
            return string.Empty;
        }

        var silentSwitch = Coalesce(installer.SilentSwitch, installer.SilentWithProgressSwitch);
        return string.IsNullOrWhiteSpace(silentSwitch)
            ? string.Empty
            : $"\"<installer-file>\" {silentSwitch}";
    }

    private static int WingetManifestInstallerPreferenceScore(WingetManifestInstallerInfo installer)
    {
        var installerType = InferInstallerType(installer.InstallerTypeRaw, installer.InstallerUrl);
        var score = installerType switch
        {
            InstallerType.Msi => 55,
            InstallerType.AppxMsix => 48,
            InstallerType.Exe => 40,
            InstallerType.Script => 18,
            _ => 0
        };

        if (installerType == InstallerType.Msi && GuidCodeRegex.IsMatch(NormalizeMsiProductCode(installer.ProductCode)))
        {
            score += 30;
        }

        if (installerType == InstallerType.Exe && !string.IsNullOrWhiteSpace(BuildUninstallRegistryPathFromProductCode(installer.ProductCode)))
        {
            score += 18;
        }

        if (installer.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase))
        {
            score += 12;
        }
        else if (installer.Architecture.Equals("neutral", StringComparison.OrdinalIgnoreCase))
        {
            score += 6;
        }

        if (installer.Scope.Equals("machine", StringComparison.OrdinalIgnoreCase))
        {
            score += 8;
        }

        if (!string.IsNullOrWhiteSpace(installer.InstallerSha256))
        {
            score += 6;
        }

        if (installer.InstallerUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            score -= 24;
        }

        return score;
    }

    private static string BuildUninstallRegistryPathFromProductCode(string productCode)
    {
        var value = productCode.Trim().Trim('\'', '"');
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
        {
            return value["HKEY_LOCAL_MACHINE\\".Length..];
        }

        if (value.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
        {
            return value["HKLM\\".Length..];
        }

        if (value.Contains(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{value}";
    }

    private static IEnumerable<string> SplitYamlLines(string yaml)
    {
        return (yaml ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static bool IsIgnorableYamlLine(string line)
    {
        return string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal);
    }

    private static int CountLeadingSpaces(string value)
    {
        var count = 0;
        while (count < value.Length && value[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static bool TryReadYamlKeyValue(string value, out string key, out string parsedValue)
    {
        key = string.Empty;
        parsedValue = string.Empty;
        var idx = value.IndexOf(':');
        if (idx <= 0)
        {
            return false;
        }

        key = value[..idx].Trim();
        parsedValue = UnquoteYamlScalar(value[(idx + 1)..].Trim());
        return !string.IsNullOrWhiteSpace(key);
    }

    private static string UnquoteYamlScalar(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '\'' && trimmed[^1] == '\'') ||
             (trimmed[0] == '"' && trimmed[^1] == '"')))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.Replace("''", "'", StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<PackageCatalogEntry>> SearchChocolateyAsync(string term, int limit, CancellationToken cancellationToken)
    {
        var sources = await GetChocolateySourcesAsync(cancellationToken);
        if (sources.Count == 0)
        {
            return [];
        }

        var perSource = Math.Clamp(limit + 4, 1, 50);
        var tasks = sources
            .Select(source => SearchChocolateySourceAsync(source, term, perSource, cancellationToken))
            .ToArray();
        await Task.WhenAll(tasks);

        return tasks
            .SelectMany(task => task.Result)
            .OrderByDescending(entry => Relevance(entry, term))
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit * 2, limit, 90))
            .ToList();
    }

    private async Task<PackageCatalogEntry> GetChocolateyDetailsAsync(PackageCatalogEntry entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.Version))
        {
            return entry;
        }

        var safeId = entry.PackageId.Replace("'", "''", StringComparison.Ordinal);
        var safeVersion = entry.Version.Replace("'", "''", StringComparison.Ordinal);
        var source = await ResolveChocolateySourceAsync(entry.SourceChannel, cancellationToken);
        var apiBase = NormalizeChocolateyApiBaseUrl(source.ApiUrl);
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            return entry;
        }

        var url = $"{apiBase.TrimEnd('/')}/Packages(Id='{safeId}',Version='{safeVersion}')";
        using var response = await SendHttpWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            providerId: "chocolatey",
            sourceChannel: source.Name,
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return entry;
        }

        var detailed = ParseChocolateyEntries(
            await response.Content.ReadAsStringAsync(cancellationToken),
            source.Name,
            source.ApiUrl).FirstOrDefault();
        return detailed ?? entry;
    }

    private IReadOnlyList<PackageCatalogEntry> ParseChocolateyEntries(
        string xml,
        string sourceName,
        string sourceApiUrl)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return [];
        }

        var doc = XDocument.Parse(xml);
        var atom = XNamespace.Get("http://www.w3.org/2005/Atom");
        var d = XNamespace.Get("http://schemas.microsoft.com/ado/2007/08/dataservices");
        var m = XNamespace.Get("http://schemas.microsoft.com/ado/2007/08/dataservices/metadata");
        var root = doc.Root;
        if (root is null)
        {
            return [];
        }

        var atomEntries = root.Name == atom + "entry" ? [root] : root.Elements(atom + "entry");
        var parsed = new List<PackageCatalogEntry>();

        foreach (var atomEntry in atomEntries)
        {
            var properties = atomEntry.Descendants(m + "properties").FirstOrDefault();
            var id = properties?.Element(d + "Id")?.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = atomEntry.Element(atom + "title")?.Value?.Trim() ?? id;
            var summary = atomEntry.Element(atom + "summary")?.Value?.Trim() ?? string.Empty;
            var version = properties?.Element(d + "Version")?.Value?.Trim() ?? string.Empty;
            var tags = properties?.Element(d + "Tags")?.Value?.Trim() ?? string.Empty;
            var homepage = properties?.Element(d + "ProjectUrl")?.Value?.Trim() ?? string.Empty;
            var icon = properties?.Element(d + "IconUrl")?.Value?.Trim() ?? string.Empty;
            var contentNode = atomEntry.Element(atom + "content");
            var packageDownloadUrl = contentNode?.Attribute("src")?.Value?.Trim() ?? string.Empty;
            var author = atomEntry.Element(atom + "author")?.Element(atom + "name")?.Value?.Trim() ?? string.Empty;
            var installerType = InferInstallerType(tags, summary);
            var template = BuildTemplate(id, installerType, tags);
            var architecture = InferArchitecture(name, id, version, tags);
            var scope = InferScope(sourceName, name, id);
            var appxIdentity = installerType == InstallerType.AppxMsix ? id : string.Empty;
            var uninstallCommand = ResolveUninstallTemplate(template.UninstallCommand, installerType, msiProductCode: string.Empty, appxIdentity);
            var detection = BuildDeterministicDetectionRule(
                installerType,
                id,
                name,
                author,
                version,
                msiProductCode: string.Empty,
                appxIdentity,
                uninstallDisplayName: name,
                uninstallPublisher: author,
                uninstallDisplayVersion: version,
                appxPublisher: author);
            var variant = BuildInstallerVariant(
                PackageCatalogSource.Chocolatey,
                BuildChocolateyDisplayName(sourceName),
                sourceName,
                id,
                version,
                version,
                installerType,
                tags,
                architecture,
                scope,
                packageDownloadUrl,
                installerSha256: string.Empty,
                hashVerifiedBySource: false,
                vendorSigned: false,
                signerSubject: string.Empty,
                suggestedInstallCommand: template.InstallCommand,
                suggestedUninstallCommand: uninstallCommand,
                detectionRule: detection.Rule,
                detectionGuidance: detection.Guidance,
                isDeterministicDetection: detection.IsDeterministic,
                confidenceScore: Math.Max(30, template.ConfidenceScore - 20),
                publishedAtUtc: null);

            parsed.Add(new PackageCatalogEntry
            {
                Source = PackageCatalogSource.Chocolatey,
                SourceDisplayName = BuildChocolateyDisplayName(sourceName),
                SourceChannel = sourceName,
                PackageId = id,
                Name = name,
                Version = version,
                BuildVersion = version,
                Publisher = author,
                Description = Truncate(summary, 260),
                HomepageUrl = homepage,
                IconUrl = ResolveIconUrl(icon, homepage, id),
                InstallerDownloadUrl = packageDownloadUrl,
                InstallerType = installerType,
                InstallerTypeRaw = tags,
                SuggestedInstallCommand = template.InstallCommand,
                SuggestedUninstallCommand = uninstallCommand,
                DetectionGuidance = detection.Guidance,
                MetadataNotes = string.IsNullOrWhiteSpace(tags)
                    ? $"Metadata from Chocolatey source '{sourceName}'."
                    : $"Source: {sourceName}. Tags: {tags}",
                ConfidenceScore = Math.Max(30, template.ConfidenceScore - 20),
                HasDetailedMetadata = true,
                InstallerVariants = [variant]
            });
        }

        return parsed;
    }

    private async Task<IReadOnlyList<PackageCatalogEntry>> SearchChocolateySourceAsync(
        ChocolateySourceInfo source,
        string term,
        int limit,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(source.ApiUrl))
        {
            return [];
        }

        var encodedTerm = Uri.EscapeDataString($"'{term}'");
        var sourceBase = NormalizeChocolateyApiBaseUrl(source.ApiUrl);
        if (string.IsNullOrWhiteSpace(sourceBase))
        {
            return [];
        }

        var url =
            $"{sourceBase.TrimEnd('/')}/Search()?%24filter=IsLatestVersion&%24top={Math.Clamp(limit, 1, 50)}&searchTerm={encodedTerm}&targetFramework=%27%27&includePrerelease=false";

        using var response = await SendHttpWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            providerId: "chocolatey",
            sourceChannel: source.Name,
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await RecordProviderFailureAsync(
                "chocolatey",
                source.Name,
                $"Search failed with HTTP {(int)response.StatusCode}.",
                isTimeout: false,
                cancellationToken);
            return [];
        }

        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
        var entries = ParseChocolateyEntries(xml, source.Name, source.ApiUrl);
        await RecordProviderSuccessAsync(
            "chocolatey",
            source.Name,
            durationMs: stopwatch.ElapsedMilliseconds,
            cancellationToken);
        return entries;
    }

    private async Task<IReadOnlyList<ChocolateySourceInfo>> GetChocolateySourcesAsync(CancellationToken cancellationToken)
    {
        var configured = await TryGetConfiguredChocolateySourcesAsync(cancellationToken);
        if (configured.Count > 0)
        {
            return configured;
        }

        return
        [
            new ChocolateySourceInfo(
                "community",
                "https://community.chocolatey.org/api/v2/",
                IsEnabled: true)
        ];
    }

    private async Task<ChocolateySourceInfo> ResolveChocolateySourceAsync(string sourceChannel, CancellationToken cancellationToken)
    {
        var sources = await GetChocolateySourcesAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(sourceChannel))
        {
            var match = sources.FirstOrDefault(source =>
                source.Name.Equals(sourceChannel, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return sources.First();
    }

    private async Task<IReadOnlyList<ChocolateySourceInfo>> TryGetConfiguredChocolateySourcesAsync(CancellationToken cancellationToken)
    {
        (int ExitCode, IReadOnlyList<string> Lines) result;
        try
        {
            result = await RunProcessCaptureAsync(
                "choco",
                "source list --allow-unofficial --limit-output",
                cancellationToken);
        }
        catch
        {
            return [];
        }

        if (result.ExitCode != 0 || result.Lines.Count == 0)
        {
            return [];
        }

        var parsed = new List<ChocolateySourceInfo>();
        foreach (var line in result.Lines)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains('|', StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = line.Split('|', StringSplitOptions.TrimEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            var name = tokens[0];
            var url = tokens[1];
            var disabledToken = tokens.Length >= 3 ? tokens[2] : "false";
            var isEnabled = !disabledToken.Equals("true", StringComparison.OrdinalIgnoreCase) &&
                            !disabledToken.Equals("disabled", StringComparison.OrdinalIgnoreCase);
            if (!isEnabled || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            parsed.Add(new ChocolateySourceInfo(name, url, IsEnabled: true));
        }

        if (parsed.Count > 0)
        {
            return parsed
                .GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        return [];
    }

    private static string NormalizeChocolateyApiBaseUrl(string apiUrl)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            return string.Empty;
        }

        var trimmed = apiUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/api/v2", StringComparison.OrdinalIgnoreCase))
        {
            path = $"{path}/api/v2";
        }

        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}{path}";
    }

    private static string BuildChocolateyDisplayName(string sourceName)
    {
        return sourceName.Equals("community", StringComparison.OrdinalIgnoreCase)
            ? "Chocolatey"
            : $"Chocolatey ({sourceName})";
    }

    private async Task<IReadOnlyList<PackageCatalogEntry>> SearchScoopAsync(string term, int limit, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var (exitCode, lines) = await RunProcessCaptureAsync(
            "scoop",
            $"search {QuoteArgument(term)}",
            cancellationToken);
        if (exitCode != 0 || lines.Count == 0)
        {
            await RecordProviderFailureAsync(
                "scoop",
                "configured",
                $"scoop search failed (exit {exitCode}).",
                isTimeout: false,
                cancellationToken);
            return [];
        }

        var rows = ParseScoopSearchRows(lines);
        if (rows.Count == 0)
        {
            await RecordProviderSuccessAsync("scoop", "configured", stopwatch.ElapsedMilliseconds, cancellationToken);
            return [];
        }

        var entries = new List<PackageCatalogEntry>();
        foreach (var row in rows.Take(Math.Clamp(limit, 1, 50)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await TryGetScoopManifestAsync(row.Name, cancellationToken);
            var version = Coalesce(manifest?.Version, row.Version);
            var publisher = Coalesce(manifest?.Publisher, row.Bucket, "Scoop");
            var sourceChannel = Coalesce(manifest?.Bucket, row.Bucket, "main");
            var installerUrl = Coalesce(manifest?.InstallerUrl);
            var installerType = InferInstallerType(Path.GetExtension(installerUrl), manifest?.Notes);
            var template = BuildTemplate(row.Name, installerType, manifest?.InstallerTypeRaw);
            var appxIdentity = installerType == InstallerType.AppxMsix
                ? InferAppxIdentity(row.Name, manifest?.Name ?? row.Name, row.Name)
                : string.Empty;
            var detection = BuildDeterministicDetectionRule(
                installerType,
                row.Name,
                manifest?.Name ?? row.Name,
                publisher,
                version,
                msiProductCode: string.Empty,
                appxIdentity,
                uninstallDisplayName: Coalesce(manifest?.Name, row.Name),
                uninstallPublisher: publisher,
                uninstallDisplayVersion: version,
                appxPublisher: publisher);
            var uninstall = ResolveUninstallTemplate(
                template.UninstallCommand,
                installerType,
                msiProductCode: string.Empty,
                appxIdentity);
            var variant = BuildInstallerVariant(
                PackageCatalogSource.Scoop,
                "Scoop",
                sourceChannel,
                row.Name,
                version,
                version,
                installerType,
                Coalesce(manifest?.InstallerTypeRaw, Path.GetExtension(installerUrl)),
                InferArchitecture(row.Name, version, sourceChannel, manifest?.Notes ?? string.Empty),
                InferScope(sourceChannel, row.Name, manifest?.Notes ?? string.Empty),
                installerUrl,
                manifest?.InstallerSha256 ?? string.Empty,
                hashVerifiedBySource: !string.IsNullOrWhiteSpace(manifest?.InstallerSha256),
                vendorSigned: false,
                signerSubject: string.Empty,
                suggestedInstallCommand: template.InstallCommand,
                suggestedUninstallCommand: uninstall,
                detectionRule: detection.Rule,
                detectionGuidance: detection.Guidance,
                isDeterministicDetection: detection.IsDeterministic,
                confidenceScore: Math.Max(46, template.ConfidenceScore - 18),
                publishedAtUtc: null);

            entries.Add(new PackageCatalogEntry
            {
                Source = PackageCatalogSource.Scoop,
                SourceDisplayName = "Scoop",
                SourceChannel = sourceChannel,
                PackageId = row.Name,
                Name = Coalesce(manifest?.Name, row.Name),
                Version = version,
                BuildVersion = version,
                Publisher = publisher,
                Description = Truncate(Coalesce(manifest?.Description, manifest?.Notes), 260),
                HomepageUrl = Coalesce(manifest?.HomepageUrl),
                IconUrl = ResolveIconUrl(manifest?.IconUrl, manifest?.HomepageUrl, row.Name),
                InstallerDownloadUrl = installerUrl,
                InstallerType = installerType,
                InstallerTypeRaw = Coalesce(manifest?.InstallerTypeRaw, Path.GetExtension(installerUrl)),
                SuggestedInstallCommand = template.InstallCommand,
                SuggestedUninstallCommand = uninstall,
                DetectionGuidance = detection.Guidance,
                MetadataNotes = $"Scoop bucket: {sourceChannel}",
                ConfidenceScore = Math.Max(46, template.ConfidenceScore - 18),
                HasDetailedMetadata = manifest is not null,
                InstallerVariants = [variant]
            });
        }

        await RecordProviderSuccessAsync("scoop", "configured", stopwatch.ElapsedMilliseconds, cancellationToken);
        return entries;
    }

    private async Task<PackageCatalogEntry> GetScoopDetailsAsync(PackageCatalogEntry entry, CancellationToken cancellationToken)
    {
        var manifest = await TryGetScoopManifestAsync(entry.PackageId, cancellationToken);
        if (manifest is null)
        {
            return entry;
        }

        var version = Coalesce(manifest.Version, entry.Version, entry.BuildVersion);
        var installerUrl = Coalesce(manifest.InstallerUrl, entry.InstallerDownloadUrl);
        var installerType = InferInstallerType(Path.GetExtension(installerUrl), manifest.InstallerTypeRaw);
        var template = BuildTemplate(entry.PackageId, installerType, manifest.InstallerTypeRaw);
        var appxIdentity = installerType == InstallerType.AppxMsix
            ? InferAppxIdentity(entry.PackageId, manifest.Name, manifest.Name)
            : string.Empty;
        var detection = BuildDeterministicDetectionRule(
            installerType,
            entry.PackageId,
            Coalesce(manifest.Name, entry.Name),
            Coalesce(entry.Publisher, manifest.Publisher, "Scoop"),
            version,
            msiProductCode: string.Empty,
            appxIdentity,
            uninstallDisplayName: Coalesce(manifest.Name, entry.Name),
            uninstallPublisher: Coalesce(entry.Publisher, manifest.Publisher, "Scoop"),
            uninstallDisplayVersion: version,
            appxPublisher: Coalesce(entry.Publisher, manifest.Publisher, "Scoop"));
        var uninstall = ResolveUninstallTemplate(
            template.UninstallCommand,
            installerType,
            msiProductCode: string.Empty,
            appxIdentity);
        var variant = BuildInstallerVariant(
            PackageCatalogSource.Scoop,
            "Scoop",
            Coalesce(manifest.Bucket, entry.SourceChannel),
            entry.PackageId,
            version,
            version,
            installerType,
            Coalesce(manifest.InstallerTypeRaw, entry.InstallerTypeRaw),
            InferArchitecture(manifest.Name, version, manifest.Bucket, manifest.Notes),
            InferScope(manifest.Bucket, manifest.Name, manifest.Notes),
            installerUrl,
            manifest.InstallerSha256,
            hashVerifiedBySource: !string.IsNullOrWhiteSpace(manifest.InstallerSha256),
            vendorSigned: false,
            signerSubject: string.Empty,
            suggestedInstallCommand: template.InstallCommand,
            suggestedUninstallCommand: uninstall,
            detectionRule: detection.Rule,
            detectionGuidance: detection.Guidance,
            isDeterministicDetection: detection.IsDeterministic,
            confidenceScore: Math.Max(entry.ConfidenceScore, template.ConfidenceScore),
            publishedAtUtc: null);

        return entry with
        {
            SourceDisplayName = "Scoop",
            SourceChannel = Coalesce(manifest.Bucket, entry.SourceChannel),
            Name = Coalesce(manifest.Name, entry.Name),
            Version = version,
            BuildVersion = version,
            Publisher = Coalesce(entry.Publisher, manifest.Publisher, "Scoop"),
            Description = Truncate(Coalesce(manifest.Description, manifest.Notes, entry.Description), 260),
            HomepageUrl = Coalesce(manifest.HomepageUrl, entry.HomepageUrl),
            IconUrl = ResolveIconUrl(manifest.IconUrl, manifest.HomepageUrl, entry.PackageId),
            InstallerDownloadUrl = installerUrl,
            InstallerType = installerType,
            InstallerTypeRaw = Coalesce(manifest.InstallerTypeRaw, entry.InstallerTypeRaw),
            SuggestedInstallCommand = template.InstallCommand,
            SuggestedUninstallCommand = uninstall,
            DetectionGuidance = detection.Guidance,
            MetadataNotes = $"Scoop bucket: {Coalesce(manifest.Bucket, entry.SourceChannel, "main")}",
            HasDetailedMetadata = true,
            ConfidenceScore = Math.Max(entry.ConfidenceScore, template.ConfidenceScore),
            InstallerVariants = [variant]
        };
    }

    private async Task<PackageCatalogDownloadResult> DownloadScoopInstallerAsync(
        PackageCatalogEntry entry,
        string workingFolder,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var details = entry;
        if (string.IsNullOrWhiteSpace(details.InstallerDownloadUrl))
        {
            details = await GetScoopDetailsAsync(entry, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(details.InstallerDownloadUrl))
        {
            return new PackageCatalogDownloadResult
            {
                Success = false,
                Message = "No installer URL was available for this Scoop package.",
                WorkingFolderPath = workingFolder
            };
        }

        var fileName = BuildDownloadFileNameFromUrl(details.InstallerDownloadUrl, details.PackageId, details.Version);
        var targetPath = Path.Combine(workingFolder, fileName);
        await DownloadFileAsync(details.InstallerDownloadUrl, targetPath, progress, cancellationToken);
        var resolvedInstaller = await ResolveDownloadedInstallerPathAsync(targetPath, workingFolder, progress, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedInstaller))
        {
            return new PackageCatalogDownloadResult
            {
                Success = false,
                Message = "Downloaded Scoop artifact did not contain a supported installer file.",
                WorkingFolderPath = workingFolder
            };
        }

        var hashVerified = !string.IsNullOrWhiteSpace(details.InstallerSha256) &&
                           details.InstallerSha256.Equals(ComputeFileSha256(resolvedInstaller), StringComparison.OrdinalIgnoreCase);
        return BuildSuccessfulDownloadResult(
            resolvedInstaller,
            Path.GetDirectoryName(resolvedInstaller) ?? workingFolder,
            hashVerified
                ? "Downloaded installer from Scoop and verified source hash."
                : "Downloaded installer from Scoop.",
            hashVerifiedBySource: hashVerified);
    }

    private static string ExtractScoopPublisher(string homepageUrl)
    {
        if (string.IsNullOrWhiteSpace(homepageUrl) || !Uri.TryCreate(homepageUrl, UriKind.Absolute, out var uri))
        {
            return "Scoop";
        }

        var host = uri.Host.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host))
        {
            return "Scoop";
        }

        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        var segments = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return "Scoop";
        }

        if (segments.Length >= 2)
        {
            return segments[^2];
        }

        return segments[0];
    }

    private static string ExtractScoopBucketFromPackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return "main";
        }

        var normalized = packageId.Trim().Replace('\\', '/');
        var separatorIndex = normalized.IndexOf('/');
        if (separatorIndex > 0)
        {
            return normalized[..separatorIndex].Trim();
        }

        return "main";
    }

    private async Task<ScoopManifestInfo?> TryGetScoopManifestAsync(string packageId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        var (exitCode, lines) = await RunProcessCaptureAsync(
            "scoop",
            $"cat {QuoteArgument(packageId)}",
            cancellationToken);
        if (exitCode != 0 || lines.Count == 0)
        {
            return null;
        }

        var jsonPayload = string.Join(Environment.NewLine, lines);
        var start = jsonPayload.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        jsonPayload = jsonPayload[start..];
        try
        {
            using var json = JsonDocument.Parse(jsonPayload);
            var root = json.RootElement;
            var name = Coalesce(GetJsonStringOrDefault(root, "name"), packageId);
            var version = GetJsonStringOrDefault(root, "version");
            var description = Coalesce(GetJsonStringOrDefault(root, "description"), GetJsonStringOrDefault(root, "notes"));
            var homepage = GetJsonStringOrDefault(root, "homepage");
            var notes = GetJsonStringOrDefault(root, "notes");
            var publisher = ExtractScoopPublisher(homepage);
            var bucket = ExtractScoopBucketFromPackageId(packageId);
            var installerTypeRaw = string.Empty;
            var installerUrl = string.Empty;
            var installerSha = string.Empty;

            if (root.TryGetProperty("architecture", out var architecture) &&
                architecture.ValueKind == JsonValueKind.Object)
            {
                if (architecture.TryGetProperty("64bit", out var bit64) && bit64.ValueKind == JsonValueKind.Object)
                {
                    installerUrl = Coalesce(GetJsonStringOrDefault(bit64, "url"), installerUrl);
                    installerSha = Coalesce(GetJsonStringOrDefault(bit64, "hash"), installerSha);
                }

                if (string.IsNullOrWhiteSpace(installerUrl) &&
                    architecture.TryGetProperty("32bit", out var bit32) &&
                    bit32.ValueKind == JsonValueKind.Object)
                {
                    installerUrl = Coalesce(GetJsonStringOrDefault(bit32, "url"), installerUrl);
                    installerSha = Coalesce(GetJsonStringOrDefault(bit32, "hash"), installerSha);
                }
            }

            installerUrl = Coalesce(installerUrl, GetJsonStringOrDefault(root, "url"));
            installerSha = Coalesce(installerSha, GetJsonStringOrDefault(root, "hash"));
            installerTypeRaw = Path.GetExtension(installerUrl);

            return new ScoopManifestInfo(
                name,
                version,
                description,
                homepage,
                notes,
                bucket,
                publisher,
                installerUrl,
                NormalizeSha256(installerSha),
                installerTypeRaw,
                IconUrl: string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static List<ScoopSearchRow> ParseScoopSearchRows(IReadOnlyList<string> lines)
    {
        var rows = new List<ScoopSearchRow>();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var line = raw.Trim();
            if (line.StartsWith("Results", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("---", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var columns = MultiSpaceRegex.Split(line)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            if (columns.Length >= 2)
            {
                var name = columns[0].Trim();
                var version = columns[1].Trim();
                var bucket = columns.Length >= 3 ? columns[2].Trim() : "main";
                rows.Add(new ScoopSearchRow(name, version, bucket));
                continue;
            }

            var regexMatch = ScoopSearchRegex.Match(line);
            if (regexMatch.Success)
            {
                rows.Add(new ScoopSearchRow(
                    regexMatch.Groups["name"].Value.Trim(),
                    regexMatch.Groups["version"].Value.Trim(),
                    Coalesce(regexMatch.Groups["bucket"].Value.Trim(), "main")));
            }
        }

        return rows
            .GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<IReadOnlyList<NuGetSourceInfo>> GetNuGetSourcesAsync(CancellationToken cancellationToken)
    {
        var configured = await TryGetConfiguredNuGetSourcesAsync(cancellationToken);
        if (configured.Count > 0)
        {
            return configured;
        }

        return
        [
            new NuGetSourceInfo(
                "nuget.org",
                "https://api.nuget.org/v3/index.json",
                IsEnabled: true)
        ];
    }

    private async Task<IReadOnlyList<NuGetSourceInfo>> TryGetConfiguredNuGetSourcesAsync(CancellationToken cancellationToken)
    {
        (int ExitCode, IReadOnlyList<string> Lines) result;
        try
        {
            result = await RunProcessCaptureAsync(
                "dotnet",
                "nuget list source",
                cancellationToken);
        }
        catch
        {
            return [];
        }

        if (result.ExitCode != 0 || result.Lines.Count == 0)
        {
            return [];
        }

        var parsed = new List<NuGetSourceInfo>();
        var headerSeen = false;
        var pendingName = string.Empty;
        var pendingEnabled = false;
        foreach (var raw in result.Lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!headerSeen && line.StartsWith("Registered Sources", StringComparison.OrdinalIgnoreCase))
            {
                headerSeen = true;
                continue;
            }

            if (!headerSeen)
            {
                continue;
            }

            var inlineMatch = Regex.Match(
                line,
                @"^(?:\d+\.\s*)?(?<name>[^\[]+?)\s*\[(?<state>Enabled|Disabled)\]\s*(?<url>\S+)?$",
                RegexOptions.IgnoreCase);
            if (inlineMatch.Success)
            {
                pendingName = inlineMatch.Groups["name"].Value.Trim().TrimEnd('.');
                pendingEnabled = inlineMatch.Groups["state"].Value.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
                var inlineUrl = inlineMatch.Groups["url"].Success
                    ? inlineMatch.Groups["url"].Value.Trim()
                    : string.Empty;
                if (pendingEnabled && !string.IsNullOrWhiteSpace(inlineUrl))
                {
                    parsed.Add(new NuGetSourceInfo(pendingName, inlineUrl, IsEnabled: true));
                    pendingName = string.Empty;
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(pendingName) &&
                pendingEnabled &&
                (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                parsed.Add(new NuGetSourceInfo(pendingName, line, IsEnabled: true));
                pendingName = string.Empty;
                continue;
            }
        }

        return parsed
            .Where(source => source.IsEnabled)
            .GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<ResolvedNuGetSourceInfo> ResolveNuGetSourceInfoAsync(NuGetSourceInfo source, CancellationToken cancellationToken)
    {
        var normalizedIndex = NormalizeNuGetIndexUrl(source.IndexUrl);
        if (string.IsNullOrWhiteSpace(normalizedIndex))
        {
            return new ResolvedNuGetSourceInfo(source.Name, source.IndexUrl, string.Empty, string.Empty);
        }

        try
        {
            using var response = await SendHttpWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, normalizedIndex),
                providerId: "nuget",
                sourceChannel: source.Name,
                cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ResolvedNuGetSourceInfo(source.Name, normalizedIndex, string.Empty, string.Empty);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!json.RootElement.TryGetProperty("resources", out var resources) ||
                resources.ValueKind != JsonValueKind.Array)
            {
                return new ResolvedNuGetSourceInfo(source.Name, normalizedIndex, string.Empty, string.Empty);
            }

            var searchUrl = string.Empty;
            var packageBaseAddressUrl = string.Empty;
            foreach (var resource in resources.EnumerateArray())
            {
                var resourceType = GetJsonStringOrDefault(resource, "@type");
                var resourceUrl = GetJsonStringOrDefault(resource, "@id");
                if (string.IsNullOrWhiteSpace(resourceType) || string.IsNullOrWhiteSpace(resourceUrl))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(searchUrl) &&
                    resourceType.Contains("SearchQueryService", StringComparison.OrdinalIgnoreCase))
                {
                    searchUrl = resourceUrl;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(packageBaseAddressUrl) &&
                    resourceType.Contains("PackageBaseAddress", StringComparison.OrdinalIgnoreCase))
                {
                    packageBaseAddressUrl = resourceUrl;
                }
            }

            if (string.IsNullOrWhiteSpace(searchUrl) &&
                normalizedIndex.Contains("api.nuget.org", StringComparison.OrdinalIgnoreCase))
            {
                searchUrl = "https://azuresearch-usnc.nuget.org/query";
            }

            if (string.IsNullOrWhiteSpace(packageBaseAddressUrl) &&
                normalizedIndex.Contains("api.nuget.org", StringComparison.OrdinalIgnoreCase))
            {
                packageBaseAddressUrl = "https://api.nuget.org/v3-flatcontainer/";
            }

            return new ResolvedNuGetSourceInfo(
                source.Name,
                normalizedIndex,
                NormalizeNuGetEndpointUrl(searchUrl, ensureTrailingSlash: false),
                NormalizeNuGetEndpointUrl(packageBaseAddressUrl, ensureTrailingSlash: true));
        }
        catch
        {
            return new ResolvedNuGetSourceInfo(source.Name, normalizedIndex, string.Empty, string.Empty);
        }
    }

    private static string NormalizeNuGetIndexUrl(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return string.Empty;
        }

        var trimmed = sourceUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var asString = uri.ToString();
        if (asString.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase))
        {
            return asString;
        }

        if (asString.EndsWith("/v3", StringComparison.OrdinalIgnoreCase))
        {
            return $"{asString.TrimEnd('/')}/index.json";
        }

        if (uri.Host.Equals("api.nuget.org", StringComparison.OrdinalIgnoreCase))
        {
            return "https://api.nuget.org/v3/index.json";
        }

        return asString;
    }

    private static string NormalizeNuGetEndpointUrl(string endpointUrl, bool ensureTrailingSlash)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(endpointUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var normalized = uri.ToString();
        if (ensureTrailingSlash && !normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        return normalized;
    }

    private static string BuildNuGetPackageDownloadUrl(string packageBaseAddressUrl, string packageId, string version)
    {
        if (string.IsNullOrWhiteSpace(packageBaseAddressUrl) ||
            string.IsNullOrWhiteSpace(packageId) ||
            string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var normalizedBase = NormalizeNuGetEndpointUrl(packageBaseAddressUrl, ensureTrailingSlash: true);
        if (string.IsNullOrWhiteSpace(normalizedBase))
        {
            return string.Empty;
        }

        var id = packageId.Trim().ToLowerInvariant();
        var normalizedVersion = version.Trim().ToLowerInvariant();
        return
            $"{normalizedBase}{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(normalizedVersion)}/{Uri.EscapeDataString(id)}.{Uri.EscapeDataString(normalizedVersion)}.nupkg";
    }

    private static string BuildNuGetDisplayName(string sourceName)
    {
        return sourceName.Equals("nuget.org", StringComparison.OrdinalIgnoreCase)
            ? "NuGet"
            : $"NuGet ({sourceName})";
    }

    private async Task<IReadOnlyList<PackageCatalogEntry>> SearchNuGetAsync(string term, int limit, CancellationToken cancellationToken)
    {
        var sources = await GetNuGetSourcesAsync(cancellationToken);
        if (sources.Count == 0)
        {
            return [];
        }

        var perSource = Math.Clamp(limit + 4, 1, 50);
        var tasks = sources
            .Select(source => SearchNuGetSourceAsync(source, term, perSource, cancellationToken))
            .ToArray();
        await Task.WhenAll(tasks);

        return tasks
            .SelectMany(task => task.Result)
            .OrderByDescending(entry => Relevance(entry, term))
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit * 2, limit, 90))
            .ToList();
    }

    private async Task<IReadOnlyList<PackageCatalogEntry>> SearchNuGetSourceAsync(
        NuGetSourceInfo source,
        string term,
        int limit,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var resolved = await ResolveNuGetSourceInfoAsync(source, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolved.SearchQueryServiceUrl))
        {
            await RecordProviderFailureAsync(
                "nuget",
                source.Name,
                "NuGet source has no SearchQueryService endpoint.",
                isTimeout: false,
                cancellationToken);
            return [];
        }

        var url = $"{resolved.SearchQueryServiceUrl}?q={Uri.EscapeDataString(term)}&skip=0&take={Math.Clamp(limit, 1, 50)}&prerelease=false&semVerLevel=2.0.0";
        using var response = await SendHttpWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            providerId: "nuget",
            sourceChannel: source.Name,
            cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await RecordProviderFailureAsync(
                "nuget",
                source.Name,
                $"NuGet search failed with HTTP {(int)response.StatusCode}.",
                isTimeout: false,
                cancellationToken);
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            await RecordProviderSuccessAsync("nuget", source.Name, stopwatch.ElapsedMilliseconds, cancellationToken);
            return [];
        }

        var entries = new List<PackageCatalogEntry>();
        foreach (var package in data.EnumerateArray())
        {
            var id = GetJsonStringOrDefault(package, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var version = GetJsonStringOrDefault(package, "version");
            var description = Truncate(GetJsonStringOrDefault(package, "description"), 260);
            var publisher = GetJsonStringOrDefault(package, "authors");
            var homepage = Coalesce(
                GetJsonStringOrDefault(package, "projectUrl"),
                GetJsonStringOrDefault(package, "licenseUrl"));
            var icon = Coalesce(GetJsonStringOrDefault(package, "iconUrl"), GetJsonStringOrDefault(package, "icon"));
            var packageUrl = BuildNuGetPackageDownloadUrl(resolved.PackageBaseAddressUrl, id, version);
            var installerType = InferInstallerType(Path.GetExtension(packageUrl), description);
            var template = BuildTemplate(id, installerType, Path.GetExtension(packageUrl));
            var appxIdentity = installerType == InstallerType.AppxMsix
                ? InferAppxIdentity(id, id, id)
                : string.Empty;
            var detection = BuildDeterministicDetectionRule(
                installerType,
                id,
                id,
                publisher,
                version,
                msiProductCode: string.Empty,
                appxIdentity,
                uninstallDisplayName: id,
                uninstallPublisher: publisher,
                uninstallDisplayVersion: version,
                appxPublisher: publisher);
            var uninstall = ResolveUninstallTemplate(
                template.UninstallCommand,
                installerType,
                msiProductCode: string.Empty,
                appxIdentity);
            var confidence = installerType == InstallerType.Unknown
                ? 30
                : Math.Max(42, template.ConfidenceScore - 24);
            var variant = BuildInstallerVariant(
                PackageCatalogSource.NuGet,
                BuildNuGetDisplayName(source.Name),
                source.Name,
                id,
                version,
                version,
                installerType,
                Path.GetExtension(packageUrl),
                InferArchitecture(id, version, source.Name, description),
                InferScope(source.Name, id, description),
                packageUrl,
                installerSha256: string.Empty,
                hashVerifiedBySource: false,
                vendorSigned: false,
                signerSubject: string.Empty,
                suggestedInstallCommand: template.InstallCommand,
                suggestedUninstallCommand: uninstall,
                detectionRule: detection.Rule,
                detectionGuidance: detection.Guidance,
                isDeterministicDetection: detection.IsDeterministic,
                confidenceScore: confidence,
                publishedAtUtc: null);

            entries.Add(new PackageCatalogEntry
            {
                Source = PackageCatalogSource.NuGet,
                SourceDisplayName = BuildNuGetDisplayName(source.Name),
                SourceChannel = source.Name,
                PackageId = id,
                Name = id,
                Version = version,
                BuildVersion = version,
                Publisher = publisher,
                Description = description,
                HomepageUrl = homepage,
                IconUrl = ResolveIconUrl(icon, homepage, id),
                InstallerDownloadUrl = packageUrl,
                InstallerType = installerType,
                InstallerTypeRaw = Path.GetExtension(packageUrl),
                SuggestedInstallCommand = template.InstallCommand,
                SuggestedUninstallCommand = uninstall,
                DetectionGuidance = detection.Guidance,
                MetadataNotes = $"NuGet source: {source.Name}",
                ConfidenceScore = confidence,
                HasDetailedMetadata = true,
                InstallerVariants = [variant]
            });
        }

        await RecordProviderSuccessAsync("nuget", source.Name, stopwatch.ElapsedMilliseconds, cancellationToken);
        return entries;
    }

    private async Task<PackageCatalogEntry> GetNuGetDetailsAsync(PackageCatalogEntry entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.PackageId))
        {
            return entry;
        }

        var sources = await GetNuGetSourcesAsync(cancellationToken);
        var source = sources.FirstOrDefault(candidate =>
                         candidate.Name.Equals(entry.SourceChannel, StringComparison.OrdinalIgnoreCase))
                     ?? sources.FirstOrDefault();
        if (source is null)
        {
            return entry;
        }

        var resolved = await ResolveNuGetSourceInfoAsync(source, cancellationToken);
        var packageUrl = BuildNuGetPackageDownloadUrl(resolved.PackageBaseAddressUrl, entry.PackageId, entry.Version);
        var installerType = InferInstallerType(Path.GetExtension(packageUrl), entry.Description);
        var template = BuildTemplate(entry.PackageId, installerType, Path.GetExtension(packageUrl));
        var appxIdentity = installerType == InstallerType.AppxMsix
            ? InferAppxIdentity(entry.PackageId, entry.Name, entry.PackageId)
            : string.Empty;
        var detection = BuildDeterministicDetectionRule(
            installerType,
            entry.PackageId,
            entry.Name,
            entry.Publisher,
            entry.Version,
            msiProductCode: string.Empty,
            appxIdentity,
            uninstallDisplayName: entry.Name,
            uninstallPublisher: entry.Publisher,
            uninstallDisplayVersion: entry.Version,
            appxPublisher: entry.Publisher);
        var uninstall = ResolveUninstallTemplate(
            template.UninstallCommand,
            installerType,
            msiProductCode: string.Empty,
            appxIdentity);
        var variant = BuildInstallerVariant(
            PackageCatalogSource.NuGet,
            BuildNuGetDisplayName(source.Name),
            source.Name,
            entry.PackageId,
            entry.Version,
            entry.BuildVersion,
            installerType,
            Path.GetExtension(packageUrl),
            InferArchitecture(entry.PackageId, entry.Version, source.Name, entry.Description),
            InferScope(source.Name, entry.PackageId, entry.Description),
            packageUrl,
            installerSha256: string.Empty,
            hashVerifiedBySource: false,
            vendorSigned: false,
            signerSubject: string.Empty,
            suggestedInstallCommand: template.InstallCommand,
            suggestedUninstallCommand: uninstall,
            detectionRule: detection.Rule,
            detectionGuidance: detection.Guidance,
            isDeterministicDetection: detection.IsDeterministic,
            confidenceScore: entry.ConfidenceScore,
            publishedAtUtc: entry.PublishedAtUtc);

        return entry with
        {
            SourceDisplayName = BuildNuGetDisplayName(source.Name),
            SourceChannel = source.Name,
            InstallerDownloadUrl = packageUrl,
            InstallerType = installerType,
            InstallerTypeRaw = Path.GetExtension(packageUrl),
            SuggestedInstallCommand = template.InstallCommand,
            SuggestedUninstallCommand = uninstall,
            DetectionGuidance = detection.Guidance,
            MetadataNotes = $"NuGet source: {source.Name}",
            InstallerVariants = [variant]
        };
    }

    private async Task<PackageCatalogDownloadResult> DownloadNuGetInstallerAsync(
        PackageCatalogEntry entry,
        string workingFolder,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var details = entry;
        if (string.IsNullOrWhiteSpace(details.InstallerDownloadUrl))
        {
            details = await GetNuGetDetailsAsync(entry, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(details.InstallerDownloadUrl))
        {
            return new PackageCatalogDownloadResult
            {
                Success = false,
                Message = "No package URL was available for this NuGet package.",
                WorkingFolderPath = workingFolder
            };
        }

        var fileName = BuildDownloadFileNameFromUrl(details.InstallerDownloadUrl, details.PackageId, details.Version);
        if (!fileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            fileName = $"{Path.GetFileNameWithoutExtension(fileName)}.nupkg";
        }

        var targetPath = Path.Combine(workingFolder, fileName);
        await DownloadFileAsync(details.InstallerDownloadUrl, targetPath, progress, cancellationToken);
        var resolvedInstaller = await ResolveDownloadedInstallerPathAsync(targetPath, workingFolder, progress, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedInstaller))
        {
            return new PackageCatalogDownloadResult
            {
                Success = false,
                Message = "NuGet package downloaded, but no supported installer artifact was found inside.",
                WorkingFolderPath = workingFolder
            };
        }

        return BuildSuccessfulDownloadResult(
            resolvedInstaller,
            Path.GetDirectoryName(resolvedInstaller) ?? workingFolder,
            "Installer extracted from NuGet package.",
            hashVerifiedBySource: false);
    }

    private async Task<IReadOnlyList<PackageCatalogEntry>> SearchGitHubReleasesAsync(string term, int limit, CancellationToken cancellationToken)
    {
        var query = $"{term} in:name,description,readme archived:false";
        var url =
            $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(query)}&sort=stars&order=desc&per_page={Math.Clamp(limit * 2, 8, 30)}";

        using var request = CreateGitHubRequest(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var payload = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(payload, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var entries = new List<PackageCatalogEntry>();
        foreach (var repo in items.EnumerateArray())
        {
            if (entries.Count >= limit)
            {
                break;
            }

            var owner = repo.TryGetProperty("owner", out var ownerElement)
                ? GetJsonStringOrDefault(ownerElement, "login")
                : string.Empty;
            var repoName = GetJsonStringOrDefault(repo, "name");
            var fullName = GetJsonStringOrDefault(repo, "full_name");
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repoName) || string.IsNullOrWhiteSpace(fullName))
            {
                continue;
            }

            GitHubReleaseInfo? release;
            try
            {
                release = await TryGetLatestGitHubReleaseAsync(owner, repoName, cancellationToken);
            }
            catch
            {
                continue;
            }

            if (release is null)
            {
                continue;
            }

            var normalizedVersion = NormalizeGitHubTagVersion(release.TagName);
            var variants = BuildGitHubInstallerVariants(
                sourceDisplayName: "GitHub Releases",
                sourceChannel: fullName,
                packageId: fullName,
                packageName: Coalesce(GetJsonStringOrDefault(repo, "name"), fullName),
                publisher: owner,
                version: normalizedVersion,
                release);
            if (variants.Count == 0)
            {
                continue;
            }

            var preferredVariant = variants
                .OrderByDescending(candidate => candidate.ConfidenceScore)
                .ThenBy(candidate => InstallerPriority(candidate.InstallerDownloadUrl))
                .First();
            entries.Add(new PackageCatalogEntry
            {
                Source = PackageCatalogSource.GitHubReleases,
                SourceDisplayName = "GitHub Releases",
                SourceChannel = fullName,
                PackageId = fullName,
                Name = Coalesce(GetJsonStringOrDefault(repo, "name"), fullName),
                Version = normalizedVersion,
                BuildVersion = normalizedVersion,
                Publisher = owner,
                Description = Truncate(Coalesce(GetJsonStringOrDefault(repo, "description"), release.ReleaseName), 260),
                HomepageUrl = Coalesce(GetJsonStringOrDefault(repo, "html_url"), $"https://github.com/{fullName}"),
                IconUrl = Coalesce(repo.TryGetProperty("owner", out var ownerObject)
                    ? GetJsonStringOrDefault(ownerObject, "avatar_url")
                    : string.Empty),
                InstallerDownloadUrl = preferredVariant.InstallerDownloadUrl,
                InstallerType = preferredVariant.InstallerType,
                InstallerTypeRaw = preferredVariant.InstallerTypeRaw,
                SuggestedInstallCommand = preferredVariant.SuggestedInstallCommand,
                SuggestedUninstallCommand = preferredVariant.SuggestedUninstallCommand,
                DetectionGuidance = preferredVariant.DetectionGuidance,
                MetadataNotes = $"GitHub release assets: {variants.Count} installer candidate(s).",
                ConfidenceScore = Math.Max(52, preferredVariant.ConfidenceScore),
                HasDetailedMetadata = true,
                PublishedAtUtc = release.PublishedAtUtc,
                InstallerVariants = variants
            });
        }

        return entries;
    }

    private async Task<PackageCatalogEntry> GetGitHubReleaseDetailsAsync(PackageCatalogEntry entry, CancellationToken cancellationToken)
    {
        if (!TryParseGitHubOwnerRepo(entry, out var owner, out var repo))
        {
            return entry;
        }

        GitHubReleaseInfo? release;
        try
        {
            release = await TryGetLatestGitHubReleaseAsync(owner, repo, cancellationToken);
        }
        catch
        {
            return entry;
        }

        if (release is null)
        {
            return entry;
        }

        var version = NormalizeGitHubTagVersion(release.TagName);
        var packageName = Coalesce(entry.Name, repo);
        var publisher = Coalesce(entry.Publisher, owner);
        var variants = BuildGitHubInstallerVariants(
            sourceDisplayName: "GitHub Releases",
            sourceChannel: $"{owner}/{repo}",
            packageId: entry.PackageId,
            packageName,
            publisher,
            version,
            release);
        if (variants.Count == 0)
        {
            return entry;
        }

        var preferredVariant = variants
            .OrderByDescending(candidate => candidate.ConfidenceScore)
            .ThenBy(candidate => InstallerPriority(candidate.InstallerDownloadUrl))
            .First();

        return entry with
        {
            SourceDisplayName = "GitHub Releases",
            SourceChannel = $"{owner}/{repo}",
            Version = version,
            BuildVersion = version,
            Description = Truncate(Coalesce(entry.Description, release.ReleaseName), 260),
            InstallerDownloadUrl = preferredVariant.InstallerDownloadUrl,
            InstallerType = preferredVariant.InstallerType,
            InstallerTypeRaw = preferredVariant.InstallerTypeRaw,
            SuggestedInstallCommand = preferredVariant.SuggestedInstallCommand,
            SuggestedUninstallCommand = preferredVariant.SuggestedUninstallCommand,
            DetectionGuidance = preferredVariant.DetectionGuidance,
            MetadataNotes = $"Latest GitHub release assets: {variants.Count} installer candidate(s).",
            ConfidenceScore = Math.Max(entry.ConfidenceScore, Math.Max(56, preferredVariant.ConfidenceScore)),
            HasDetailedMetadata = true,
            PublishedAtUtc = release.PublishedAtUtc,
            InstallerVariants = variants
        };
    }

    private async Task<PackageCatalogDownloadResult> DownloadGitHubReleaseInstallerAsync(
        PackageCatalogEntry entry,
        string workingFolder,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var details = entry;
        if (string.IsNullOrWhiteSpace(details.InstallerDownloadUrl))
        {
            details = await GetGitHubReleaseDetailsAsync(entry, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(details.InstallerDownloadUrl))
        {
            return new PackageCatalogDownloadResult
            {
                Success = false,
                Message = "No downloadable GitHub release asset was available for this package.",
                WorkingFolderPath = workingFolder
            };
        }

        var fileName = BuildDownloadFileNameFromUrl(details.InstallerDownloadUrl, entry.PackageId, details.Version);
        var targetPath = Path.Combine(workingFolder, fileName);

        await DownloadFileAsync(details.InstallerDownloadUrl, targetPath, progress, cancellationToken);
        var resolvedInstaller = await ResolveDownloadedInstallerPathAsync(targetPath, workingFolder, progress, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedInstaller))
        {
            return new PackageCatalogDownloadResult
            {
                Success = false,
                Message = "Downloaded GitHub release asset did not contain a supported installer file.",
                WorkingFolderPath = workingFolder
            };
        }

        return BuildSuccessfulDownloadResult(
            resolvedInstaller,
            Path.GetDirectoryName(resolvedInstaller) ?? workingFolder,
            "Downloaded installer from GitHub release asset.",
            hashVerifiedBySource: false);
    }

    private async Task<PackageCatalogDownloadResult> DownloadWingetInstallerAsync(
        PackageCatalogEntry entry,
        string workingFolder,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var sourceName = string.IsNullOrWhiteSpace(entry.SourceChannel)
            ? "winget"
            : entry.SourceChannel;
        var downloadArguments =
            $"download --id {QuoteArgument(entry.PackageId)} --exact --source {QuoteArgument(sourceName)} --accept-source-agreements --download-directory {QuoteArgument(workingFolder)} --disable-interactivity";
        var (downloadExitCode, downloadLines) = await RunProcessCaptureAsync("winget", downloadArguments, cancellationToken);
        foreach (var line in downloadLines.TakeLast(8))
        {
            progress?.Report(line);
        }

        var hashVerifiedByWinget = downloadLines.Any(line =>
            line.Contains("verified installer hash", StringComparison.OrdinalIgnoreCase));
        var hashMismatchDetected = downloadLines.Any(line =>
            line.Contains("hash mismatch", StringComparison.OrdinalIgnoreCase) ||
            (line.Contains("hash", StringComparison.OrdinalIgnoreCase) &&
             line.Contains("match", StringComparison.OrdinalIgnoreCase) &&
             line.Contains("not", StringComparison.OrdinalIgnoreCase)));
        var installerUrlFromDownloadOutput = TryExtractInstallerUrlFromWingetLines(downloadLines);

        if (downloadExitCode == 0)
        {
            var installerFromDownload = FindInstallerInFolder(workingFolder, entry);
            if (!string.IsNullOrWhiteSpace(installerFromDownload))
            {
                return BuildSuccessfulDownloadResult(
                    installerFromDownload,
                    Path.GetDirectoryName(installerFromDownload) ?? workingFolder,
                    "Downloaded installer using WinGet.",
                    hashVerifiedByWinget);
            }
        }

        if (!string.IsNullOrWhiteSpace(installerUrlFromDownloadOutput))
        {
            progress?.Report("WinGet output included an installer URL. Attempting direct fallback download...");
            try
            {
                var directUrlFileName = BuildDownloadFileNameFromUrl(installerUrlFromDownloadOutput, entry.PackageId, entry.Version);
                var fallbackTargetPath = Path.Combine(workingFolder, directUrlFileName);
                await DownloadFileAsync(installerUrlFromDownloadOutput, fallbackTargetPath, progress, cancellationToken);

                var installerFromUrlFallback = await ResolveDownloadedInstallerPathAsync(fallbackTargetPath, workingFolder, progress, cancellationToken);
                if (!string.IsNullOrWhiteSpace(installerFromUrlFallback))
                {
                    var expectedSha = NormalizeSha256(entry.InstallerSha256);
                    if (!string.IsNullOrWhiteSpace(expectedSha) && !FileHashMatches(installerFromUrlFallback, expectedSha))
                    {
                        return new PackageCatalogDownloadResult
                        {
                            Success = false,
                            Message = "Downloaded installer hash did not match the WinGet manifest SHA256.",
                            WorkingFolderPath = workingFolder
                        };
                    }

                    var fallbackMessage = hashMismatchDetected
                        ? "WinGet reported a hash mismatch. Downloaded installer from WinGet output URL, but source hash was not verified for this build."
                        : "Downloaded installer from WinGet output URL. Source hash was not verified by WinGet for this build.";
                    return BuildSuccessfulDownloadResult(
                        installerFromUrlFallback,
                        Path.GetDirectoryName(installerFromUrlFallback) ?? workingFolder,
                        fallbackMessage,
                        hashVerifiedBySource: !string.IsNullOrWhiteSpace(expectedSha));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                progress?.Report($"Direct URL fallback failed: {ex.Message}");
            }
        }

        progress?.Report("WinGet download command did not produce a direct installer. Trying installer URL metadata...");

        var details = await GetWingetDetailsAsync(entry, cancellationToken);
        var installerUrl = details.InstallerDownloadUrl;
        if (string.IsNullOrWhiteSpace(installerUrl))
        {
            var failureMessage = "WinGet download failed and no installer URL metadata was available for this package.";
            if (hashMismatchDetected)
            {
                failureMessage += " WinGet also reported a hash mismatch for the installer.";
            }

            return new PackageCatalogDownloadResult
            {
                Success = false,
                Message = failureMessage,
                WorkingFolderPath = workingFolder
            };
        }

        var fallbackFileName = BuildDownloadFileNameFromUrl(installerUrl, entry.PackageId, entry.Version);
        var targetPath = Path.Combine(workingFolder, fallbackFileName);
        await DownloadFileAsync(installerUrl, targetPath, progress, cancellationToken);

        var resolvedInstaller = await ResolveDownloadedInstallerPathAsync(targetPath, workingFolder, progress, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedInstaller))
        {
            var expectedSha = NormalizeSha256(details.InstallerSha256);
            if (!string.IsNullOrWhiteSpace(expectedSha) && !FileHashMatches(resolvedInstaller, expectedSha))
            {
                return new PackageCatalogDownloadResult
                {
                    Success = false,
                    Message = "Downloaded installer hash did not match the WinGet manifest SHA256.",
                    WorkingFolderPath = workingFolder
                };
            }

            return BuildSuccessfulDownloadResult(
                resolvedInstaller,
                Path.GetDirectoryName(resolvedInstaller) ?? workingFolder,
                "Downloaded installer from WinGet metadata URL.",
                hashVerifiedByWinget || !string.IsNullOrWhiteSpace(expectedSha));
        }

        return new PackageCatalogDownloadResult
        {
            Success = false,
            Message = "Downloaded artifact did not contain a supported installer file.",
            WorkingFolderPath = workingFolder
        };
    }

    private static string TryExtractInstallerUrlFromWingetLines(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = UrlRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var url = match.Value.Trim();
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        return string.Empty;
    }

    private async Task<PackageCatalogDownloadResult> DownloadChocolateyInstallerAsync(
        PackageCatalogEntry entry,
        string workingFolder,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var nupkgUrl = entry.InstallerDownloadUrl;
        if (string.IsNullOrWhiteSpace(nupkgUrl))
        {
            var source = await ResolveChocolateySourceAsync(entry.SourceChannel, cancellationToken);
            nupkgUrl = BuildChocolateyPackageUrl(entry.PackageId, entry.Version, source.ApiUrl);
        }

        if (string.IsNullOrWhiteSpace(nupkgUrl))
        {
            return new PackageCatalogDownloadResult
            {
                Success = false,
                Message = "Chocolatey package URL could not be resolved.",
                WorkingFolderPath = workingFolder
            };
        }

        var nupkgFileName = BuildDownloadFileNameFromUrl(nupkgUrl, entry.PackageId, entry.Version);
        if (!nupkgFileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            nupkgFileName = $"{Path.GetFileNameWithoutExtension(nupkgFileName)}.nupkg";
        }

        var nupkgPath = Path.Combine(workingFolder, nupkgFileName);
        await DownloadFileAsync(nupkgUrl, nupkgPath, progress, cancellationToken);

        var extractedFolder = Path.Combine(workingFolder, "expanded");
        if (Directory.Exists(extractedFolder))
        {
            Directory.Delete(extractedFolder, recursive: true);
        }

        ZipFile.ExtractToDirectory(nupkgPath, extractedFolder);
        var installerFromPackage = FindInstallerInFolder(extractedFolder);
        if (!string.IsNullOrWhiteSpace(installerFromPackage))
        {
            return BuildSuccessfulDownloadResult(
                installerFromPackage,
                Path.GetDirectoryName(installerFromPackage) ?? workingFolder,
                "Installer extracted from Chocolatey package.",
                hashVerifiedBySource: false);
        }

        var installerFromScript = await TryDownloadInstallerFromChocolateyScriptAsync(extractedFolder, workingFolder, progress, cancellationToken);
        if (!string.IsNullOrWhiteSpace(installerFromScript))
        {
            return BuildSuccessfulDownloadResult(
                installerFromScript,
                Path.GetDirectoryName(installerFromScript) ?? workingFolder,
                "Installer downloaded from Chocolatey install script metadata.",
                hashVerifiedBySource: false);
        }

        return new PackageCatalogDownloadResult
        {
            Success = false,
            Message = "Chocolatey package does not contain a downloadable installer payload. Open homepage or vendor download page.",
            WorkingFolderPath = workingFolder
        };
    }

    private async Task<string> TryDownloadInstallerFromChocolateyScriptAsync(
        string extractedFolder,
        string workingFolder,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var scriptPath = Directory
            .EnumerateFiles(extractedFolder, "*.ps1", SearchOption.AllDirectories)
            .FirstOrDefault(path =>
                Path.GetFileName(path).Equals("chocolateyInstall.ps1", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals("chocolateyinstall.ps1", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            return string.Empty;
        }

        var script = await File.ReadAllTextAsync(scriptPath, cancellationToken);
        var urls = UrlRegex.Matches(script)
            .Select(match => match.Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(value => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var url in urls)
        {
            var fileName = BuildDownloadFileNameFromUrl(url, "installer", null);
            var targetPath = Path.Combine(workingFolder, fileName);

            try
            {
                await DownloadFileAsync(url, targetPath, progress, cancellationToken);
                var resolvedInstaller = await ResolveDownloadedInstallerPathAsync(targetPath, workingFolder, progress, cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolvedInstaller))
                {
                    return resolvedInstaller;
                }
            }
            catch
            {
                // Continue with the next URL candidate.
            }
        }

        return string.Empty;
    }

    private async Task DownloadFileAsync(
        string url,
        string targetPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"Downloading {Path.GetFileName(targetPath)}...");

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private Task<string> ResolveDownloadedInstallerPathAsync(
        string targetPath,
        string workingFolder,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsSupportedInstallerFile(targetPath))
        {
            return Task.FromResult(targetPath);
        }

        if (targetPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            targetPath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            var archiveFolder = Path.Combine(
                workingFolder,
                $"{Path.GetFileNameWithoutExtension(targetPath)}-expanded");

            if (Directory.Exists(archiveFolder))
            {
                Directory.Delete(archiveFolder, recursive: true);
            }

            ZipFile.ExtractToDirectory(targetPath, archiveFolder);
            var installerFromArchive = FindInstallerInFolder(archiveFolder);
            if (!string.IsNullOrWhiteSpace(installerFromArchive))
            {
                progress?.Report($"Installer extracted from archive: {Path.GetFileName(installerFromArchive)}");
                return Task.FromResult(installerFromArchive);
            }
        }

        var installerFromFolder = FindInstallerInFolder(workingFolder);
        if (!string.IsNullOrWhiteSpace(installerFromFolder))
        {
            return Task.FromResult(installerFromFolder);
        }

        return Task.FromResult(string.Empty);
    }

    private static string FindInstallerInFolder(string folderPath)
        => FindInstallerInFolder(folderPath, null);

    private static string FindInstallerInFolder(string folderPath, PackageCatalogEntry? entry)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return string.Empty;
        }

        var candidates = Directory
            .EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(IsSupportedInstallerFile)
            .OrderBy(path => InstallerSelectionScore(path, entry))
            .ThenBy(path => InstallerPriority(path))
            .ThenByDescending(path => new FileInfo(path).Length)
            .ToList();

        return candidates.FirstOrDefault() ?? string.Empty;
    }

    private static int InstallerSelectionScore(string path, PackageCatalogEntry? entry)
    {
        var score = InstallerPriority(path) * 10;
        var normalizedPath = path.Replace('/', '\\');
        if (normalizedPath.Contains("\\Dependencies\\", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (entry is null)
        {
            return score;
        }

        var fileName = Path.GetFileNameWithoutExtension(path);
        var hints = BuildInstallerNameHints(entry);
        if (hints.Count == 0)
        {
            return score;
        }

        var normalizedFileName = NormalizeInstallerHint(fileName);
        var matched = hints.Any(hint =>
            normalizedFileName.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
            hint.Contains(normalizedFileName, StringComparison.OrdinalIgnoreCase));

        return matched ? score - 40 : score + 25;
    }

    private static IReadOnlyList<string> BuildInstallerNameHints(PackageCatalogEntry entry)
    {
        var values = new List<string>();
        AddInstallerNameHints(values, entry.Name);
        AddInstallerNameHints(values, entry.PackageId);
        AddInstallerNameHints(values, entry.CanonicalProductName);
        return values
            .Select(NormalizeInstallerHint)
            .Where(value => value.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddInstallerNameHints(List<string> values, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        values.Add(value);
        foreach (var part in value.Split(['.', '-', '_', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            values.Add(part);
        }
    }

    private static string NormalizeInstallerHint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static int InstallerPriority(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".msi" => 0,
            ".msix" or ".msixbundle" or ".appx" or ".appxbundle" => 1,
            ".exe" => 2,
            _ => 3
        };
    }

    private static bool IsSupportedInstallerFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return SupportedInstallerExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildChocolateyPackageUrl(string packageId, string version, string sourceApiUrl = "")
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return string.Empty;
        }

        var apiBase = NormalizeChocolateyApiBaseUrl(sourceApiUrl);
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            apiBase = "https://community.chocolatey.org/api/v2";
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return $"{apiBase.TrimEnd('/')}/package/{Uri.EscapeDataString(packageId)}";
        }

        return $"{apiBase.TrimEnd('/')}/package/{Uri.EscapeDataString(packageId)}/{Uri.EscapeDataString(version)}";
    }

    private static string BuildDownloadFileNameFromUrl(string url, string packageId, string? version)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var candidate = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        var safePackageId = SanitizePathSegment(packageId);
        var safeVersion = SanitizePathSegment(string.IsNullOrWhiteSpace(version) ? "latest" : version);
        return $"{safePackageId}-{safeVersion}.bin";
    }

    private async Task<(int ExitCode, IReadOnlyList<string> Lines)> RunProcessCaptureAsync(string executable, string arguments, CancellationToken cancellationToken)
    {
        var lines = new ConcurrentQueue<string>();
        var progress = new SynchronousProgress<ProcessOutputLine>(line =>
        {
            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                lines.Enqueue(line.Text.TrimEnd());
            }
        });

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProcessCaptureTimeout);

            var result = await _processRunner.RunAsync(new ProcessRunRequest
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = Environment.CurrentDirectory,
                PreferLowImpact = true
            }, progress, timeoutCts.Token);

            if (result.TimedOut)
            {
                lines.Enqueue(
                    $"{executable} command timed out after {ProcessCaptureTimeout.TotalSeconds:0} seconds.");
                return (-1, lines.ToArray());
            }

            return (result.ExitCode, lines.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            lines.Enqueue(
                $"{executable} command timed out after {ProcessCaptureTimeout.TotalSeconds:0} seconds.");
            return (-1, lines.ToArray());
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(ex.Message))
            {
                lines.Enqueue(ex.Message.Trim());
            }

            return (-1, lines.ToArray());
        }
    }

    private async Task<IReadOnlyList<WingetSourceInfo>> GetWingetSearchSourcesAsync(CancellationToken cancellationToken)
    {
        var (exitCode, lines) = await RunProcessCaptureAsync(
            "winget",
            "source list --disable-interactivity",
            cancellationToken);
        if (exitCode != 0 || lines.Count == 0)
        {
            return [new WingetSourceInfo("winget", false)];
        }

        var parsed = ParseWingetSourceRows(lines)
            .Where(source => !source.IsExplicit)
            .Where(source => !string.IsNullOrWhiteSpace(source.Name))
            .GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (!parsed.Any(source => source.Name.Equals("winget", StringComparison.OrdinalIgnoreCase)))
        {
            parsed.Insert(0, new WingetSourceInfo("winget", false));
        }

        return parsed;
    }

    private static IReadOnlyList<(string Name, string Id, string Version, string Match)> ParseWingetRows(IReadOnlyList<string> lines)
    {
        var rows = new List<(string Name, string Id, string Version, string Match)>();
        var inTable = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!inTable && line.Contains("Name", StringComparison.OrdinalIgnoreCase) && line.Contains("Id", StringComparison.OrdinalIgnoreCase))
            {
                inTable = true;
                continue;
            }

            if (!inTable || line.All(character => character is '-' or ' '))
            {
                continue;
            }

            var cols = MultiSpaceRegex.Split(line).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (cols.Length >= 3)
            {
                rows.Add((cols[0].Trim(), cols[1].Trim(), cols[2].Trim(), cols.Length >= 4 ? cols[3].Trim() : string.Empty));
            }
        }

        return rows;
    }

    private static IReadOnlyList<WingetSourceInfo> ParseWingetSourceRows(IReadOnlyList<string> lines)
    {
        var rows = new List<WingetSourceInfo>();
        var inTable = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!inTable &&
                line.Contains("Name", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("Argument", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("Explicit", StringComparison.OrdinalIgnoreCase))
            {
                inTable = true;
                continue;
            }

            if (!inTable || line.All(character => character is '-' or ' '))
            {
                continue;
            }

            var cols = MultiSpaceRegex.Split(line).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (cols.Length < 3)
            {
                continue;
            }

            var name = cols[0].Trim();
            var explicitRaw = cols[^1].Trim();
            var isExplicit = explicitRaw.Equals("true", StringComparison.OrdinalIgnoreCase);
            rows.Add(new WingetSourceInfo(name, isExplicit));
        }

        return rows;
    }

    private async Task<GitHubReleaseInfo?> TryGetLatestGitHubReleaseAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            return null;
        }

        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/latest";
        using var request = CreateGitHubRequest(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var payload = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(payload, cancellationToken: cancellationToken);
        var root = json.RootElement;

        var tagName = GetJsonStringOrDefault(root, "tag_name");
        var releaseName = GetJsonStringOrDefault(root, "name");
        DateTimeOffset? publishedAtUtc = null;
        if (DateTimeOffset.TryParse(
                GetJsonStringOrDefault(root, "published_at"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var published))
        {
            publishedAtUtc = published.ToUniversalTime();
        }

        var assets = new List<GitHubAssetInfo>();
        if (root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetArray.EnumerateArray())
            {
                var fileName = GetJsonStringOrDefault(asset, "name");
                var urlValue = GetJsonStringOrDefault(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(urlValue))
                {
                    continue;
                }

                assets.Add(new GitHubAssetInfo(
                    fileName,
                    urlValue,
                    GetJsonStringOrDefault(asset, "content_type"),
                    GetJsonInt32OrDefault(asset, "size")));
            }
        }

        return new GitHubReleaseInfo(tagName, releaseName, publishedAtUtc, assets);
    }

    private static GitHubAssetInfo? SelectPreferredGitHubAsset(IEnumerable<GitHubAssetInfo> assets)
    {
        return assets
            .Where(asset => !string.IsNullOrWhiteSpace(asset.DownloadUrl))
            .Select(asset => new
            {
                Asset = asset,
                Extension = Path.GetExtension(asset.FileName),
                Priority = InstallerPriority(asset.FileName)
            })
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Extension) &&
                (IsSupportedInstallerFile(candidate.Asset.FileName) ||
                 candidate.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(candidate => candidate.Priority)
            .ThenByDescending(candidate => candidate.Asset.FileSize)
            .Select(candidate => candidate.Asset)
            .FirstOrDefault();
    }

    private static bool TryParseGitHubOwnerRepo(PackageCatalogEntry entry, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        var candidates = new[]
        {
            entry.PackageId,
            entry.SourceChannel
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var match = GitHubRepoIdRegex.Match(candidate.Trim());
            if (!match.Success)
            {
                continue;
            }

            owner = match.Groups["owner"].Value;
            repo = match.Groups["repo"].Value;
            return true;
        }

        return false;
    }

    private HttpRequestMessage CreateGitHubRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        return request;
    }

    private static string NormalizeGitHubTagVersion(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return string.Empty;
        }

        var normalized = tagName.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        return normalized;
    }

    private static PackageCatalogEntry NormalizeCatalogEntry(PackageCatalogEntry entry)
    {
        var packageId = Coalesce(entry.PackageId, entry.SourceChannel, entry.Name);
        var name = Coalesce(entry.Name, packageId);
        var publisher = Coalesce(entry.Publisher, DerivePublisherFromPackageId(packageId));
        var version = Coalesce(entry.Version, entry.BuildVersion);
        var buildVersion = Coalesce(entry.BuildVersion, version);
        var releaseChannel = InferReleaseChannel(entry.ReleaseChannel, name, packageId, version, buildVersion);
        var canonicalPublisher = NormalizeIdentityComponent(Coalesce(entry.CanonicalPublisher, publisher));
        if (string.IsNullOrWhiteSpace(canonicalPublisher))
        {
            canonicalPublisher = NormalizeIdentityComponent(DerivePublisherFromPackageId(packageId));
        }

        var canonicalProduct = NormalizeIdentityComponent(Coalesce(entry.CanonicalProductName, DeriveCanonicalProduct(name, packageId)));
        if (string.IsNullOrWhiteSpace(canonicalProduct))
        {
            canonicalProduct = "package";
        }

        var canonicalKey = Coalesce(entry.CanonicalPackageKey, BuildCanonicalPackageKey(canonicalPublisher, canonicalProduct, releaseChannel));
        var variants = NormalizeInstallerVariants(entry, packageId, name, publisher, version, buildVersion);
        var preferredVariant = variants
            .OrderByDescending(VariantPreferenceScore)
            .FirstOrDefault();

        return entry with
        {
            CanonicalPackageKey = canonicalKey,
            CanonicalPublisher = canonicalPublisher,
            CanonicalProductName = canonicalProduct,
            ReleaseChannel = releaseChannel,
            PackageId = packageId,
            Name = name,
            Publisher = publisher,
            Version = version,
            BuildVersion = buildVersion,
            InstallerType = preferredVariant?.InstallerType ?? entry.InstallerType,
            InstallerTypeRaw = Coalesce(preferredVariant?.InstallerTypeRaw, entry.InstallerTypeRaw),
            InstallerDownloadUrl = Coalesce(preferredVariant?.InstallerDownloadUrl, entry.InstallerDownloadUrl),
            SuggestedInstallCommand = Coalesce(preferredVariant?.SuggestedInstallCommand, entry.SuggestedInstallCommand),
            SuggestedUninstallCommand = Coalesce(preferredVariant?.SuggestedUninstallCommand, entry.SuggestedUninstallCommand),
            DetectionGuidance = Coalesce(preferredVariant?.DetectionGuidance, entry.DetectionGuidance),
            ConfidenceScore = Math.Max(entry.ConfidenceScore, preferredVariant?.ConfidenceScore ?? 0),
            InstallerVariants = variants
        };
    }

    private static IReadOnlyList<PackageCatalogEntry> MergeCanonicalEntries(IEnumerable<PackageCatalogEntry> entries)
    {
        var normalized = entries
            .Where(entry => entry is not null)
            .Select(NormalizeCatalogEntry)
            .ToList();
        if (normalized.Count == 0)
        {
            return [];
        }

        return normalized
            .GroupBy(entry => entry.CanonicalPackageKey, StringComparer.OrdinalIgnoreCase)
            .Select(MergeCanonicalGroup)
            .ToList();
    }

    private static PackageCatalogEntry MergeCanonicalGroup(IGrouping<string, PackageCatalogEntry> group)
    {
        var entries = group.ToList();
        var preferredEntry = entries
            .OrderByDescending(EntryQualityScore)
            .First();
        var mergedVariants = entries
            .SelectMany(entry => entry.InstallerVariants)
            .GroupBy(variant => Coalesce(variant.VariantKey, BuildVariantKey(
                variant.Source,
                variant.SourceChannel,
                variant.PackageId,
                variant.Version,
                variant.InstallerType,
                variant.Architecture,
                variant.Scope,
                variant.InstallerDownloadUrl)), StringComparer.OrdinalIgnoreCase)
            .Select(variantGroup => variantGroup
                .OrderByDescending(VariantPreferenceScore)
                .First())
            .OrderByDescending(VariantPreferenceScore)
            .ToList();

        var preferredVariant = mergedVariants.FirstOrDefault();
        var sourceVariantCount = mergedVariants
            .Select(variant => $"{variant.Source}:{variant.SourceChannel}:{variant.PackageId}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var confidenceBonus = sourceVariantCount > 1 ? 8 : 0;
        var mergedConfidence = Math.Min(
            100,
            Math.Max(preferredEntry.ConfidenceScore, preferredVariant?.ConfidenceScore ?? 0) + confidenceBonus);
        var mergedVersion = entries
            .Select(entry => entry.Version)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? preferredEntry.Version;
        var mergedBuildVersion = entries
            .Select(entry => entry.BuildVersion)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? preferredEntry.BuildVersion;
        var mergedPublishedAt = entries
            .Select(entry => entry.PublishedAtUtc)
            .Where(value => value.HasValue)
            .Max();

        var mergedNotes = CleanDisplayValue(string.Join(
            " ",
            entries.Select(entry => entry.MetadataNotes).Where(note => !string.IsNullOrWhiteSpace(note)).Distinct(StringComparer.OrdinalIgnoreCase)));
        if (string.IsNullOrWhiteSpace(mergedNotes))
        {
            mergedNotes = sourceVariantCount > 1
                ? $"Merged package from {sourceVariantCount} source variants."
                : preferredEntry.MetadataNotes;
        }
        else if (sourceVariantCount > 1)
        {
            mergedNotes += $" Merged package from {sourceVariantCount} source variants.";
        }

        return preferredEntry with
        {
            Source = preferredVariant?.Source ?? preferredEntry.Source,
            SourceDisplayName = Coalesce(preferredVariant?.SourceDisplayName, preferredEntry.SourceDisplayName),
            SourceChannel = Coalesce(preferredVariant?.SourceChannel, preferredEntry.SourceChannel),
            PackageId = Coalesce(preferredVariant?.PackageId, preferredEntry.PackageId),
            Version = mergedVersion,
            BuildVersion = mergedBuildVersion,
            InstallerType = preferredVariant?.InstallerType ?? preferredEntry.InstallerType,
            InstallerTypeRaw = Coalesce(preferredVariant?.InstallerTypeRaw, preferredEntry.InstallerTypeRaw),
            InstallerDownloadUrl = Coalesce(preferredVariant?.InstallerDownloadUrl, preferredEntry.InstallerDownloadUrl),
            SuggestedInstallCommand = Coalesce(preferredVariant?.SuggestedInstallCommand, preferredEntry.SuggestedInstallCommand),
            SuggestedUninstallCommand = Coalesce(preferredVariant?.SuggestedUninstallCommand, preferredEntry.SuggestedUninstallCommand),
            DetectionGuidance = Coalesce(preferredVariant?.DetectionGuidance, preferredEntry.DetectionGuidance),
            Description = entries
                .Select(entry => entry.Description)
                .OrderByDescending(value => value?.Length ?? 0)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? preferredEntry.Description,
            HomepageUrl = entries
                .Select(entry => entry.HomepageUrl)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? preferredEntry.HomepageUrl,
            IconUrl = entries
                .Select(entry => entry.IconUrl)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? preferredEntry.IconUrl,
            ConfidenceScore = mergedConfidence,
            MetadataNotes = mergedNotes,
            HasDetailedMetadata = entries.Any(entry => entry.HasDetailedMetadata),
            PublishedAtUtc = mergedPublishedAt,
            HashVerifiedBySource = entries.Any(entry => entry.HashVerifiedBySource) || mergedVariants.Any(variant => variant.HashVerifiedBySource),
            VendorSigned = entries.Any(entry => entry.VendorSigned) || mergedVariants.Any(variant => variant.VendorSigned),
            InstallerVariants = mergedVariants
        };
    }

    private static IReadOnlyList<CatalogInstallerVariant> NormalizeInstallerVariants(
        PackageCatalogEntry entry,
        string packageId,
        string packageName,
        string publisher,
        string version,
        string buildVersion)
    {
        if (entry.InstallerVariants.Count == 0)
        {
            var msiProductCode = ExtractGuidFromText(entry.SuggestedUninstallCommand);
            var appxIdentity = entry.InstallerType == InstallerType.AppxMsix
                ? InferAppxIdentity(packageId, packageName, entry.PackageId)
                : string.Empty;
            var detection = BuildDeterministicDetectionRule(
                entry.InstallerType,
                packageId,
                packageName,
                publisher,
                version,
                msiProductCode,
                appxIdentity,
                uninstallDisplayName: packageName,
                uninstallPublisher: publisher,
                uninstallDisplayVersion: version,
                appxPublisher: publisher);
            var variant = BuildInstallerVariant(
                entry.Source,
                entry.SourceDisplayName,
                entry.SourceChannel,
                packageId,
                version,
                buildVersion,
                entry.InstallerType,
                entry.InstallerTypeRaw,
                architecture: InferArchitecture(packageName, packageId, version, entry.InstallerTypeRaw),
                scope: InferScope(entry.SourceChannel, packageName, packageId),
                installerDownloadUrl: entry.InstallerDownloadUrl,
                installerSha256: entry.InstallerSha256,
                hashVerifiedBySource: entry.HashVerifiedBySource,
                vendorSigned: entry.VendorSigned,
                signerSubject: string.Empty,
                suggestedInstallCommand: entry.SuggestedInstallCommand,
                suggestedUninstallCommand: entry.SuggestedUninstallCommand,
                detectionRule: detection.Rule,
                detectionGuidance: Coalesce(entry.DetectionGuidance, detection.Guidance),
                isDeterministicDetection: detection.IsDeterministic,
                confidenceScore: entry.ConfidenceScore,
                publishedAtUtc: entry.PublishedAtUtc);
            return [variant];
        }

        return entry.InstallerVariants
            .Select(variant => NormalizeInstallerVariant(entry, variant, packageId, packageName, publisher, version, buildVersion))
            .GroupBy(variant => variant.VariantKey, StringComparer.OrdinalIgnoreCase)
            .Select(variantGroup => variantGroup
                .OrderByDescending(VariantPreferenceScore)
                .First())
            .OrderByDescending(VariantPreferenceScore)
            .ToList();
    }

    private static CatalogInstallerVariant NormalizeInstallerVariant(
        PackageCatalogEntry entry,
        CatalogInstallerVariant variant,
        string packageId,
        string packageName,
        string publisher,
        string version,
        string buildVersion)
    {
        var resolvedInstallerType = variant.InstallerType == InstallerType.Unknown
            ? entry.InstallerType
            : variant.InstallerType;
        var resolvedPackageId = Coalesce(variant.PackageId, packageId);
        var resolvedVersion = Coalesce(variant.Version, version);
        var resolvedBuildVersion = Coalesce(variant.BuildVersion, buildVersion, resolvedVersion);
        var resolvedArchitecture = Coalesce(variant.Architecture, InferArchitecture(packageName, resolvedPackageId, resolvedVersion, variant.InstallerTypeRaw));
        var resolvedScope = Coalesce(variant.Scope, InferScope(variant.SourceChannel, packageName, resolvedPackageId));
        var msiProductCode = ExtractGuidFromText(Coalesce(variant.SuggestedUninstallCommand, entry.SuggestedUninstallCommand));
        var appxIdentity = resolvedInstallerType == InstallerType.AppxMsix
            ? InferAppxIdentity(resolvedPackageId, packageName, variant.PackageId)
            : string.Empty;
        var detection = variant.DetectionRule.RuleType != IntuneDetectionRuleType.None
            ? new DetectionStrategyPlan(variant.DetectionRule, variant.DetectionGuidance, variant.IsDeterministicDetection)
            : BuildDeterministicDetectionRule(
                resolvedInstallerType,
                resolvedPackageId,
                packageName,
                publisher,
                resolvedVersion,
                msiProductCode,
                appxIdentity,
                uninstallDisplayName: packageName,
                uninstallPublisher: publisher,
                uninstallDisplayVersion: resolvedVersion,
                appxPublisher: publisher);

        var template = BuildTemplate(resolvedPackageId, resolvedInstallerType, variant.InstallerTypeRaw);
        var installCommand = Coalesce(variant.SuggestedInstallCommand, entry.SuggestedInstallCommand, template.InstallCommand);
        var uninstallCommand = Coalesce(
            variant.SuggestedUninstallCommand,
            entry.SuggestedUninstallCommand,
            ResolveUninstallTemplate(template.UninstallCommand, resolvedInstallerType, msiProductCode, appxIdentity));

        return BuildInstallerVariant(
            source: variant.Source,
            sourceDisplayName: Coalesce(variant.SourceDisplayName, entry.SourceDisplayName),
            sourceChannel: Coalesce(variant.SourceChannel, entry.SourceChannel),
            packageId: resolvedPackageId,
            version: resolvedVersion,
            buildVersion: resolvedBuildVersion,
            installerType: resolvedInstallerType,
            installerTypeRaw: Coalesce(variant.InstallerTypeRaw, entry.InstallerTypeRaw),
            architecture: resolvedArchitecture,
            scope: resolvedScope,
            installerDownloadUrl: Coalesce(variant.InstallerDownloadUrl, entry.InstallerDownloadUrl),
            installerSha256: Coalesce(NormalizeSha256(variant.InstallerSha256), NormalizeSha256(entry.InstallerSha256)),
            hashVerifiedBySource: variant.HashVerifiedBySource || entry.HashVerifiedBySource,
            vendorSigned: variant.VendorSigned || entry.VendorSigned,
            signerSubject: variant.SignerSubject,
            suggestedInstallCommand: installCommand,
            suggestedUninstallCommand: uninstallCommand,
            detectionRule: detection.Rule,
            detectionGuidance: Coalesce(variant.DetectionGuidance, entry.DetectionGuidance, detection.Guidance),
            isDeterministicDetection: variant.IsDeterministicDetection || detection.IsDeterministic,
            confidenceScore: Math.Max(variant.ConfidenceScore, entry.ConfidenceScore),
            publishedAtUtc: variant.PublishedAtUtc ?? entry.PublishedAtUtc,
            variantKey: variant.VariantKey);
    }

    private static CatalogInstallerVariant BuildInstallerVariant(
        PackageCatalogSource source,
        string sourceDisplayName,
        string sourceChannel,
        string packageId,
        string version,
        string buildVersion,
        InstallerType installerType,
        string installerTypeRaw,
        string architecture,
        string scope,
        string installerDownloadUrl,
        string installerSha256,
        bool hashVerifiedBySource,
        bool vendorSigned,
        string signerSubject,
        string suggestedInstallCommand,
        string suggestedUninstallCommand,
        IntuneDetectionRule detectionRule,
        string detectionGuidance,
        bool isDeterministicDetection,
        int confidenceScore,
        DateTimeOffset? publishedAtUtc,
        string variantKey = "")
    {
        var resolvedVersion = Coalesce(version, buildVersion);
        var resolvedBuildVersion = Coalesce(buildVersion, resolvedVersion);
        var resolvedVariantKey = string.IsNullOrWhiteSpace(variantKey)
            ? BuildVariantKey(source, sourceChannel, packageId, resolvedVersion, installerType, architecture, scope, installerDownloadUrl)
            : variantKey;

        return new CatalogInstallerVariant
        {
            VariantKey = resolvedVariantKey,
            Source = source,
            SourceDisplayName = sourceDisplayName,
            SourceChannel = sourceChannel,
            PackageId = packageId,
            Version = resolvedVersion,
            BuildVersion = resolvedBuildVersion,
            InstallerType = installerType,
            InstallerTypeRaw = installerTypeRaw,
            Architecture = architecture,
            Scope = scope,
            InstallerDownloadUrl = installerDownloadUrl,
            InstallerSha256 = NormalizeSha256(installerSha256),
            HashVerifiedBySource = hashVerifiedBySource,
            VendorSigned = vendorSigned,
            SignerSubject = signerSubject,
            SuggestedInstallCommand = suggestedInstallCommand,
            SuggestedUninstallCommand = suggestedUninstallCommand,
            DetectionRule = detectionRule,
            DetectionGuidance = detectionGuidance,
            IsDeterministicDetection = isDeterministicDetection,
            ConfidenceScore = confidenceScore,
            PublishedAtUtc = publishedAtUtc
        };
    }

    private static string BuildVariantKey(
        PackageCatalogSource source,
        string sourceChannel,
        string packageId,
        string version,
        InstallerType installerType,
        string architecture,
        string scope,
        string installerDownloadUrl)
    {
        var identity = string.Join("|",
            source,
            sourceChannel.Trim().ToLowerInvariant(),
            packageId.Trim().ToLowerInvariant(),
            NormalizeVersionSegment(version),
            installerType,
            architecture.Trim().ToLowerInvariant(),
            scope.Trim().ToLowerInvariant(),
            installerDownloadUrl.Trim().ToLowerInvariant());
        return ComputeStableHash(identity);
    }

    private static IReadOnlyList<CatalogInstallerVariant> BuildGitHubInstallerVariants(
        string sourceDisplayName,
        string sourceChannel,
        string packageId,
        string packageName,
        string publisher,
        string version,
        GitHubReleaseInfo release)
    {
        var variants = new List<CatalogInstallerVariant>();
        foreach (var asset in release.Assets)
        {
            if (!IsPotentialInstallerAsset(asset.FileName))
            {
                continue;
            }

            var extension = Path.GetExtension(asset.FileName);
            var installerType = InferInstallerType(extension, asset.ContentType);
            var template = BuildTemplate(packageId, installerType, asset.FileName);
            var architecture = InferArchitecture(asset.FileName, packageId, version, asset.ContentType);
            var scope = InferScope(sourceChannel, asset.FileName, packageId);
            var appxIdentity = installerType == InstallerType.AppxMsix
                ? InferAppxIdentity(packageId, packageName, asset.FileName)
                : string.Empty;
            var detection = BuildDeterministicDetectionRule(
                installerType,
                packageId,
                packageName,
                publisher,
                version,
                msiProductCode: string.Empty,
                appxIdentity,
                uninstallDisplayName: packageName,
                uninstallPublisher: publisher,
                uninstallDisplayVersion: version,
                appxPublisher: publisher);
            var variantConfidence = installerType == InstallerType.Unknown
                ? 44
                : Math.Max(56, template.ConfidenceScore - 12);

            variants.Add(BuildInstallerVariant(
                source: PackageCatalogSource.GitHubReleases,
                sourceDisplayName,
                sourceChannel,
                packageId,
                version,
                version,
                installerType,
                extension,
                architecture,
                scope,
                asset.DownloadUrl,
                installerSha256: string.Empty,
                hashVerifiedBySource: false,
                vendorSigned: false,
                signerSubject: string.Empty,
                suggestedInstallCommand: template.InstallCommand,
                suggestedUninstallCommand: ResolveUninstallTemplate(template.UninstallCommand, installerType, msiProductCode: string.Empty, appxIdentity),
                detectionRule: detection.Rule,
                detectionGuidance: detection.Guidance,
                isDeterministicDetection: detection.IsDeterministic,
                confidenceScore: variantConfidence,
                publishedAtUtc: release.PublishedAtUtc));
        }

        return variants
            .GroupBy(variant => variant.VariantKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(VariantPreferenceScore)
                .First())
            .OrderByDescending(VariantPreferenceScore)
            .ToList();
    }

    private static bool IsPotentialInstallerAsset(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (IsSupportedInstallerFile(fileName))
        {
            return true;
        }

        var extension = Path.GetExtension(fileName);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".nupkg", StringComparison.OrdinalIgnoreCase);
    }

    private static DetectionStrategyPlan BuildDeterministicDetectionRule(
        InstallerType installerType,
        string packageId,
        string packageName,
        string publisher,
        string version,
        string msiProductCode,
        string appxIdentity,
        string uninstallRegistryKeyPath = "",
        string uninstallDisplayName = "",
        string uninstallPublisher = "",
        string uninstallDisplayVersion = "",
        string fileDetectionPath = "",
        string fileDetectionName = "",
        string fileDetectionVersion = "",
        string appxPublisher = "")
    {
        if (installerType == InstallerType.Msi)
        {
            msiProductCode = NormalizeMsiProductCode(msiProductCode);
            if (!string.IsNullOrWhiteSpace(msiProductCode) && GuidCodeRegex.IsMatch(msiProductCode))
            {
                return new DetectionStrategyPlan(
                    new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.MsiProductCode,
                        Msi = new MsiDetectionRule
                        {
                            ProductCode = msiProductCode,
                            ProductVersion = version
                        }
                    },
                    "Deterministic: MSI Product Code detection.",
                    true);
            }

            return new DetectionStrategyPlan(
                new IntuneDetectionRule { RuleType = IntuneDetectionRuleType.None },
                "MSI detected, but Product Code is missing. Resolve Product Code before packaging.",
                false);
        }

        if (installerType == InstallerType.Exe)
        {
            var exactDisplayName = Coalesce(uninstallDisplayName, packageName);
            var exactPublisher = Coalesce(uninstallPublisher, publisher);
            var exactVersion = Coalesce(uninstallDisplayVersion, version);
            var stableFilePath = fileDetectionPath;
            var stableFileName = fileDetectionName;
            var stableFileVersion = Coalesce(fileDetectionVersion, version);

            if (!string.IsNullOrWhiteSpace(uninstallRegistryKeyPath) &&
                !string.IsNullOrWhiteSpace(exactDisplayName) &&
                !string.IsNullOrWhiteSpace(exactPublisher) &&
                !string.IsNullOrWhiteSpace(exactVersion))
            {
                return new DetectionStrategyPlan(
                    new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Registry,
                        Registry = new RegistryDetectionRule
                        {
                            Hive = "HKEY_LOCAL_MACHINE",
                            KeyPath = uninstallRegistryKeyPath,
                            ValueName = "DisplayVersion",
                            Operator = IntuneDetectionOperator.Equals,
                            Value = exactVersion
                        }
                    },
                    "Detection selection: MSI rejected (installer type EXE). Registry accepted using exact uninstall key metadata plus DisplayVersion equality. File/script detection were not needed.",
                    true);
            }

            if (!string.IsNullOrWhiteSpace(stableFilePath) &&
                !string.IsNullOrWhiteSpace(stableFileName) &&
                !string.IsNullOrWhiteSpace(stableFileVersion))
            {
                return new DetectionStrategyPlan(
                    new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.File,
                        File = new FileDetectionRule
                        {
                            Path = stableFilePath,
                            FileOrFolderName = stableFileName,
                            Check32BitOn64System = false,
                            Operator = IntuneDetectionOperator.Equals,
                            Value = stableFileVersion
                        }
                    },
                    "Detection selection: MSI rejected (installer type EXE). Registry rejected because exact uninstall key metadata is unavailable. File accepted using stable installed binary path + version metadata. Script detection was not needed.",
                    true);
            }

            if (!string.IsNullOrWhiteSpace(exactDisplayName) &&
                !string.IsNullOrWhiteSpace(exactPublisher) &&
                !string.IsNullOrWhiteSpace(exactVersion))
            {
                return new DetectionStrategyPlan(
                    new IntuneDetectionRule { RuleType = IntuneDetectionRuleType.None },
                    "Detection selection: MSI rejected (installer type EXE). Registry rejected because exact uninstall key metadata is unavailable. File rejected because no stable installed file path + version metadata was provided. Script fallback is available from exact DisplayName, Publisher, and DisplayVersion metadata, but was not applied automatically.",
                    false);
            }

            return new DetectionStrategyPlan(
                new IntuneDetectionRule { RuleType = IntuneDetectionRuleType.None },
                "Detection selection: MSI rejected (installer type EXE). Registry rejected because exact uninstall key metadata is unavailable. File rejected because no stable installed file path + version metadata was provided. Script rejected because strict DisplayName/Publisher/DisplayVersion metadata is incomplete.",
                false);
        }

        if (installerType == InstallerType.AppxMsix)
        {
            var identity = Coalesce(appxIdentity, packageId);
            if (!string.IsNullOrWhiteSpace(identity) && !string.IsNullOrWhiteSpace(version))
            {
                return new DetectionStrategyPlan(
                    new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Script,
                        Script = new ScriptDetectionRule
                        {
                            ScriptBody = BuildExactAppxDetectionScript(identity, version, appxPublisher),
                            RunAs32BitOn64System = false,
                            EnforceSignatureCheck = false
                        }
                    },
                    "Deterministic: exact APPX/MSIX identity and version.",
                    true);
            }

            return new DetectionStrategyPlan(
                new IntuneDetectionRule { RuleType = IntuneDetectionRuleType.None },
                "APPX/MSIX detected, but package identity/version is incomplete.",
                false);
        }

        return new DetectionStrategyPlan(
            new IntuneDetectionRule { RuleType = IntuneDetectionRuleType.None },
            "Installer type is unknown. Configure deterministic detection manually.",
            false);
    }

    private static string BuildExactRegistryDetectionScript(string displayName, string publisher, string version)
    {
        return DeterministicDetectionScript.BuildExactExeRegistryScript(displayName, publisher, version);
    }

    private static string BuildExactAppxDetectionScript(string appxIdentity, string version, string publisher)
    {
        return DeterministicDetectionScript.BuildExactAppxIdentityScript(appxIdentity, version, publisher);
    }

    private static int VariantPreferenceScore(CatalogInstallerVariant variant)
    {
        var score = variant.ConfidenceScore;
        if (variant.IsDeterministicDetection)
        {
            score += 14;
        }

        if (!string.IsNullOrWhiteSpace(variant.InstallerDownloadUrl))
        {
            score += 8;
        }

        if (variant.HashVerifiedBySource)
        {
            score += 8;
        }

        if (variant.VendorSigned)
        {
            score += 6;
        }

        score += variant.InstallerType switch
        {
            InstallerType.Msi => 16,
            InstallerType.AppxMsix => 12,
            InstallerType.Exe => 8,
            InstallerType.Script => 6,
            _ => 0
        };

        return score;
    }

    private static int EntryQualityScore(PackageCatalogEntry entry)
    {
        var score = entry.ConfidenceScore;
        if (entry.HasDetailedMetadata)
        {
            score += 10;
        }

        if (!string.IsNullOrWhiteSpace(entry.InstallerDownloadUrl))
        {
            score += 8;
        }

        score += entry.Source switch
        {
            PackageCatalogSource.Winget => 7,
            PackageCatalogSource.Chocolatey => 5,
            PackageCatalogSource.GitHubReleases => 4,
            _ => 0
        };

        if (entry.PublishedAtUtc.HasValue)
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(entry.Publisher))
        {
            score += 2;
        }

        return score;
    }

    private static string ResolveUninstallTemplate(string template, InstallerType installerType, string msiProductCode, string appxIdentity)
    {
        msiProductCode = NormalizeMsiProductCode(msiProductCode);
        if (installerType == InstallerType.Msi &&
            !string.IsNullOrWhiteSpace(msiProductCode) &&
            GuidCodeRegex.IsMatch(msiProductCode))
        {
            return $"msiexec /x {msiProductCode} /quiet /norestart";
        }

        if (installerType == InstallerType.AppxMsix && !string.IsNullOrWhiteSpace(appxIdentity))
        {
            var escapedIdentity = appxIdentity.Replace("'", "''", StringComparison.Ordinal);
            return $"powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Get-AppxPackage -Name '{escapedIdentity}' | Remove-AppxPackage\"";
        }

        return template;
    }

    private static string ExtractGuidFromText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var match = GuidInTextRegex.Match(value);
        return match.Success ? match.Value : string.Empty;
    }

    private static string NormalizeMsiProductCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        trimmed = trimmed.Trim('{', '}');
        if (!Guid.TryParse(trimmed, out var guid))
        {
            return string.Empty;
        }

        return $"{{{guid:D}}}".ToUpperInvariant();
    }

    private static bool TryParseFileDetectionFromDisplayIcon(string displayIcon, out string folderPath, out string fileName)
    {
        folderPath = string.Empty;
        fileName = string.Empty;

        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return false;
        }

        var candidate = displayIcon.Trim().Trim('"');
        var commaIndex = candidate.IndexOf(',');
        if (commaIndex > 0)
        {
            candidate = candidate[..commaIndex].Trim().Trim('"');
        }

        if (!candidate.Contains('\\') && !candidate.Contains('/'))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(candidate);
            var directory = Path.GetDirectoryName(fullPath);
            var name = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            folderPath = directory;
            fileName = name;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string InferArchitecture(string first, string second, string third, string fourth)
    {
        var text = $"{first} {second} {third} {fourth}".ToLowerInvariant();
        if (text.Contains("arm64", StringComparison.Ordinal))
        {
            return "arm64";
        }

        if (text.Contains("x64", StringComparison.Ordinal) || text.Contains("amd64", StringComparison.Ordinal) || text.Contains("win64", StringComparison.Ordinal))
        {
            return "x64";
        }

        if (text.Contains("x86", StringComparison.Ordinal) || text.Contains("32-bit", StringComparison.Ordinal) || text.Contains("win32", StringComparison.Ordinal))
        {
            return "x86";
        }

        if (text.Contains("arm", StringComparison.Ordinal))
        {
            return "arm";
        }

        return string.Empty;
    }

    private static string InferScope(string sourceChannel, string first, string second)
    {
        var text = $"{sourceChannel} {first} {second}".ToLowerInvariant();
        if (text.Contains("per-user", StringComparison.Ordinal) ||
            text.Contains("per user", StringComparison.Ordinal) ||
            text.Contains("user", StringComparison.Ordinal))
        {
            return "user";
        }

        if (text.Contains("machine", StringComparison.Ordinal) ||
            text.Contains("system", StringComparison.Ordinal) ||
            text.Contains("allusers", StringComparison.Ordinal))
        {
            return "machine";
        }

        if (sourceChannel.Equals("msstore", StringComparison.OrdinalIgnoreCase))
        {
            return "user";
        }

        return string.Empty;
    }

    private static string NormalizeSha256(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return string.Empty;
        }

        var normalized = hash.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        return Sha256Regex.IsMatch(normalized) ? normalized.ToLowerInvariant() : string.Empty;
    }

    private static string InferAppxIdentity(string packageId, string packageName, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(fallback) && fallback.Contains('.', StringComparison.Ordinal))
        {
            return fallback.Trim();
        }

        if (!string.IsNullOrWhiteSpace(packageId) && packageId.Contains('.', StringComparison.Ordinal))
        {
            return packageId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(packageName))
        {
            return packageName.Trim();
        }

        return packageId.Trim();
    }

    private static string BuildCanonicalPackageKey(string canonicalPublisher, string canonicalProduct, string releaseChannel)
    {
        var publisher = string.IsNullOrWhiteSpace(canonicalPublisher) ? "unknown" : canonicalPublisher;
        var product = string.IsNullOrWhiteSpace(canonicalProduct) ? "package" : canonicalProduct;
        var channel = NormalizeReleaseChannel(releaseChannel);
        return $"{publisher}|{product}|{channel}";
    }

    private static string DerivePublisherFromPackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return string.Empty;
        }

        var parts = packageId
            .Split(IdentitySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? string.Empty : parts[0];
    }

    private static string DeriveCanonicalProduct(string packageName, string packageId)
    {
        var candidate = Coalesce(packageName, packageId);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "package";
        }

        var normalized = NormalizeIdentityComponent(candidate);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "package";
        }

        var ignoredTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "setup",
            "installer",
            "install",
            "windows",
            "win32",
            "x86",
            "x64",
            "arm",
            "arm64",
            "stable",
            "beta",
            "preview",
            "dev",
            "canary",
            "nightly",
            "lts"
        };

        var tokens = normalized
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !ignoredTokens.Contains(token))
            .Take(4)
            .ToList();

        if (tokens.Count == 0)
        {
            return normalized;
        }

        return string.Join("-", tokens);
    }

    private static string InferReleaseChannel(string explicitChannel, string packageName, string packageId, string version, string buildVersion)
    {
        if (!string.IsNullOrWhiteSpace(explicitChannel))
        {
            return NormalizeReleaseChannel(explicitChannel);
        }

        var text = $"{packageName} {packageId} {version} {buildVersion}".ToLowerInvariant();
        if (text.Contains("canary", StringComparison.Ordinal))
        {
            return "canary";
        }

        if (text.Contains("nightly", StringComparison.Ordinal))
        {
            return "nightly";
        }

        if (text.Contains("preview", StringComparison.Ordinal) || text.Contains("insider", StringComparison.Ordinal))
        {
            return "preview";
        }

        if (text.Contains("beta", StringComparison.Ordinal))
        {
            return "beta";
        }

        if (text.Contains("dev", StringComparison.Ordinal))
        {
            return "dev";
        }

        if (text.Contains("lts", StringComparison.Ordinal))
        {
            return "lts";
        }

        return "stable";
    }

    private static string NormalizeReleaseChannel(string releaseChannel)
    {
        if (string.IsNullOrWhiteSpace(releaseChannel))
        {
            return "stable";
        }

        var normalized = releaseChannel.Trim().ToLowerInvariant();
        return normalized switch
        {
            "stable" => "stable",
            "beta" => "beta",
            "preview" => "preview",
            "canary" => "canary",
            "dev" => "dev",
            "nightly" => "nightly",
            "lts" => "lts",
            _ => "stable"
        };
    }

    private static string NormalizeIdentityComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSeparator = false;

        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(character);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string NormalizeVersionSegment(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "latest";
        }

        var normalized = version.Trim().ToLowerInvariant();
        var plusIndex = normalized.IndexOf('+');
        if (plusIndex > 0)
        {
            normalized = normalized[..plusIndex];
        }

        return normalized;
    }

    private static bool IsVersionEquivalent(string left, string right)
    {
        return NormalizeVersionSegment(left).Equals(NormalizeVersionSegment(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanDisplayValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var compact = MultiSpaceRegex.Replace(value, " ").Trim();
        return compact.Length <= 520 ? compact : compact[..520].TrimEnd();
    }

    private static TemplateSuggestion BuildTemplate(string packageId, InstallerType installerType, string? raw)
    {
        var normalized = raw?.ToLowerInvariant() ?? string.Empty;
        if (installerType == InstallerType.Msi)
        {
            return new("msiexec /i \"<installer-file>.msi\" /quiet /norestart", "msiexec /x \"{PRODUCT-CODE}\" /quiet /norestart", "Use MSI Product Code detection in Intune.", 90);
        }

        if (installerType == InstallerType.AppxMsix)
        {
            var identity = string.IsNullOrWhiteSpace(packageId) ? "<package-identity>" : packageId;
            return new(
                "powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Add-AppxPackage -Path \\\"<package-file>.msix\\\"\"",
                $"powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Get-AppxPackage -Name '{identity}' | Remove-AppxPackage\"",
                "Use script detection with Get-AppxPackage and validate identity on device.",
                84);
        }

        if (installerType == InstallerType.Exe && normalized.Contains("inno", StringComparison.Ordinal))
        {
            return new("\"<installer-file>.exe\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", "\"<installer-file>.exe\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", "Inno profile detected. Validate switches once.", 78);
        }

        if (installerType == InstallerType.Exe && (normalized.Contains("nsis", StringComparison.Ordinal) || normalized.Contains("nullsoft", StringComparison.Ordinal)))
        {
            return new("\"<installer-file>.exe\" /S", "\"<installer-file>.exe\" /S", "NSIS profile detected. Validate switches once.", 74);
        }

        if (installerType == InstallerType.Exe)
        {
            return new("\"<installer-file>.exe\" <silent-args>", "\"<installer-file>.exe\" <uninstall-args>", "Unknown EXE framework. Use vendor docs and validate silently before production.", 40);
        }

        return new("\"<installer-file>\" <install-args>", "\"<uninstall-command>\" <uninstall-args>", "Installer type unknown. Configure commands and detection manually.", 28);
    }

    private static InstallerType InferInstallerType(string? raw, string? fallback)
    {
        var value = $"{raw} {fallback}".ToLowerInvariant();
        if (value.Contains("msix", StringComparison.Ordinal) || value.Contains("appx", StringComparison.Ordinal)) return InstallerType.AppxMsix;
        if (value.Contains("msi", StringComparison.Ordinal) || value.Contains("wix", StringComparison.Ordinal)) return InstallerType.Msi;
        if (value.Contains("inno", StringComparison.Ordinal) || value.Contains("nsis", StringComparison.Ordinal) || value.Contains("nullsoft", StringComparison.Ordinal) || value.Contains("installshield", StringComparison.Ordinal) || value.Contains("burn", StringComparison.Ordinal) || value.Contains("exe", StringComparison.Ordinal)) return InstallerType.Exe;
        if (value.Contains("script", StringComparison.Ordinal) || value.Contains("powershell", StringComparison.Ordinal)) return InstallerType.Script;
        return InstallerType.Unknown;
    }

    private static int Relevance(PackageCatalogEntry entry, string term)
    {
        var t = term.ToLowerInvariant();
        var name = entry.Name.ToLowerInvariant();
        var id = entry.PackageId.ToLowerInvariant();
        var score = 0;
        if (name.StartsWith(t, StringComparison.Ordinal)) score += 90;
        if (name.Contains(t, StringComparison.Ordinal)) score += 60;
        if (id.StartsWith(t, StringComparison.Ordinal)) score += 85;
        if (id.Contains(t, StringComparison.Ordinal)) score += 55;
        if (entry.Source == PackageCatalogSource.Winget) score += 5;
        if (entry.Source == PackageCatalogSource.Chocolatey) score += 3;
        if (entry.Source == PackageCatalogSource.GitHubReleases) score += 2;
        return score;
    }

    private static string ResolveIconUrl(string? iconUrl, string? homepage, string? packageId = null)
    {
        if (!string.IsNullOrWhiteSpace(iconUrl) &&
            Uri.TryCreate(iconUrl, UriKind.Absolute, out var iconUri) &&
            !iconUri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return iconUrl!;
        }

        if (!string.IsNullOrWhiteSpace(homepage) &&
            Uri.TryCreate(homepage, UriKind.Absolute, out var homepageUri) &&
            !string.IsNullOrWhiteSpace(homepageUri.Host))
        {
            return BuildFaviconUrl(homepageUri.Host);
        }

        if (!string.IsNullOrWhiteSpace(iconUrl) &&
            Uri.TryCreate(iconUrl, UriKind.Absolute, out var svgUri) &&
            !string.IsNullOrWhiteSpace(svgUri.Host))
        {
            return BuildFaviconUrl(svgUri.Host);
        }

        var inferredHost = BuildLikelyHostFromPackageId(packageId);
        if (!string.IsNullOrWhiteSpace(inferredHost))
        {
            return BuildFaviconUrl(inferredHost);
        }

        return string.Empty;
    }

    private static string BuildFaviconUrl(string host)
    {
        return $"https://www.google.com/s2/favicons?sz=128&domain={Uri.EscapeDataString(host)}";
    }

    private static string BuildLikelyHostFromPackageId(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return string.Empty;
        }

        var parts = packageId
            .Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var vendor = parts[0].ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(vendor) || vendor.Length < 2)
        {
            return string.Empty;
        }

        if (vendor.Contains("microsoft", StringComparison.Ordinal))
        {
            return "microsoft.com";
        }

        if (vendor.Contains("google", StringComparison.Ordinal))
        {
            return "google.com";
        }

        if (vendor.Contains("github", StringComparison.Ordinal))
        {
            return "github.com";
        }

        if (vendor.EndsWith("corp", StringComparison.Ordinal))
        {
            vendor = vendor[..^4];
        }

        return $"{vendor}.com";
    }

    private static string SanitizePathSegment(string value)
    {
        var cleaned = value;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(cleaned) ? "package" : cleaned;
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "\"\"";
        if (!value.Contains(' ') && !value.Contains('\"')) return value;
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string Coalesce(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 1)].TrimEnd() + "...";
    }

    private static string GetJsonStringOrDefault(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static int GetJsonInt32OrDefault(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private sealed record DetectionStrategyPlan(IntuneDetectionRule Rule, string Guidance, bool IsDeterministic);
    private sealed record TemplateSuggestion(string InstallCommand, string UninstallCommand, string DetectionGuidance, int ConfidenceScore);
    private sealed record CachedSearchResult(IReadOnlyList<PackageCatalogEntry> Results, bool IsFresh);
    private sealed record WingetSourceInfo(string Name, bool IsExplicit);
    private sealed record WingetManifestInfo(
        string PackageIdentifier,
        string PackageVersion,
        string PackageName,
        string Publisher,
        string HomepageUrl,
        string ReleaseDate,
        IReadOnlyList<WingetManifestInstallerInfo> Installers);
    private sealed record WingetManifestInstallerInfo(
        string InstallerTypeRaw,
        string Architecture,
        string Scope,
        string InstallerUrl,
        string InstallerSha256,
        string ProductCode,
        string SilentSwitch,
        string SilentWithProgressSwitch,
        string InstallLocationSwitch,
        string DefaultInstallLocation,
        string DisplayName,
        string Publisher,
        string DisplayVersion);
    private sealed class WingetManifestInstallerBuilder
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Switches { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> AppsAndFeatures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> InstallationMetadata { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ChocolateySourceInfo(string Name, string ApiUrl, bool IsEnabled);
    private sealed record ScoopSearchRow(string Name, string Version, string Bucket);
    private sealed record ScoopManifestInfo(
        string Name,
        string Version,
        string Description,
        string HomepageUrl,
        string Notes,
        string Bucket,
        string Publisher,
        string InstallerUrl,
        string InstallerSha256,
        string InstallerTypeRaw,
        string IconUrl);
    private sealed record NuGetSourceInfo(string Name, string IndexUrl, bool IsEnabled);
    private sealed record ResolvedNuGetSourceInfo(
        string Name,
        string IndexUrl,
        string SearchQueryServiceUrl,
        string PackageBaseAddressUrl);
    private sealed record GitHubReleaseInfo(string TagName, string ReleaseName, DateTimeOffset? PublishedAtUtc, IReadOnlyList<GitHubAssetInfo> Assets);
    private sealed record GitHubAssetInfo(string FileName, string DownloadUrl, string ContentType, int FileSize);

    private static string ComputeStableHash(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string ComputeFileSha256(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool FileHashMatches(string filePath, string expectedSha256)
    {
        var expected = NormalizeSha256(expectedSha256);
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var actual = NormalizeSha256(ComputeFileSha256(filePath));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static (bool IsSigned, string Subject) TryGetSignature(string filePath)
    {
        try
        {
            var certificate = X509Certificate.CreateFromSignedFile(filePath);
            if (certificate is null)
            {
                return (false, string.Empty);
            }

            using var cert = new X509Certificate2(certificate);
            return (!string.IsNullOrWhiteSpace(cert.Subject), cert.Subject ?? string.Empty);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private static PackageCatalogDownloadResult BuildSuccessfulDownloadResult(
        string installerPath,
        string workingFolderPath,
        string message,
        bool hashVerifiedBySource)
    {
        var sha256 = ComputeFileSha256(installerPath);
        var signature = TryGetSignature(installerPath);

        return new PackageCatalogDownloadResult
        {
            Success = true,
            Message = message,
            InstallerPath = installerPath,
            WorkingFolderPath = workingFolderPath,
            InstallerSha256 = sha256,
            HashVerifiedBySource = hashVerifiedBySource,
            VendorSigned = signature.IsSigned,
            SignerSubject = signature.Subject
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }
}
