using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;
using IntuneWinPackager.Infrastructure.Support;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class PackageCatalogService : IPackageCatalogService
{
    private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://[^\s'""`]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GitHubRepoIdRegex = new(@"^(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)$", RegexOptions.Compiled);
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
        if (term.Length < 2 || (!query.IncludeWinget && !query.IncludeChocolatey && !query.IncludeGitHubReleases))
        {
            return [];
        }

        var max = Math.Clamp(query.MaxResults, 1, 50);
        var enabledSourceCount =
            (query.IncludeWinget ? 1 : 0) +
            (query.IncludeChocolatey ? 1 : 0) +
            (query.IncludeGitHubReleases ? 1 : 0);
        var perSource = enabledSourceCount > 1 ? Math.Max(6, max) : max;

        var wingetTask = query.IncludeWinget
            ? SearchWingetAsync(term, perSource, cancellationToken)
            : Task.FromResult<IReadOnlyList<PackageCatalogEntry>>([]);
        var chocolateyTask = query.IncludeChocolatey
            ? SearchChocolateyAsync(term, perSource, cancellationToken)
            : Task.FromResult<IReadOnlyList<PackageCatalogEntry>>([]);
        var githubTask = query.IncludeGitHubReleases
            ? SearchGitHubReleasesAsync(term, perSource, cancellationToken)
            : Task.FromResult<IReadOnlyList<PackageCatalogEntry>>([]);

        await Task.WhenAll(wingetTask, chocolateyTask, githubTask);
        var sourceEntries = wingetTask.Result
            .Concat(chocolateyTask.Result)
            .Concat(githubTask.Result)
            .Select(NormalizeCatalogEntry)
            .ToList();
        if (sourceEntries.Count == 0)
        {
            return [];
        }

        return MergeCanonicalEntries(sourceEntries)
            .OrderByDescending(entry => Relevance(entry, term))
            .ThenByDescending(entry => entry.InstallerVariantCount)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
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
        var packageName = Coalesce(detailName, map.GetValueOrDefault("Package Name"), entry.Name);
        var publisher = Coalesce(map.GetValueOrDefault("Publisher"), entry.Publisher);
        var version = Coalesce(map.GetValueOrDefault("Version"), entry.Version, entry.BuildVersion);
        var installerRaw = map.GetValueOrDefault("Installer Type", entry.InstallerTypeRaw);
        var installerType = InferInstallerType(installerRaw, map.GetValueOrDefault("Description"));
        var architecture = Coalesce(map.GetValueOrDefault("Architecture"), InferArchitecture(packageName, packageId, version, installerRaw));
        var scope = Coalesce(map.GetValueOrDefault("Scope"), InferScope(sourceName, packageName, packageId));
        var installerSha256 = NormalizeSha256(Coalesce(map.GetValueOrDefault("Installer SHA256")));
        var msiProductCode = NormalizeMsiProductCode(Coalesce(map.GetValueOrDefault("ProductCode"), map.GetValueOrDefault("Product Code")));
        var appxIdentity = Coalesce(map.GetValueOrDefault("Package Family Name"), map.GetValueOrDefault("Package Name"));
        var template = BuildTemplate(packageId, installerType, installerRaw);
        var installCommand = template.InstallCommand;
        var uninstallCommand = ResolveUninstallTemplate(template.UninstallCommand, installerType, msiProductCode, appxIdentity);
        var detection = BuildDeterministicDetectionRule(
            installerType,
            packageId,
            packageName,
            publisher,
            version,
            msiProductCode,
            appxIdentity);
        var homepage = Coalesce(map.GetValueOrDefault("Homepage"), map.GetValueOrDefault("Publisher Url"), entry.HomepageUrl);
        var installerUrl = Coalesce(map.GetValueOrDefault("Installer Url"), entry.InstallerDownloadUrl);
        var variant = BuildInstallerVariant(
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
            publishedAtUtc: null);

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
            InstallerDownloadUrl = installerUrl,
            InstallerType = installerType,
            InstallerTypeRaw = installerRaw,
            SuggestedInstallCommand = installCommand,
            SuggestedUninstallCommand = uninstallCommand,
            DetectionGuidance = detection.Guidance,
            ConfidenceScore = template.ConfidenceScore,
            MetadataNotes = string.IsNullOrWhiteSpace(map.GetValueOrDefault("Installer Url"))
                ? $"Detailed metadata from WinGet source '{sourceName}'."
                : $"Installer URL: {map["Installer Url"]}",
            PublishedAtUtc = releaseDate,
            HasDetailedMetadata = true,
            InstallerVariants = [variant]
        };
    }

    private async Task<IReadOnlyList<PackageCatalogEntry>> SearchChocolateyAsync(string term, int limit, CancellationToken cancellationToken)
    {
        var encodedTerm = Uri.EscapeDataString($"'{term}'");
        var url =
            $"https://community.chocolatey.org/api/v2/Search()?%24filter=IsLatestVersion&%24top={Math.Clamp(limit, 1, 50)}&searchTerm={encodedTerm}&targetFramework=%27%27&includePrerelease=false";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        return ParseChocolateyEntries(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task<PackageCatalogEntry> GetChocolateyDetailsAsync(PackageCatalogEntry entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.Version))
        {
            return entry;
        }

        var safeId = entry.PackageId.Replace("'", "''", StringComparison.Ordinal);
        var safeVersion = entry.Version.Replace("'", "''", StringComparison.Ordinal);
        var url =
            $"https://community.chocolatey.org/api/v2/Packages(Id='{safeId}',Version='{safeVersion}')";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return entry;
        }

        var detailed = ParseChocolateyEntries(await response.Content.ReadAsStringAsync(cancellationToken)).FirstOrDefault();
        return detailed ?? entry;
    }

    private IReadOnlyList<PackageCatalogEntry> ParseChocolateyEntries(string xml)
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
            var scope = InferScope("community", name, id);
            var appxIdentity = installerType == InstallerType.AppxMsix ? id : string.Empty;
            var uninstallCommand = ResolveUninstallTemplate(template.UninstallCommand, installerType, msiProductCode: string.Empty, appxIdentity);
            var detection = BuildDeterministicDetectionRule(
                installerType,
                id,
                name,
                author,
                version,
                msiProductCode: string.Empty,
                appxIdentity);
            var variant = BuildInstallerVariant(
                PackageCatalogSource.Chocolatey,
                "Chocolatey",
                "community",
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
                SourceDisplayName = "Chocolatey",
                SourceChannel = "community",
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
                MetadataNotes = string.IsNullOrWhiteSpace(tags) ? "Metadata from Chocolatey package feed." : $"Tags: {tags}",
                ConfidenceScore = Math.Max(30, template.ConfidenceScore - 20),
                HasDetailedMetadata = true,
                InstallerVariants = [variant]
            });
        }

        return parsed;
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
            var installerFromDownload = FindInstallerInFolder(workingFolder);
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
                    var fallbackMessage = hashMismatchDetected
                        ? "WinGet reported a hash mismatch. Downloaded installer from WinGet output URL, but source hash was not verified for this build."
                        : "Downloaded installer from WinGet output URL. Source hash was not verified by WinGet for this build.";
                    return BuildSuccessfulDownloadResult(
                        installerFromUrlFallback,
                        Path.GetDirectoryName(installerFromUrlFallback) ?? workingFolder,
                        fallbackMessage,
                        hashVerifiedBySource: false);
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
            return BuildSuccessfulDownloadResult(
                resolvedInstaller,
                Path.GetDirectoryName(resolvedInstaller) ?? workingFolder,
                "Downloaded installer from WinGet metadata URL.",
                hashVerifiedByWinget);
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
        var nupkgUrl = !string.IsNullOrWhiteSpace(entry.InstallerDownloadUrl)
            ? entry.InstallerDownloadUrl
            : BuildChocolateyPackageUrl(entry.PackageId, entry.Version);

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
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return string.Empty;
        }

        var candidates = Directory
            .EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(IsSupportedInstallerFile)
            .OrderBy(path => InstallerPriority(path))
            .ThenByDescending(path => new FileInfo(path).Length)
            .ToList();

        return candidates.FirstOrDefault() ?? string.Empty;
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

    private static string BuildChocolateyPackageUrl(string packageId, string version)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return $"https://community.chocolatey.org/api/v2/package/{Uri.EscapeDataString(packageId)}";
        }

        return $"https://community.chocolatey.org/api/v2/package/{Uri.EscapeDataString(packageId)}/{Uri.EscapeDataString(version)}";
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

        var result = await _processRunner.RunAsync(new ProcessRunRequest
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = Environment.CurrentDirectory,
            PreferLowImpact = true
        }, progress, cancellationToken);

        return (result.ExitCode, lines.ToArray());
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
                appxIdentity);
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
                appxIdentity);

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
                appxIdentity);
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
        string appxIdentity)
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
            if (!string.IsNullOrWhiteSpace(packageName) &&
                !string.IsNullOrWhiteSpace(publisher) &&
                !string.IsNullOrWhiteSpace(version))
            {
                return new DetectionStrategyPlan(
                    new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Script,
                        Script = new ScriptDetectionRule
                        {
                            ScriptBody = BuildExactRegistryDetectionScript(packageName, publisher, version),
                            RunAs32BitOn64System = false,
                            EnforceSignatureCheck = false
                        }
                    },
                    "Deterministic: exact registry equality on DisplayName, Publisher, and DisplayVersion.",
                    true);
            }

            return new DetectionStrategyPlan(
                new IntuneDetectionRule { RuleType = IntuneDetectionRuleType.None },
                "EXE detected, but strict registry values are incomplete. Fill exact DisplayName/Publisher/Version.",
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
                            ScriptBody = BuildExactAppxDetectionScript(identity, version),
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
        var escapedDisplayName = EscapePowerShellDoubleQuoted(displayName);
        var escapedPublisher = EscapePowerShellDoubleQuoted(publisher);
        var escapedVersion = EscapePowerShellDoubleQuoted(version);

        return string.Join(Environment.NewLine,
        [
            $"$displayName = \"{escapedDisplayName}\"",
            $"$publisher = \"{escapedPublisher}\"",
            $"$displayVersion = \"{escapedVersion}\"",
            "$roots = @(",
            "    'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',",
            "    'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',",
            "    'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'",
            ")",
            "$match = Get-ItemProperty -Path $roots -ErrorAction SilentlyContinue | Where-Object {",
            "    $_.DisplayName -eq $displayName -and",
            "    $_.Publisher -eq $publisher -and",
            "    $_.DisplayVersion -eq $displayVersion",
            "} | Select-Object -First 1",
            "if ($null -ne $match) { exit 0 }",
            "exit 1"
        ]);
    }

    private static string BuildExactAppxDetectionScript(string appxIdentity, string version)
    {
        var escapedIdentity = EscapePowerShellDoubleQuoted(appxIdentity);
        var escapedVersion = EscapePowerShellDoubleQuoted(version);

        return string.Join(Environment.NewLine,
        [
            $"$packageName = \"{escapedIdentity}\"",
            $"$expectedVersion = \"{escapedVersion}\"",
            "$match = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Where-Object {",
            "    $_.Version.ToString() -eq $expectedVersion",
            "} | Select-Object -First 1",
            "if ($null -ne $match) { exit 0 }",
            "exit 1"
        ]);
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

    private static string EscapePowerShellDoubleQuoted(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace("`", "``", StringComparison.Ordinal)
            .Replace("\"", "`\"", StringComparison.Ordinal);
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
        if (value.Contains("msi", StringComparison.Ordinal)) return InstallerType.Msi;
        if (value.Contains("inno", StringComparison.Ordinal) || value.Contains("nsis", StringComparison.Ordinal) || value.Contains("nullsoft", StringComparison.Ordinal) || value.Contains("installshield", StringComparison.Ordinal) || value.Contains("wix", StringComparison.Ordinal) || value.Contains("burn", StringComparison.Ordinal) || value.Contains("exe", StringComparison.Ordinal)) return InstallerType.Exe;
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
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength) return value;
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
    private sealed record WingetSourceInfo(string Name, bool IsExplicit);
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
