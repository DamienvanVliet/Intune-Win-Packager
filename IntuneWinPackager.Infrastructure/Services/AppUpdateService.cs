using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Infrastructure.Support;
using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class AppUpdateService : IAppUpdateService
{
    private const string RepoFullName = "DamienvanVliet/Intune-Win-Packager";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/DamienvanVliet/Intune-Win-Packager/releases/latest";
    private const string ReleasesApiUrl = "https://api.github.com/repos/DamienvanVliet/Intune-Win-Packager/releases?per_page=20";
    private const int MaxHttpAttempts = 3;

    private readonly HttpClient _httpClient;

    public AppUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(35)
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("IntuneWinPackager/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<AppUpdateInfo> CheckForUpdatesAsync(
        string currentVersion,
        DateTimeOffset? currentBuildTimestampUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            currentVersion = "0.0.0";
        }

        var currentNormalized = NormalizeVersion(currentVersion);

        try
        {
            var releasesApiPreferred = await TryCheckWithPublicReleasesListAsync(
                currentNormalized,
                currentBuildTimestampUtc,
                cancellationToken);
            if (releasesApiPreferred is not null)
            {
                return releasesApiPreferred;
            }

            using var response = await GetWithRetryAsync(
                $"{LatestReleaseApiUrl}?_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                HttpCompletionOption.ResponseContentRead,
                operationCodePrefix: "UPD-CHK",
                progress: null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound
                    || response.StatusCode == HttpStatusCode.Unauthorized
                    || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var releasesApiFallback = await TryCheckWithPublicReleasesListAsync(
                        currentNormalized,
                        currentBuildTimestampUtc,
                        cancellationToken);
                    if (releasesApiFallback is not null)
                    {
                        return releasesApiFallback;
                    }

                    var ghFallback = await TryCheckWithGhCliAsync(
                        currentVersion,
                        currentBuildTimestampUtc,
                        cancellationToken);
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
                        Message = WithCode("UPD-CHK-FEED", "No public release feed found. If this repository is private, auto-updates require GitHub CLI login on this machine.")
                    };
                }

                return new AppUpdateInfo
                {
                    CheckSucceeded = false,
                    CurrentVersion = currentNormalized,
                    LatestVersion = currentNormalized,
                    Message = WithCode($"UPD-CHK-HTTP-{(int)response.StatusCode}", $"Update check failed (HTTP {(int)response.StatusCode}).")
                };
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            return BuildUpdateInfoFromReleasePayload(document.RootElement, currentNormalized, currentBuildTimestampUtc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var releasesApiFallback = await TryCheckWithPublicReleasesListAsync(
                currentNormalized,
                currentBuildTimestampUtc,
                cancellationToken);
            if (releasesApiFallback is not null)
            {
                return releasesApiFallback;
            }

            var ghFallback = await TryCheckWithGhCliAsync(
                currentVersion,
                currentBuildTimestampUtc,
                cancellationToken);
            if (ghFallback is not null)
            {
                return ghFallback;
            }

            return new AppUpdateInfo
            {
                CheckSucceeded = false,
                CurrentVersion = currentNormalized,
                LatestVersion = currentNormalized,
                Message = WithCode("UPD-CHK-EX", $"Update check failed: {ex.Message}")
            };
        }
    }

    public async Task<AppUpdateInstallResult> DownloadAndLaunchInstallerAsync(
        AppUpdateInfo updateInfo,
        IProgress<string>? logProgress = null,
        bool silentInstall = false,
        CancellationToken cancellationToken = default)
    {
        if (updateInfo is null)
        {
            return new AppUpdateInstallResult
            {
                Success = false,
                Message = WithCode("UPD-DL-NODATA", "No update metadata was provided.")
            };
        }

        DataPathProvider.EnsureBaseDirectory();
        Directory.CreateDirectory(DataPathProvider.UpdatesDirectory);

        var installerFileName = BuildInstallerFileName(updateInfo);
        var installerPath = Path.Combine(DataPathProvider.UpdatesDirectory, installerFileName);

        if (string.IsNullOrWhiteSpace(updateInfo.InstallerDownloadUrl))
        {
            var ghDownloadResult = await TryDownloadWithGhCliAsync(updateInfo, logProgress, silentInstall, cancellationToken);
            if (ghDownloadResult is not null)
            {
                return ghDownloadResult;
            }

            return new AppUpdateInstallResult
            {
                Success = false,
                Message = WithCode("UPD-DL-NOURL", "No installer download URL is available for this release.")
            };
        }

        try
        {
            logProgress?.Report($"Downloading update installer {installerFileName}...");

            using var response = await GetWithRetryAsync(
                updateInfo.InstallerDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                operationCodePrefix: "UPD-DL",
                progress: logProgress,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            await using (var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var targetStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
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
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var ghDownloadResult = await TryDownloadWithGhCliAsync(updateInfo, logProgress, silentInstall, cancellationToken);
            if (ghDownloadResult is not null)
            {
                return ghDownloadResult;
            }

            return new AppUpdateInstallResult
            {
                Success = false,
                Message = WithCode("UPD-DL-EX", $"Update download failed: {ex.Message}"),
                InstallerPath = installerPath
            };
        }

        if (!File.Exists(installerPath))
        {
            return new AppUpdateInstallResult
            {
                Success = false,
                Message = WithCode("UPD-DL-MISSING", "Update installer download failed."),
                InstallerPath = installerPath
            };
        }

        logProgress?.Report("Verifying installer integrity (SHA-256)...");
        var hashVerification = await VerifyInstallerHashAsync(installerPath, updateInfo.InstallerSha256, cancellationToken);
        if (!hashVerification.Success)
        {
            return new AppUpdateInstallResult
            {
                Success = false,
                Message = hashVerification.Message,
                InstallerPath = installerPath
            };
        }
        logProgress?.Report("Installer hash verified.");

        return TryLaunchInstaller(installerPath, installerFileName, silentInstall, logProgress);
    }

    private AppUpdateInstallResult TryLaunchInstaller(
        string installerPath,
        string installerFileName,
        bool silentInstall,
        IProgress<string>? logProgress)
    {
        try
        {
            var installerArguments = silentInstall
                ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS"
                : string.Empty;

            logProgress?.Report("Scheduling update installer after app shutdown...");

            if (!TryScheduleInstallerAfterCurrentProcessExit(installerPath, installerArguments, out var scheduleError))
            {
                return new AppUpdateInstallResult
                {
                    Success = false,
                    Message = WithCode(
                        "UPD-LAUNCH-SCHED",
                        $"Installer could not be scheduled safely after app shutdown. {scheduleError}"),
                    InstallerPath = installerPath
                };
            }

            return new AppUpdateInstallResult
            {
                Success = true,
                Message = silentInstall
                    ? $"Silent installer scheduled successfully ({installerFileName})."
                    : $"Installer scheduled successfully ({installerFileName}).",
                InstallerPath = installerPath
            };
        }
        catch (Exception ex)
        {
            return new AppUpdateInstallResult
            {
                Success = false,
                Message = WithCode(
                    "UPD-LAUNCH-EX",
                    $"Installer could not be launched automatically. Open this file manually: {installerPath}. Details: {ex.Message}"),
                InstallerPath = installerPath
            };
        }
    }

    private static bool TryScheduleInstallerAfterCurrentProcessExit(
        string installerPath,
        string installerArguments,
        out string errorMessage)
    {
        errorMessage = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                errorMessage = "Installer file was not found on disk.";
                return false;
            }

            var currentPid = Environment.ProcessId;
            var currentProcessName = Process.GetCurrentProcess().ProcessName;
            var launcherScriptPath = Path.Combine(
                Path.GetDirectoryName(installerPath) ?? DataPathProvider.UpdatesDirectory,
                $"launch-update-{Guid.NewGuid():N}.cmd");
            var script = BuildDeferredInstallerBatch(
                currentPid,
                currentProcessName,
                installerPath,
                installerArguments);
            File.WriteAllText(launcherScriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var launcherStartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{launcherScriptPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? DataPathProvider.UpdatesDirectory
            };

            Process.Start(launcherStartInfo);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string BuildDeferredInstallerBatch(
        int processId,
        string processName,
        string installerPath,
        string installerArguments)
    {
        var targetImage = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : $"{processName}.exe";

        var escapedImage = EscapeBatchVariableValue(targetImage);
        var escapedInstallerPath = EscapeBatchVariableValue(installerPath);
        var escapedInstallerArgs = EscapeBatchVariableValue(installerArguments);

        return string.Join(
            Environment.NewLine,
            "@echo off",
            "setlocal",
            $"set \"TARGET_PID={processId}\"",
            $"set \"TARGET_IMAGE={escapedImage}\"",
            $"set \"INSTALLER_PATH={escapedInstallerPath}\"",
            $"set \"INSTALLER_ARGS={escapedInstallerArgs}\"",
            ":wait_pid",
            "tasklist /FI \"PID eq %TARGET_PID%\" /NH | findstr /R /C:\" %TARGET_PID% \" >NUL",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >NUL",
            "  goto wait_pid",
            ")",
            ":wait_image",
            "tasklist /FI \"IMAGENAME eq %TARGET_IMAGE%\" /NH | findstr /I /C:\"%TARGET_IMAGE%\" >NUL",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >NUL",
            "  goto wait_image",
            ")",
            "timeout /t 1 /nobreak >NUL",
            "if \"%INSTALLER_ARGS%\"==\"\" (",
            "  start \"\" \"%INSTALLER_PATH%\"",
            ") else (",
            "  start \"\" \"%INSTALLER_PATH%\" %INSTALLER_ARGS%",
            ")",
            "del \"%~f0\" >NUL 2>&1",
            "endlocal");
    }

    private static string EscapeBatchVariableValue(string value)
    {
        return value
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("\"", "\"\"", StringComparison.Ordinal);
    }

    private async Task<AppUpdateInfo?> TryCheckWithGhCliAsync(
        string currentVersion,
        DateTimeOffset? currentBuildTimestampUtc,
        CancellationToken cancellationToken)
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
            return BuildUpdateInfoFromReleasePayload(document.RootElement, currentNormalized, currentBuildTimestampUtc);
        }
        catch
        {
            return null;
        }
    }

    private async Task<AppUpdateInstallResult?> TryDownloadWithGhCliAsync(
        AppUpdateInfo updateInfo,
        IProgress<string>? logProgress,
        bool silentInstall,
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

        var hashVerification = await VerifyInstallerHashAsync(downloadedInstaller.FullName, updateInfo.InstallerSha256, cancellationToken);
        if (!hashVerification.Success)
        {
            return new AppUpdateInstallResult
            {
                Success = false,
                Message = hashVerification.Message,
                InstallerPath = downloadedInstaller.FullName
            };
        }

        return TryLaunchInstaller(
            downloadedInstaller.FullName,
            downloadedInstaller.Name,
            silentInstall,
            logProgress);
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

    private static (string DownloadUrl, string Sha256, string FileName) GetInstallerAssetMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        (string DownloadUrl, string Sha256, string FileName)? fallbackExe = null;

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

            var digest = GetString(asset, "digest");
            var sha256 = NormalizeSha256Digest(digest);
            var metadata = (url, sha256, name);

            if (fallbackExe is null)
            {
                fallbackExe = metadata;
            }

            if (name.Contains("setup", StringComparison.OrdinalIgnoreCase))
            {
                return metadata;
            }
        }

        return fallbackExe ?? (string.Empty, string.Empty, string.Empty);
    }

    private async Task<AppUpdateInfo?> TryCheckWithPublicReleasesListAsync(
        string currentNormalized,
        DateTimeOffset? currentBuildTimestampUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await GetWithRetryAsync(
                $"{ReleasesApiUrl}&_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                HttpCompletionOption.ResponseContentRead,
                operationCodePrefix: "UPD-CHK-LIST",
                progress: null,
                cancellationToken);
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

            JsonElement? bestStable = null;
            JsonElement? bestAny = null;
            var bestStableVersion = "0.0.0";
            var bestAnyVersion = "0.0.0";
            DateTimeOffset? bestStablePublishedAt = null;
            DateTimeOffset? bestAnyPublishedAt = null;

            foreach (var release in document.RootElement.EnumerateArray())
            {
                var isDraft = TryGetBoolean(release, "draft");
                if (isDraft)
                {
                    continue;
                }

                var candidateVersion = NormalizeVersion(GetStringWithFallback(release, "tag_name", "tagName"));
                var candidatePublishedAt = TryGetDateTimeOffsetWithFallback(release, "published_at", "publishedAt");

                if (bestAny is null ||
                    CompareVersions(candidateVersion, bestAnyVersion) > 0 ||
                    (CompareVersions(candidateVersion, bestAnyVersion) == 0 &&
                     IsPublishedLater(candidatePublishedAt, bestAnyPublishedAt)))
                {
                    bestAny = release;
                    bestAnyVersion = candidateVersion;
                    bestAnyPublishedAt = candidatePublishedAt;
                }

                var isPrerelease = TryGetBoolean(release, "prerelease");
                if (!isPrerelease)
                {
                    if (bestStable is null ||
                        CompareVersions(candidateVersion, bestStableVersion) > 0 ||
                        (CompareVersions(candidateVersion, bestStableVersion) == 0 &&
                         IsPublishedLater(candidatePublishedAt, bestStablePublishedAt)))
                    {
                        bestStable = release;
                        bestStableVersion = candidateVersion;
                        bestStablePublishedAt = candidatePublishedAt;
                    }
                }
            }

            if (bestStable is not null)
            {
                return BuildUpdateInfoFromReleasePayload(bestStable.Value, currentNormalized, currentBuildTimestampUtc);
            }

            if (bestAny is not null)
            {
                return BuildUpdateInfoFromReleasePayload(bestAny.Value, currentNormalized, currentBuildTimestampUtc);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static AppUpdateInfo BuildUpdateInfoFromReleasePayload(
        JsonElement root,
        string currentNormalized,
        DateTimeOffset? currentBuildTimestampUtc)
    {
        var tagName = GetStringWithFallback(root, "tag_name", "tagName");
        var releaseName = GetString(root, "name");
        var releaseNotes = GetString(root, "body");
        var publishedAt = TryGetDateTimeOffsetWithFallback(root, "published_at", "publishedAt");
        var latestVersion = NormalizeVersion(tagName);
        var installerAsset = GetInstallerAssetMetadata(root);
        var installerUrl = installerAsset.DownloadUrl;
        var installerSha256 = installerAsset.Sha256;

        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            latestVersion = currentNormalized;
        }

        var updateAvailable = IsVersionGreater(latestVersion, currentNormalized);
        var sameVersionNewerBuildAvailable = !updateAvailable
            && string.Equals(latestVersion, currentNormalized, StringComparison.OrdinalIgnoreCase)
            && publishedAt.HasValue
            && currentBuildTimestampUtc.HasValue
            && publishedAt.Value > currentBuildTimestampUtc.Value.AddMinutes(5)
            && !string.IsNullOrWhiteSpace(installerUrl);

        if (sameVersionNewerBuildAvailable && !IsValidSha256(installerSha256))
        {
            sameVersionNewerBuildAvailable = false;
        }

        updateAvailable = updateAvailable || sameVersionNewerBuildAvailable;
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
                InstallerSha256 = installerSha256,
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
                InstallerSha256 = installerSha256,
                PublishedAtUtc = publishedAt,
                Message = "A newer release exists, but no installer asset was found."
            };
        }

        if (!IsValidSha256(installerSha256))
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
                InstallerSha256 = installerSha256,
                PublishedAtUtc = publishedAt,
                Message = WithCode(
                    "UPD-HASH-MISSING",
                    "A newer release exists, but SHA-256 digest metadata is missing. Auto-install is blocked for safety.")
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
            InstallerSha256 = installerSha256,
            PublishedAtUtc = publishedAt,
            Message = sameVersionNewerBuildAvailable
                ? $"A newer build for version {latestVersion} is available."
                : $"Update available: {latestVersion}"
        };
    }

    private static string WithCode(string code, string message)
    {
        return $"[{code}] {message}";
    }

    private static bool IsValidSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isHex = (character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f')
                || (character >= 'A' && character <= 'F');

            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeSha256Digest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        return normalized.ToLowerInvariant();
    }

    private static async Task<(bool Success, string Message)> VerifyInstallerHashAsync(
        string installerPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var normalizedExpected = NormalizeSha256Digest(expectedSha256);
        if (!IsValidSha256(normalizedExpected))
        {
            return (
                false,
                WithCode(
                    "UPD-HASH-MISSING",
                    "Release SHA-256 digest is missing or invalid. Auto-install is blocked for safety."));
        }

        await using var fileStream = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(fileStream, cancellationToken);
        var actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        if (!string.Equals(actualHash, normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            return (
                false,
                WithCode(
                    "UPD-HASH-MISMATCH",
                    $"Installer hash mismatch. Expected {normalizedExpected}, got {actualHash}."));
        }

        return (true, "Installer hash verified.");
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(
        string requestUri,
        HttpCompletionOption completionOption,
        string operationCodePrefix,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxHttpAttempts; attempt++)
        {
            try
            {
                var response = await _httpClient.GetAsync(requestUri, completionOption, cancellationToken);

                if (IsTransientStatusCode(response.StatusCode) && attempt < MaxHttpAttempts)
                {
                    var delayMs = GetRetryDelayMilliseconds(attempt);
                    progress?.Report(
                        WithCode(
                            $"{operationCodePrefix}-RETRY",
                            $"Transient HTTP {(int)response.StatusCode}. Retrying in {delayMs} ms."));

                    response.Dispose();
                    await Task.Delay(delayMs, cancellationToken);
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxHttpAttempts)
            {
                lastException = ex;
                var delayMs = GetRetryDelayMilliseconds(attempt);
                progress?.Report(
                    WithCode(
                        $"{operationCodePrefix}-RETRY",
                        $"Network timeout while calling update endpoint. Retrying in {delayMs} ms."));
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (Exception ex) when (IsTransientException(ex) && attempt < MaxHttpAttempts)
            {
                lastException = ex;
                var delayMs = GetRetryDelayMilliseconds(attempt);
                progress?.Report(
                    WithCode(
                        $"{operationCodePrefix}-RETRY",
                        $"Transient network error. Retrying in {delayMs} ms."));
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        throw new HttpRequestException(
            WithCode(
                $"{operationCodePrefix}-NET",
                $"Network operation failed after {MaxHttpAttempts} attempts."),
            lastException);
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode == HttpStatusCode.RequestTimeout
            || code == 429
            || code >= 500;
    }

    private static bool IsTransientException(Exception exception)
    {
        return exception is HttpRequestException
            || exception is IOException
            || exception is TaskCanceledException;
    }

    private static int GetRetryDelayMilliseconds(int attempt)
    {
        return attempt switch
        {
            1 => 900,
            2 => 1800,
            _ => 3200
        };
    }

    private static bool IsVersionGreater(string latestVersion, string currentVersion)
    {
        return CompareVersions(latestVersion, currentVersion) > 0;
    }

    private static int CompareVersions(string leftVersion, string rightVersion)
    {
        if (TryParseVersion(leftVersion, out var left) && TryParseVersion(rightVersion, out var right))
        {
            return left.CompareTo(right);
        }

        return string.Compare(leftVersion, rightVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPublishedLater(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left.HasValue && right.HasValue)
        {
            return left.Value > right.Value;
        }

        return left.HasValue && !right.HasValue;
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

        var plusIndex = normalized.IndexOf('+');
        if (plusIndex > 0)
        {
            normalized = normalized[..plusIndex];
        }

        var dashIndex = normalized.IndexOf('-');
        if (dashIndex > 0)
        {
            normalized = normalized[..dashIndex];
        }

        var numericMatch = Regex.Match(normalized, @"\d+(?:\.\d+){0,3}");
        if (numericMatch.Success)
        {
            return numericMatch.Value;
        }

        return "0.0.0";
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
