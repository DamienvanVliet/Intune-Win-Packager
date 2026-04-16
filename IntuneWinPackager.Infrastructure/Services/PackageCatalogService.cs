using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class PackageCatalogService : IPackageCatalogService
{
    private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);

    private readonly IProcessRunner _processRunner;
    private readonly HttpClient _httpClient;

    public PackageCatalogService(IProcessRunner processRunner, HttpClient? httpClient = null)
    {
        _processRunner = processRunner;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("IntuneWinPackager/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
    }

    public async Task<IReadOnlyList<PackageCatalogEntry>> SearchAsync(PackageCatalogQuery query, CancellationToken cancellationToken = default)
    {
        query ??= new PackageCatalogQuery();
        var term = query.SearchTerm?.Trim() ?? string.Empty;
        if (term.Length < 2 || (!query.IncludeWinget && !query.IncludeChocolatey))
        {
            return [];
        }

        var max = Math.Clamp(query.MaxResults, 1, 50);
        var perSource = query.IncludeWinget && query.IncludeChocolatey ? Math.Max(6, max) : max;

        var wingetTask = query.IncludeWinget
            ? SearchWingetAsync(term, perSource, cancellationToken)
            : Task.FromResult<IReadOnlyList<PackageCatalogEntry>>([]);
        var chocolateyTask = query.IncludeChocolatey
            ? SearchChocolateyAsync(term, perSource, cancellationToken)
            : Task.FromResult<IReadOnlyList<PackageCatalogEntry>>([]);

        await Task.WhenAll(wingetTask, chocolateyTask);

        return wingetTask.Result
            .Concat(chocolateyTask.Result)
            .GroupBy(entry => $"{entry.Source}:{entry.PackageId}", StringComparer.OrdinalIgnoreCase)
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
                _ => entry
            };
        }
        catch
        {
            return entry;
        }
    }

    private async Task<IReadOnlyList<PackageCatalogEntry>> SearchWingetAsync(string term, int limit, CancellationToken cancellationToken)
    {
        var (exitCode, lines) = await RunProcessCaptureAsync(
            "winget",
            $"search --query {QuoteArgument(term)} --count {Math.Clamp(limit, 1, 1000)} --source winget --accept-source-agreements --disable-interactivity",
            cancellationToken);
        if (exitCode != 0 || lines.Count == 0)
        {
            return [];
        }

        var rows = ParseWingetRows(lines);
        var entries = rows.Select(row => new PackageCatalogEntry
        {
            Source = PackageCatalogSource.Winget,
            SourceDisplayName = "WinGet",
            PackageId = row.Id,
            Name = row.Name,
            Version = row.Version,
            BuildVersion = row.Version,
            Description = row.Match,
            MetadataNotes = "Basic result from WinGet search.",
            ConfidenceScore = 25
        }).ToList();

        var enrichCount = Math.Min(entries.Count, 4);
        for (var i = 0; i < enrichCount; i++)
        {
            entries[i] = await GetWingetDetailsAsync(entries[i], cancellationToken);
        }

        return entries;
    }

    private async Task<PackageCatalogEntry> GetWingetDetailsAsync(PackageCatalogEntry entry, CancellationToken cancellationToken)
    {
        var (exitCode, lines) = await RunProcessCaptureAsync(
            "winget",
            $"show --id {QuoteArgument(entry.PackageId)} --exact --source winget --accept-source-agreements --disable-interactivity --locale en-US",
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
            IconUrl = ResolveIconUrl(map.GetValueOrDefault("Icon"), homepage),
            InstallerType = installerType,
            InstallerTypeRaw = installerRaw,
            SuggestedInstallCommand = template.InstallCommand,
            SuggestedUninstallCommand = template.UninstallCommand,
            DetectionGuidance = template.DetectionGuidance,
            ConfidenceScore = template.ConfidenceScore,
            MetadataNotes = string.IsNullOrWhiteSpace(map.GetValueOrDefault("Installer Url"))
                ? "Detailed metadata from WinGet."
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
            var author = atomEntry.Element(atom + "author")?.Element(atom + "name")?.Value?.Trim() ?? string.Empty;
            var installerType = InferInstallerType(tags, summary);
            var template = BuildTemplate(id, installerType, tags);

            parsed.Add(new PackageCatalogEntry
            {
                Source = PackageCatalogSource.Chocolatey,
                SourceDisplayName = "Chocolatey",
                PackageId = id,
                Name = name,
                Version = version,
                BuildVersion = version,
                Publisher = author,
                Description = Truncate(summary, 260),
                HomepageUrl = homepage,
                IconUrl = ResolveIconUrl(icon, homepage),
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

    private async Task<(int ExitCode, IReadOnlyList<string> Lines)> RunProcessCaptureAsync(string executable, string arguments, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var progress = new Progress<ProcessOutputLine>(line =>
        {
            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                lines.Add(line.Text.TrimEnd());
            }
        });

        var result = await _processRunner.RunAsync(new ProcessRunRequest
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = Environment.CurrentDirectory,
            PreferLowImpact = true
        }, progress, cancellationToken);

        return (result.ExitCode, lines);
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
        return score;
    }

    private static string ResolveIconUrl(string? iconUrl, string? homepage)
    {
        if (!string.IsNullOrWhiteSpace(iconUrl) && Uri.TryCreate(iconUrl, UriKind.Absolute, out var iconUri) && !iconUri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return iconUrl!;
        }

        if (!string.IsNullOrWhiteSpace(homepage) && Uri.TryCreate(homepage, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return $"https://icons.duckduckgo.com/ip3/{uri.Host}.ico";
        }

        return string.Empty;
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

    private sealed record TemplateSuggestion(string InstallCommand, string UninstallCommand, string DetectionGuidance, int ConfidenceScore);
}
