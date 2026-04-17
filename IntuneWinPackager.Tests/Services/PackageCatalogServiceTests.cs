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

        public StubProcessRunner(IEnumerable<StubProcessResult> results)
        {
            _results = new Queue<StubProcessResult>(results);
        }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            IProgress<ProcessOutputLine>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No stubbed process result is available.");
            }

            var result = _results.Dequeue();
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
