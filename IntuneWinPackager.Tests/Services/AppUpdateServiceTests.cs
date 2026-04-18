using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using IntuneWinPackager.Infrastructure.Services;

namespace IntuneWinPackager.Tests.Services;

public class AppUpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsUpdate_WhenHigherVersionExists()
    {
        var payload = """
                      [
                        {
                          "tag_name": "v1.1.6",
                          "draft": false,
                          "prerelease": false,
                          "published_at": "2026-04-13T12:00:00Z",
                          "assets": [
                            {
                              "name": "IntuneWinPackager-Setup-1.1.6.exe",
                              "browser_download_url": "https://example.com/iwp-1.1.6.exe",
                              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                            }
                          ]
                        }
                      ]
                      """;

        var sut = CreateService(payload);

        var result = await sut.CheckForUpdatesAsync("1.1.5");

        Assert.True(result.CheckSucceeded);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.1.6", result.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsUpdate_WhenSameVersionButNewerBuildPublished()
    {
        var payload = """
                      [
                        {
                          "tag_name": "v1.1.5",
                          "draft": false,
                          "prerelease": false,
                          "published_at": "2026-04-13T12:00:00Z",
                          "assets": [
                            {
                              "name": "IntuneWinPackager-Setup-1.1.5.exe",
                              "browser_download_url": "https://example.com/iwp-1.1.5.exe",
                              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                            }
                          ]
                        }
                      ]
                      """;

        var sut = CreateService(payload);
        var currentBuildTimestamp = new DateTimeOffset(2026, 4, 10, 9, 0, 0, TimeSpan.Zero);

        var result = await sut.CheckForUpdatesAsync("1.1.5", currentBuildTimestamp);

        Assert.True(result.CheckSucceeded);
        Assert.True(result.IsUpdateAvailable);
        Assert.Contains("newer build", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsLatest_WhenSameVersionAndNoNewBuild()
    {
        var payload = """
                      [
                        {
                          "tag_name": "v1.1.5",
                          "draft": false,
                          "prerelease": false,
                          "published_at": "2026-04-13T12:00:00Z",
                          "assets": [
                            {
                              "name": "IntuneWinPackager-Setup-1.1.5.exe",
                              "browser_download_url": "https://example.com/iwp-1.1.5.exe",
                              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                            }
                          ]
                        }
                      ]
                      """;

        var sut = CreateService(payload);
        var currentBuildTimestamp = new DateTimeOffset(2026, 4, 13, 12, 1, 0, TimeSpan.Zero);

        var result = await sut.CheckForUpdatesAsync("1.1.5", currentBuildTimestamp);

        Assert.True(result.CheckSucceeded);
        Assert.False(result.IsUpdateAvailable);
        Assert.Contains("latest version", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_HandlesCurrentVersionWithBuildMetadata()
    {
        var payload = """
                      [
                        {
                          "tag_name": "v1.1.10",
                          "draft": false,
                          "prerelease": false,
                          "published_at": "2026-04-13T12:00:00Z",
                          "assets": [
                            {
                              "name": "IntuneWinPackager-Setup-1.1.10.exe",
                              "browser_download_url": "https://example.com/iwp-1.1.10.exe",
                              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                            }
                          ]
                        }
                      ]
                      """;

        var sut = CreateService(payload);

        var result = await sut.CheckForUpdatesAsync("1.1.5+build.42");

        Assert.True(result.CheckSucceeded);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.1.10", result.LatestVersion);
        Assert.Equal("1.1.5", result.CurrentVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SkipsNonInstallableNewerRelease_WhenInstallableReleaseExists()
    {
        var payload = """
                      [
                        {
                          "tag_name": "v1.1.40",
                          "draft": false,
                          "prerelease": false,
                          "published_at": "2026-04-18T12:00:00Z",
                          "assets": []
                        },
                        {
                          "tag_name": "v1.1.39",
                          "draft": false,
                          "prerelease": false,
                          "published_at": "2026-04-18T10:00:00Z",
                          "assets": [
                            {
                              "name": "IntuneWinPackager-Setup-1.1.39.exe",
                              "browser_download_url": "https://example.com/iwp-1.1.39.exe",
                              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                            }
                          ]
                        }
                      ]
                      """;

        var sut = CreateService(payload);

        var result = await sut.CheckForUpdatesAsync("1.1.38");

        Assert.True(result.CheckSucceeded);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.1.39", result.LatestVersion);
        Assert.Contains("1.1.39", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_NormalizesEscapedNewLinesInReleaseNotes()
    {
        var payload = """
                      [
                        {
                          "tag_name": "v1.1.8",
                          "name": "v1.1.8",
                          "body": "Line 1\\n\\n- Item A\\n- Item B",
                          "draft": false,
                          "prerelease": false,
                          "published_at": "2026-04-13T12:00:00Z",
                          "assets": [
                            {
                              "name": "IntuneWinPackager-Setup-1.1.8.exe",
                              "browser_download_url": "https://example.com/iwp-1.1.8.exe",
                              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                            }
                          ]
                        }
                      ]
                      """;

        var sut = CreateService(payload);

        var result = await sut.CheckForUpdatesAsync("1.1.7");

        Assert.True(result.CheckSucceeded);
        Assert.True(result.IsUpdateAvailable);
        Assert.Contains("Line 1", result.ReleaseNotes, StringComparison.Ordinal);
        Assert.Contains('\n', result.ReleaseNotes);
        Assert.DoesNotContain("\\n", result.ReleaseNotes, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredLauncherBatch_WaitsForProcessUnlockBeforeStartingInstaller()
    {
        var buildBatchMethod = typeof(AppUpdateService).GetMethod(
            "BuildDeferredInstallerBatch",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildBatchMethod);

        var script = (string?)buildBatchMethod!.Invoke(
            null,
            new object?[]
            {
                1234,
                "IntuneWinPackager.App",
                @"C:\Program Files\Intune Win Packager\IntuneWinPackager.App.exe",
                @"C:\Temp\IntuneWinPackager-Setup-1.1.36.exe",
                "/NORESTART /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS",
                @"C:\Temp\launch-update.started"
            });

        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.Contains("TARGET_EXE_PATH=", script, StringComparison.Ordinal);
        Assert.Contains("LAUNCH_MARKER=", script, StringComparison.Ordinal);
        Assert.Contains("launcher_started", script, StringComparison.Ordinal);
        Assert.Contains("WAIT_PID_RETRIES_MAX=180", script, StringComparison.Ordinal);
        Assert.Contains("WAIT_UNLOCK_RETRIES_MAX=180", script, StringComparison.Ordinal);
        Assert.Contains("goto wait_unlock_retry", script, StringComparison.Ordinal);
        Assert.Contains(":wait_unlock", script, StringComparison.Ordinal);
        Assert.Contains("Test-Path -LiteralPath", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::Open", script, StringComparison.Ordinal);
        Assert.DoesNotContain(":wait_image", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredLauncherPowerShellCommand_WaitsForPidAndUnlockBeforeStartingInstaller()
    {
        var buildPowerShellMethod = typeof(AppUpdateService).GetMethod(
            "BuildDeferredInstallerPowerShellCommand",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(buildPowerShellMethod);

        var command = (string?)buildPowerShellMethod!.Invoke(
            null,
            new object?[]
            {
                5678,
                @"C:\Program Files\Intune Win Packager\IntuneWinPackager.App.exe",
                @"C:\Temp\IntuneWinPackager-Setup-1.1.40.exe",
                "/NORESTART /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS"
            });

        Assert.False(string.IsNullOrWhiteSpace(command));
        Assert.Contains("$targetPid=5678", command, StringComparison.Ordinal);
        Assert.Contains("Get-Process -Id $targetPid", command, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::Open", command, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath $installerPath", command, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredAppHostScheduling_ReturnsFalseWhenProcessPathIsInvalid()
    {
        var scheduleWithAppHostMethod = typeof(AppUpdateService).GetMethod(
            "TryScheduleInstallerAfterCurrentProcessExitWithAppHost",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(scheduleWithAppHostMethod);

        var parameters = new object?[]
        {
            1234,
            @"C:\this\path\does-not-exist\IntuneWinPackager.App.exe",
            @"C:\Temp\IntuneWinPackager-Setup-1.1.40.exe",
            "/NORESTART /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS",
            null
        };

        var scheduled = (bool?)scheduleWithAppHostMethod!.Invoke(null, parameters);
        var error = parameters[4] as string;

        Assert.False(scheduled);
        Assert.Contains("executable path", error, StringComparison.OrdinalIgnoreCase);
    }

    private static AppUpdateService CreateService(string releasesPayload)
    {
        var handler = new StubHttpMessageHandler(releasesPayload);
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("IntuneWinPackager.Tests");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return new AppUpdateService(client);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _releasesPayload;

        public StubHttpMessageHandler(string releasesPayload)
        {
            _releasesPayload = releasesPayload;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;

            if (uri.Contains("/releases?per_page=20", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(_releasesPayload));
            }

            if (uri.Contains("/releases/latest", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
