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

        return wingetTask.Result
            .Concat(chocolateyTask.Result)
            .Concat(githubTask.Result)
            .GroupBy(entry => $"{entry.Source}:{entry.SourceChannel}:{entry.PackageId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(entry => Relevance(entry, term))
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
            return entry.Source switch
            {
                PackageCatalogSource.Winget => await GetWingetDetailsAsync(entry, cancellationToken),
                PackageCatalogSource.Chocolatey => await GetChocolateyDetailsAsync(entry, cancellationToken),
                PackageCatalogSource.GitHubReleases => await GetGitHubReleaseDetailsAsync(entry, cancellationToken),
                _ => entry
            };
        }
        catch
        {
            return entry;
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

        var installerRaw = map.GetValueOrDefault("Installer Type", entry.InstallerTypeRaw);
        var installerType = InferInstallerType(installerRaw, map.GetValueOrDefault("Description"));
        var template = BuildTemplate(entry.PackageId, installerType, installerRaw);
        var homepage = Coalesce(map.GetValueOrDefault("Homepage"), map.GetValueOrDefault("Publisher Url"), entry.HomepageUrl);
        var installerUrl = Coalesce(map.GetValueOrDefault("Installer Url"), entry.InstallerDownloadUrl);

        DateTimeOffset? releaseDate = null;
        if (DateTimeOffset.TryParse(map.GetValueOrDefault("Release Date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            releaseDate = parsed.ToUniversalTime();
        }

        return entry with
        {
            Name = Coalesce(detailName, entry.Name),
            Version = Coalesce(map.GetValueOrDefault("Version"), entry.Version),
            BuildVersion = Coalesce(map.GetValueOrDefault("Version"), entry.BuildVersion),
            Publisher = Coalesce(map.GetValueOrDefault("Publisher"), entry.Publisher),
            Description = Coalesce(map.GetValueOrDefault("Description"), entry.Description),
            HomepageUrl = homepage,
            IconUrl = ResolveIconUrl(map.GetValueOrDefault("Icon"), homepage, entry.PackageId),
            InstallerDownloadUrl = installerUrl,
            InstallerType = installerType,
            InstallerTypeRaw = installerRaw,
            SuggestedInstallCommand = template.InstallCommand,
            SuggestedUninstallCommand = template.UninstallCommand,
            DetectionGuidance = template.DetectionGuidance,
            ConfidenceScore = template.ConfidenceScore,
            MetadataNotes = string.IsNullOrWhiteSpace(map.GetValueOrDefault("Installer Url"))
                ? $"Detailed metadata from WinGet source '{sourceName}'."
                : $"Installer URL: {map["Installer Url"]}",
            PublishedAtUtc = releaseDate,
            HasDetailedMetadata = true
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
                SuggestedUninstallCommand = template.UninstallCommand,
                DetectionGuidance = template.DetectionGuidance,
                MetadataNotes = string.IsNullOrWhiteSpace(tags) ? "Metadata from Chocolatey package feed." : $"Tags: {tags}",
                ConfidenceScore = Math.Max(30, template.ConfidenceScore - 20),
                HasDetailedMetadata = true
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

            var asset = SelectPreferredGitHubAsset(release.Assets);
            if (asset is null)
            {
                continue;
            }

            var installerType = InferInstallerType(Path.GetExtension(asset.FileName), asset.ContentType);
            var template = BuildTemplate(fullName, installerType, Path.GetExtension(asset.FileName));
            entries.Add(new PackageCatalogEntry
            {
                Source = PackageCatalogSource.GitHubReleases,
                SourceDisplayName = "GitHub Releases",
                SourceChannel = fullName,
                PackageId = fullName,
                Name = Coalesce(GetJsonStringOrDefault(repo, "name"), fullName),
                Version = NormalizeGitHubTagVersion(release.TagName),
                BuildVersion = NormalizeGitHubTagVersion(release.TagName),
                Publisher = owner,
                Description = Truncate(Coalesce(GetJsonStringOrDefault(repo, "description"), release.ReleaseName), 260),
                HomepageUrl = Coalesce(GetJsonStringOrDefault(repo, "html_url"), $"https://github.com/{fullName}"),
                IconUrl = Coalesce(repo.TryGetProperty("owner", out var ownerObject)
                    ? GetJsonStringOrDefault(ownerObject, "avatar_url")
                    : string.Empty),
                InstallerDownloadUrl = asset.DownloadUrl,
                InstallerType = installerType,
                InstallerTypeRaw = Path.GetExtension(asset.FileName),
                SuggestedInstallCommand = template.InstallCommand,
                SuggestedUninstallCommand = template.UninstallCommand,
                DetectionGuidance = template.DetectionGuidance,
                MetadataNotes = $"GitHub release asset: {asset.FileName}",
                ConfidenceScore = Math.Max(52, template.ConfidenceScore - 18),
                HasDetailedMetadata = true,
                PublishedAtUtc = release.PublishedAtUtc
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

        var asset = SelectPreferredGitHubAsset(release.Assets);
        if (asset is null)
        {
            return entry;
        }

        var installerType = InferInstallerType(Path.GetExtension(asset.FileName), asset.ContentType);
        var template = BuildTemplate(entry.PackageId, installerType, Path.GetExtension(asset.FileName));

        return entry with
        {
            SourceDisplayName = "GitHub Releases",
            SourceChannel = $"{owner}/{repo}",
            Version = NormalizeGitHubTagVersion(release.TagName),
            BuildVersion = NormalizeGitHubTagVersion(release.TagName),
            Description = Truncate(Coalesce(entry.Description, release.ReleaseName), 260),
            InstallerDownloadUrl = asset.DownloadUrl,
            InstallerType = installerType,
            InstallerTypeRaw = Path.GetExtension(asset.FileName),
            SuggestedInstallCommand = template.InstallCommand,
            SuggestedUninstallCommand = template.UninstallCommand,
            DetectionGuidance = template.DetectionGuidance,
            MetadataNotes = $"Latest GitHub release asset: {asset.FileName}",
            ConfidenceScore = Math.Max(entry.ConfidenceScore, Math.Max(56, template.ConfidenceScore - 16)),
            HasDetailedMetadata = true,
            PublishedAtUtc = release.PublishedAtUtc
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
