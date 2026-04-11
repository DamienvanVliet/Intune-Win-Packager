using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Infrastructure.Support;
using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class AppUpdateService : IAppUpdateService
{
    private const string RepoFullName = "DamienvanVliet/Intune-Win-Packager";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/DamienvanVliet/Intune-Win-Packager/releases/latest";
    private const string ReleasesApiUrl = "https://api.github.com/repos/DamienvanVliet/Intune-Win-Packager/releases?per_page=20";

    private readonly HttpClient _httpClient;

    public AppUpdateService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(35)
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("IntuneWinPackager/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<AppUpdateInfo> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            currentVersion = "0.0.0";
        }

        var currentNormalized = NormalizeVersion(currentVersion);

        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound
                    || response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var releasesApiFallback = await TryCheckWithPublicReleasesListAsync(currentNormalized, cancellationToken);
                    if (releasesApiFallback is not null)
                    {
                        return releasesApiFallback;
                    }

                    var ghFallback = await TryCheckWithGhCliAsync(currentVersion, cancellationToken);
                    if (ghFallback is not null)
                    {
                        return ghFallback;
                    }

                    return new AppUpdateInfo
                    {
                        CheckSucceeded = false,
                        IsUpdateAvailable = false,
                        CurrentVersion = currentNormalized,
                        LatestVersion = currentNormalized,
                        Message = "No public release feed found (HTTP 404). If this repository is private, auto-updates require GitHub CLI login on this machine."
                    };
                }

                return new AppUpdateInfo
                {
                    CheckSucceeded = false,
                    CurrentVersion = currentNormalized,
                    LatestVersion = currentNormalized,
                    Message = $"Update check failed (HTTP {(int)response.StatusCode})."
                };
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            return BuildUpdateInfoFromReleasePayload(document.RootElement, currentNormalized);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var releasesApiFallback = await TryCheckWithPublicReleasesListAsync(currentNormalized, cancellationToken);
            if (releasesApiFallback is not null)
            {
                return releasesApiFallback;
            }

            var ghFallback = await TryCheckWithGhCliAsync(currentVersion, cancellationToken);
            if (ghFallback is not null)
            {
                return ghFallback;
            }

            return new AppUpdateInfo
            {
                CheckSucceeded = false,
                CurrentVersion = currentNormalized,
                LatestVersion = currentNormalized,
                Message = $"Update check failed: {ex.Message}"
            };
        }
    }

    public async Task<AppUpdateInstallResult> DownloadAndLaunchInstallerAsync(
        AppUpdateInfo updateInfo,
        IProgress<string>? logProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (updateInfo is null)
        {
            return new AppUpdateInstallResult
            {
                Success = false,
                Message = "No update metadata was provided."
            };
        }

        try
        {
            DataPathProvider.EnsureBaseDirectory();
            Directory.CreateDirectory(DataPathProvider.UpdatesDirectory);

            var installerFileName = BuildInstallerFileName(updateInfo);
            var installerPath = Path.Combine(DataPathProvider.UpdatesDirectory, installerFileName);

            if (string.IsNullOrWhiteSpace(updateInfo.InstallerDownloadUrl))
            {
                var ghDownloadResult = await TryDownloadWithGhCliAsync(updateInfo, logProgress, cancellationToken);
                if (ghDownloadResult is not null)
                {
                    return ghDownloadResult;
                }

                return new AppUpdateInstallResult
                {
                    Success = false,
                    Message = "No installer download URL is available for this release."
                };
            }

            logProgress?.Report($"Downloading update installer {installerFileName}...");

            using var response = await _httpClient.GetAsync(
                updateInfo.InstallerDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var targetStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long totalRead = 0;
            var lastReportedPercent = -1;

            while (true)
            {
                var read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await targetStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    var percent = (int)Math.Round((double)totalRead * 100d / totalBytes.Value);
                    if (percent >= lastReportedPercent + 10)
                    {
                        lastReportedPercent = percent;
                        logProgress?.Report($"Downloading installer... {percent}%");
                    }
                }
            }

            await targetStream.FlushAsync(cancellationToken);

            if (!File.Exists(installerPath))
            {
                return new AppUpdateInstallResult
                {
                    Success = false,
                    Message = "Update installer download failed.",
                    InstallerPath = installerPath
                };
            }

            logProgress?.Report("Launching update installer...");

            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            });

            return new AppUpdateInstallResult
            {
                Success = true,
                Message = "Installer launched successfully.",
                InstallerPath = installerPath
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var ghDownloadResult = await TryDownloadWithGhCliAsync(updateInfo, logProgress, cancellationToken);
            if (ghDownloadResult is not null)
            {
                return ghDownloadResult;
            }

            return new AppUpdateInstallResult
            {
                Success = false,
                Message = $"Update install failed: {ex.Message}"
            };
        }
    }

    private async Task<AppUpdateInfo?> TryCheckWithGhCliAsync(string currentVersion, CancellationToken cancellationToken)
    {
        var ghCliPath = ResolveGhCliPath();
        if (ghCliPath is null)
        {
            return null;
        }

        var result = await RunProcessCaptureAsync(
            ghCliPath,
            $"release view --repo {RepoFullName} --json tagName,name,body,publishedAt,assets",
            cancellationToken);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var currentNormalized = NormalizeVersion(currentVersion);
            return BuildUpdateInfoFromReleasePayload(document.RootElement, currentNormalized);
        }
        catch
        {
            return null;
        }
    }

    private async Task<AppUpdateInstallResult?> TryDownloadWithGhCliAsync(
        AppUpdateInfo updateInfo,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        var ghCliPath = ResolveGhCliPath();
        if (ghCliPath is null)
        {
            return null;
        }

        DataPathProvider.EnsureBaseDirectory();
        Directory.CreateDirectory(DataPathProvider.UpdatesDirectory);

        var releaseTag = !string.IsNullOrWhiteSpace(updateInfo.ReleaseTag)
            ? updateInfo.ReleaseTag
            : $"v{NormalizeVersion(updateInfo.LatestVersion)}";

        logProgress?.Report("Trying GitHub CLI fallback for private release download...");

        var downloadResult = await RunProcessCaptureAsync(
            ghCliPath,
            $"release download {releaseTag} --repo {RepoFullName} --pattern \"*.exe\" --dir \"{DataPathProvider.UpdatesDirectory}\" --clobber",
            cancellationToken);

        if (downloadResult.ExitCode != 0)
        {
            return null;
        }

        var downloadedInstaller = Directory
            .EnumerateFiles(DataPathProvider.UpdatesDirectory, "*.exe", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (downloadedInstaller is null || !downloadedInstaller.Exists)
        {
            return null;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = downloadedInstaller.FullName,
            UseShellExecute = true
        });

        return new AppUpdateInstallResult
        {
            Success = true,
            Message = "Installer launched successfully.",
            InstallerPath = downloadedInstaller.FullName
        };
    }

    private static string? ResolveGhCliPath()
    {
        var environmentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var candidates = environmentPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(path, "gh.exe"))
            .Where(File.Exists)
            .ToList();

        if (candidates.Count > 0)
        {
            return candidates[0];
        }

        var knownPath = @"C:\Program Files\GitHub CLI\gh.exe";
        return File.Exists(knownPath) ? knownPath : null;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessCaptureAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return (process.ExitCode, stdOut, stdErr);
    }

    private static string BuildInstallerFileName(AppUpdateInfo updateInfo)
    {
        var safeVersion = string.IsNullOrWhiteSpace(updateInfo.LatestVersion)
            ? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            : updateInfo.LatestVersion.Replace(' ', '-');

        return $"IntuneWinPackager-Setup-{safeVersion}.exe";
    }

    private static string GetInstallerDownloadUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        string? fallbackExe = null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            var url = GetString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(url))
            {
                url = GetString(asset, "url");
            }

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fallbackExe is null)
            {
                fallbackExe = url;
            }

            if (name.Contains("setup", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        return fallbackExe ?? string.Empty;
    }

    private async Task<AppUpdateInfo?> TryCheckWithPublicReleasesListAsync(string currentNormalized, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(ReleasesApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            JsonElement? firstNonDraftStable = null;
            JsonElement? firstNonDraft = null;

            foreach (var release in document.RootElement.EnumerateArray())
            {
                var isDraft = TryGetBoolean(release, "draft");
                if (isDraft)
                {
                    continue;
                }

                firstNonDraft ??= release;

                var isPrerelease = TryGetBoolean(release, "prerelease");
                if (!isPrerelease)
                {
                    firstNonDraftStable = release;
                    break;
                }
            }

            if (firstNonDraftStable is not null)
            {
                return BuildUpdateInfoFromReleasePayload(firstNonDraftStable.Value, currentNormalized);
            }

            if (firstNonDraft is not null)
            {
                return BuildUpdateInfoFromReleasePayload(firstNonDraft.Value, currentNormalized);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static AppUpdateInfo BuildUpdateInfoFromReleasePayload(JsonElement root, string currentNormalized)
    {
        var tagName = GetStringWithFallback(root, "tag_name", "tagName");
        var releaseName = GetString(root, "name");
        var releaseNotes = GetString(root, "body");
        var publishedAt = TryGetDateTimeOffsetWithFallback(root, "published_at", "publishedAt");
        var latestVersion = NormalizeVersion(tagName);
        var installerUrl = GetInstallerDownloadUrl(root);

        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            latestVersion = currentNormalized;
        }

        var updateAvailable = IsVersionGreater(latestVersion, currentNormalized);
        if (!updateAvailable)
        {
            return new AppUpdateInfo
            {
                CheckSucceeded = true,
                IsUpdateAvailable = false,
                CurrentVersion = currentNormalized,
                LatestVersion = latestVersion,
                ReleaseTag = tagName,
                ReleaseName = releaseName,
                ReleaseNotes = releaseNotes,
                InstallerDownloadUrl = installerUrl,
                PublishedAtUtc = publishedAt,
                Message = $"You already have the latest version ({currentNormalized})."
            };
        }

        if (string.IsNullOrWhiteSpace(installerUrl))
        {
            return new AppUpdateInfo
            {
                CheckSucceeded = true,
                IsUpdateAvailable = false,
                CurrentVersion = currentNormalized,
                LatestVersion = latestVersion,
                ReleaseTag = tagName,
                ReleaseName = releaseName,
                ReleaseNotes = releaseNotes,
                PublishedAtUtc = publishedAt,
                Message = "A newer release exists, but no installer asset was found."
            };
        }

        return new AppUpdateInfo
        {
            CheckSucceeded = true,
            IsUpdateAvailable = true,
            CurrentVersion = currentNormalized,
            LatestVersion = latestVersion,
            ReleaseTag = tagName,
            ReleaseName = releaseName,
            ReleaseNotes = releaseNotes,
            InstallerDownloadUrl = installerUrl,
            PublishedAtUtc = publishedAt,
            Message = $"Update available: {latestVersion}"
        };
    }

    private static bool IsVersionGreater(string latestVersion, string currentVersion)
    {
        if (TryParseVersion(latestVersion, out var latest) && TryParseVersion(currentVersion, out var current))
        {
            return latest > current;
        }

        return !string.Equals(latestVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVersion(string value, out Version parsedVersion)
    {
        parsedVersion = new Version(0, 0, 0, 0);

        var normalized = NormalizeVersion(value);
        if (!Version.TryParse(normalized, out var parsed))
        {
            return false;
        }

        parsedVersion = parsed;
        return true;
    }

    private static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var dashIndex = normalized.IndexOf('-');
        if (dashIndex > 0)
        {
            normalized = normalized[..dashIndex];
        }

        return normalized;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return property.GetString() ?? string.Empty;
    }

    private static string GetStringWithFallback(JsonElement element, string primaryPropertyName, string secondaryPropertyName)
    {
        var primary = GetString(element, primaryPropertyName);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary;
        }

        return GetString(element, secondaryPropertyName);
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
        {
            return value;
        }

        return null;
    }

    private static DateTimeOffset? TryGetDateTimeOffsetWithFallback(JsonElement element, string primaryPropertyName, string secondaryPropertyName)
    {
        var primary = TryGetDateTimeOffset(element, primaryPropertyName);
        if (primary.HasValue)
        {
            return primary;
        }

        return TryGetDateTimeOffset(element, secondaryPropertyName);
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind == JsonValueKind.True;
    }
}
