using System.Net;
using System.Net.Http;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Infrastructure.Services;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;

namespace IntuneWinPackager.Tests.Services;

public sealed class PackageCatalogServiceTests
{
    [Fact]
    public async Task DownloadInstallerAsync_WingetHashMismatch_UsesDirectUrlFallback()
    {
        const string installerUrl = "https://download.example.test/SpotifyFullSetupX64.exe";
        var processRunner = new StubProcessRunner(
        [
            new StubProcessResult(1,
            [
                "Found Spotify [Spotify.Spotify] Version 1.2.87.414.g4e7a1155",
                $"Downloading {installerUrl}",
                "Installer hash does not match."
            ]),
            new StubProcessResult(0,
            [
                "Found Spotify [Spotify.Spotify]",
                "Version: 1.2.87.414.g4e7a1155",
                "Installer:",
                "  No applicable installer found; see logs for more details."
            ])
        ]);

        using var httpClient = new HttpClient(new StaticHttpMessageHandler(request =>
        {
            if (request.RequestUri?.ToString().Equals(installerUrl, StringComparison.OrdinalIgnoreCase) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00])
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var sut = new PackageCatalogService(processRunner, httpClient);
        var entry = new PackageCatalogEntry
        {
            Source = PackageCatalogSource.Winget,
            SourceDisplayName = "WinGet",
            PackageId = $"UnitTest.SpotifyFallback.{Guid.NewGuid():N}",
            Name = "Spotify",
            Version = "1.2.87"
        };

        var result = await sut.DownloadInstallerAsync(entry);

        try
        {
            Assert.True(result.Success, result.Message);
            Assert.False(result.HashVerifiedBySource);
            Assert.Contains("output URL", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not verified", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".exe", result.InstallerPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.InstallerPath));
        }
        finally
        {
            TryDeleteDirectory(result.WorkingFolderPath);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_WingetHashMismatchWithoutUrl_ReturnsClearFailureMessage()
    {
        var processRunner = new StubProcessRunner(
        [
            new StubProcessResult(1,
            [
                "Found Spotify [Spotify.Spotify] Version 1.2.87.414.g4e7a1155",
                "Installer hash does not match."
            ]),
            new StubProcessResult(0,
            [
                "Found Spotify [Spotify.Spotify]",
                "Installer:",
                "  No applicable installer found; see logs for more details."
            ])
        ]);

        using var httpClient = new HttpClient(new StaticHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        var sut = new PackageCatalogService(processRunner, httpClient);
        var entry = new PackageCatalogEntry
        {
            Source = PackageCatalogSource.Winget,
            SourceDisplayName = "WinGet",
            PackageId = $"UnitTest.NoUrl.{Guid.NewGuid():N}",
            Name = "Spotify",
            Version = "1.2.87"
        };

        var result = await sut.DownloadInstallerAsync(entry);

        try
        {
            Assert.False(result.Success);
            Assert.Contains("no installer url metadata", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(result.WorkingFolderPath);
        }
    }

    [Fact]
    public async Task SearchAsync_GitHubReleasesSource_ReturnsReleaseInstallerEntry()
    {
        var processRunner = new StubProcessRunner([]);
        using var httpClient = new HttpClient(new StaticHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/search/repositories", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "items": [
                        {
                          "name": "contoso-app",
                          "full_name": "contoso/contoso-app",
                          "description": "Contoso desktop app",
                          "html_url": "https://github.com/contoso/contoso-app",
                          "owner": {
                            "login": "contoso",
                            "avatar_url": "https://avatars.githubusercontent.com/u/1?v=4"
                          }
                        }
                      ]
                    }
                    """)
                };
            }

            if (url.Contains("/repos/contoso/contoso-app/releases/latest", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "tag_name": "v2.5.0",
                      "name": "v2.5.0",
                      "published_at": "2026-04-01T10:00:00Z",
                      "assets": [
                        {
                          "name": "ContosoApp-x64.msi",
                          "browser_download_url": "https://github.com/contoso/contoso-app/releases/download/v2.5.0/ContosoApp-x64.msi",
                          "content_type": "application/x-msi",
                          "size": 424242
                        }
                      ]
                    }
                    """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var sut = new PackageCatalogService(processRunner, httpClient);
        var results = await sut.SearchAsync(new PackageCatalogQuery
        {
            SearchTerm = "contoso",
            IncludeWinget = false,
            IncludeChocolatey = false,
            IncludeGitHubReleases = true
        });

        var entry = Assert.Single(results);
        Assert.Equal(PackageCatalogSource.GitHubReleases, entry.Source);
        Assert.Equal("contoso/contoso-app", entry.PackageId);
        Assert.Equal("contoso/contoso-app", entry.SourceChannel);
        Assert.Equal("2.5.0", entry.Version);
        Assert.Equal(InstallerType.Msi, entry.InstallerType);
        Assert.Contains(".msi", entry.InstallerDownloadUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_WingetSourceList_UsesConfiguredNonExplicitSources()
    {
        var processRunner = new RoutingProcessRunner(request =>
        {
            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("source list", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name        Argument                                      Explicit",
                    "------------------------------------------------------------------",
                    "msstore     https://storeedgefd.dsx.mp.microsoft.com/v9.0  false",
                    "winget      https://cdn.winget.microsoft.com/cache        false",
                    "winget-font https://cdn.winget.microsoft.com/fonts        true"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("search ", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.Contains("--source winget", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name      Id                         Version Match",
                    "----------------------------------------------------",
                    "App One   Vendor.AppOne              1.0.0   Tag"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("search ", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.Contains("--source msstore", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name       Id                         Version Match",
                    "----------------------------------------------------",
                    "Store App  Vendor.StoreApp            4.2.0   Tag"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("show ", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(1, []);
            }

            return new StubProcessResult(1, []);
        });

        using var httpClient = new HttpClient(new StaticHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));

        var sut = new PackageCatalogService(processRunner, httpClient);
        var results = await sut.SearchAsync(new PackageCatalogQuery
        {
            SearchTerm = "app",
            IncludeWinget = true,
            IncludeChocolatey = false,
            IncludeGitHubReleases = false
        });

        Assert.NotEmpty(results);
        Assert.Contains(results, entry => entry.Source == PackageCatalogSource.Winget && entry.SourceChannel.Equals("winget", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results, entry => entry.Source == PackageCatalogSource.Winget && entry.SourceChannel.Equals("msstore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(results, entry => entry.Source == PackageCatalogSource.Winget && entry.SourceChannel.Equals("winget-font", StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }

    private sealed record StubProcessResult(int ExitCode, IReadOnlyList<string> Lines);

    private sealed class StubProcessRunner : IProcessRunner
    {
        private readonly Queue<StubProcessResult> _results;
        private readonly object _gate = new();

        public StubProcessRunner(IEnumerable<StubProcessResult> results)
        {
            _results = new Queue<StubProcessResult>(results);
        }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            IProgress<ProcessOutputLine>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            StubProcessResult result;
            lock (_gate)
            {
                if (_results.Count == 0)
                {
                    throw new InvalidOperationException("No stubbed process result is available.");
                }

                result = _results.Dequeue();
            }

            foreach (var line in result.Lines)
            {
                outputProgress?.Report(new ProcessOutputLine
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Severity = LogSeverity.Info,
                    Text = line
                });
            }

            return Task.FromResult(new ProcessRunResult
            {
                ExitCode = result.ExitCode,
                TimedOut = false
            });
        }
    }

    private sealed class RoutingProcessRunner : IProcessRunner
    {
        private readonly Func<ProcessRunRequest, StubProcessResult> _resolver;

        public RoutingProcessRunner(Func<ProcessRunRequest, StubProcessResult> resolver)
        {
            _resolver = resolver;
        }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            IProgress<ProcessOutputLine>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            var result = _resolver(request);
            foreach (var line in result.Lines)
            {
                outputProgress?.Report(new ProcessOutputLine
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Severity = LogSeverity.Info,
                    Text = line
                });
            }

            return Task.FromResult(new ProcessRunResult
            {
                ExitCode = result.ExitCode,
                TimedOut = false
            });
        }
    }

    private sealed class StaticHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StaticHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}
