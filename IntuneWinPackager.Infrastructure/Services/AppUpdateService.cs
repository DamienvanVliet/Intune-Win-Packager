using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Infrastructure.Support;
using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class AppUpdateService : IAppUpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/DamienvanVliet/Intune-Win-Packager/releases/latest";

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

        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AppUpdateInfo
                {
                    CurrentVersion = NormalizeVersion(currentVersion),
                    Message = $"Update check failed (HTTP {(int)response.StatusCode})."
                };
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tagName = GetString(root, "tag_name");
            var releaseName = GetString(root, "name");
            var releaseNotes = GetString(root, "body");
            var publishedAt = TryGetDateTimeOffset(root, "published_at");

            var latestVersion = NormalizeVersion(tagName);
            var currentNormalized = NormalizeVersion(currentVersion);
            var installerUrl = GetInstallerDownloadUrl(root);

            var updateAvailable = IsVersionGreater(latestVersion, currentNormalized);
            if (!updateAvailable)
            {
                return new AppUpdateInfo
                {
                    IsUpdateAvailable = false,
                    CurrentVersion = currentNormalized,
                    LatestVersion = latestVersion,
                    ReleaseName = releaseName,
                    ReleaseNotes = releaseNotes,
                    InstallerDownloadUrl = installerUrl,
                    PublishedAtUtc = publishedAt,
                    Message = "You already have the latest version."
                };
            }

            if (string.IsNullOrWhiteSpace(installerUrl))
            {
                return new AppUpdateInfo
                {
                    IsUpdateAvailable = false,
                    CurrentVersion = currentNormalized,
                    LatestVersion = latestVersion,
                    ReleaseName = releaseName,
                    ReleaseNotes = releaseNotes,
                    PublishedAtUtc = publishedAt,
                    Message = "A newer release exists, but no installer asset was found."
                };
            }

            return new AppUpdateInfo
            {
                IsUpdateAvailable = true,
                CurrentVersion = currentNormalized,
                LatestVersion = latestVersion,
                ReleaseName = releaseName,
                ReleaseNotes = releaseNotes,
                InstallerDownloadUrl = installerUrl,
                PublishedAtUtc = publishedAt,
                Message = $"Update available: {latestVersion}"
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AppUpdateInfo
            {
                CurrentVersion = NormalizeVersion(currentVersion),
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

        if (string.IsNullOrWhiteSpace(updateInfo.InstallerDownloadUrl))
        {
            return new AppUpdateInstallResult
            {
                Success = false,
                Message = "No installer download URL is available for this release."
            };
        }

        try
        {
            DataPathProvider.EnsureBaseDirectory();
            Directory.CreateDirectory(DataPathProvider.UpdatesDirectory);

            var installerFileName = BuildInstallerFileName(updateInfo);
            var installerPath = Path.Combine(DataPathProvider.UpdatesDirectory, installerFileName);

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
            return new AppUpdateInstallResult
            {
                Success = false,
                Message = $"Update install failed: {ex.Message}"
            };
        }
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
}
