using System.Net;
using System.Net.Http;
using System.Threading;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Infrastructure.Services;
using IntuneWinPackager.Infrastructure.Support;
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
    public async Task DownloadInstallerAsync_WingetDownload_PrefersPackageInstallerOverDependency()
    {
        var packageId = $"UnitTest.AmazonGames.{Guid.NewGuid():N}";
        const string version = "3.0.9700.3";
        var workingFolder = Path.Combine(DataPathProvider.CatalogDownloadsDirectory, packageId, version);
        var dependenciesFolder = Path.Combine(workingFolder, "Dependencies");
        Directory.CreateDirectory(dependenciesFolder);

        var mainInstaller = Path.Combine(workingFolder, "Amazon Games_3.0.9700.3_User_X86_exe_en-US.exe");
        var dependencyInstaller = Path.Combine(dependenciesFolder, "Microsoft Visual C++ v14 Redistributable (x86)_14.50.35719.0_Machine_X86_burn_en-US.exe");
        File.WriteAllBytes(mainInstaller, [0x4D, 0x5A]);
        File.WriteAllBytes(dependencyInstaller, new byte[1024]);

        var processRunner = new StubProcessRunner(
        [
            new StubProcessResult(0,
            [
                "Successfully downloaded installer and dependency."
            ])
        ]);

        using var httpClient = new HttpClient(new StaticHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        var sut = new PackageCatalogService(processRunner, httpClient);
        var entry = new PackageCatalogEntry
        {
            Source = PackageCatalogSource.Winget,
            SourceDisplayName = "WinGet",
            SourceChannel = "winget",
            PackageId = packageId,
            Name = "Amazon Games",
            Version = version
        };

        var result = await sut.DownloadInstallerAsync(entry);

        try
        {
            Assert.True(result.Success, result.Message);
            Assert.Equal(mainInstaller, result.InstallerPath);
        }
        finally
        {
            TryDeleteDirectory(Path.Combine(DataPathProvider.CatalogDownloadsDirectory, packageId));
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
            SearchTerm = $"contoso-{Guid.NewGuid():N}",
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
            SearchTerm = $"app-{Guid.NewGuid():N}",
            IncludeWinget = true,
            IncludeChocolatey = false,
            IncludeGitHubReleases = false
        });

        Assert.NotEmpty(results);
        Assert.Contains(results, entry => entry.Source == PackageCatalogSource.Winget && entry.SourceChannel.Equals("winget", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results, entry => entry.Source == PackageCatalogSource.Winget && entry.SourceChannel.Equals("msstore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(results, entry => entry.Source == PackageCatalogSource.Winget && entry.SourceChannel.Equals("winget-font", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchAsync_MergesCrossSourceDuplicates_IntoCanonicalPackage()
    {
        var processRunner = new RoutingProcessRunner(request =>
        {
            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("source list", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name    Argument                                      Explicit",
                    "---------------------------------------------------------------",
                    "winget  https://cdn.winget.microsoft.com/cache        false"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("search ", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name      Id                Version Match",
                    "------------------------------------------",
                    "Spotify   Spotify.Spotify   1.2.90  Tag"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("show ", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Found Spotify [Spotify.Spotify]",
                    "Version: 1.2.90",
                    "Publisher: Spotify AB",
                    "Installer Type: exe",
                    "Installer Url: https://download.spotify.com/SpotifySetup.exe"
                ]);
            }

            return new StubProcessResult(1, []);
        });

        using var httpClient = new HttpClient(new StaticHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/Search()", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    <?xml version="1.0" encoding="utf-8"?>
                    <feed xmlns="http://www.w3.org/2005/Atom"
                          xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
                          xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
                      <entry>
                        <title type="text">Spotify</title>
                        <summary type="text">Spotify desktop app.</summary>
                        <author><name>Spotify AB</name></author>
                        <content type="application/zip" src="https://community.chocolatey.org/api/v2/package/spotify/1.2.90" />
                        <m:properties>
                          <d:Id>spotify</d:Id>
                          <d:Version>1.2.90</d:Version>
                          <d:Tags>exe music player</d:Tags>
                        </m:properties>
                      </entry>
                    </feed>
                    """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var sut = new PackageCatalogService(processRunner, httpClient);
        var results = await sut.SearchAsync(new PackageCatalogQuery
        {
            SearchTerm = $"spotify-{Guid.NewGuid():N}",
            IncludeWinget = true,
            IncludeChocolatey = true,
            IncludeGitHubReleases = false
        });

        var entry = Assert.Single(results);
        Assert.NotEmpty(entry.CanonicalPackageKey);
        Assert.True(entry.SourceVariantCount >= 2);
        Assert.True(entry.InstallerVariantCount >= 2);
        Assert.Contains(entry.InstallerVariants, variant => variant.Source == PackageCatalogSource.Winget);
        Assert.Contains(entry.InstallerVariants, variant => variant.Source == PackageCatalogSource.Chocolatey);
    }

    [Fact]
    public async Task SearchAsync_ExeVariant_WithoutNativeFootprint_LeavesDetectionManual()
    {
        var processRunner = new StubProcessRunner([]);
        using var httpClient = new HttpClient(new StaticHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/Search()", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    <?xml version="1.0" encoding="utf-8"?>
                    <feed xmlns="http://www.w3.org/2005/Atom"
                          xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
                          xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
                      <entry>
                        <title type="text">Notepad++</title>
                        <summary type="text">Notepad++ editor.</summary>
                        <author><name>Notepad++ Team</name></author>
                        <content type="application/zip" src="https://community.chocolatey.org/api/v2/package/notepadplusplus/8.7.2" />
                        <m:properties>
                          <d:Id>notepadplusplus</d:Id>
                          <d:Version>8.7.2</d:Version>
                          <d:Tags>exe editor</d:Tags>
                        </m:properties>
                      </entry>
                    </feed>
                    """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var sut = new PackageCatalogService(processRunner, httpClient);
        var results = await sut.SearchAsync(new PackageCatalogQuery
        {
            SearchTerm = $"notepad-{Guid.NewGuid():N}",
            IncludeWinget = false,
            IncludeChocolatey = true,
            IncludeGitHubReleases = false
        });

        var entry = Assert.Single(results);
        var variant = Assert.Single(entry.InstallerVariants);
        Assert.Equal(InstallerType.Exe, variant.InstallerType);
        Assert.Equal(IntuneDetectionRuleType.None, variant.DetectionRule.RuleType);
        Assert.False(variant.IsDeterministicDetection);
        Assert.Contains("Registry rejected", variant.DetectionGuidance, StringComparison.Ordinal);
        Assert.Contains("File rejected", variant.DetectionGuidance, StringComparison.Ordinal);
        Assert.Contains("Script fallback is available", variant.DetectionGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain("Script selected", variant.DetectionGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_WingetMsiVariant_PrefersMsiProductCodeDetection()
    {
        const string productCode = "{12345678-1234-1234-1234-123456789ABC}";
        var processRunner = new RoutingProcessRunner(request =>
        {
            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("source list", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name    Argument                                      Explicit",
                    "---------------------------------------------------------------",
                    "winget  https://cdn.winget.microsoft.com/cache        false"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("search ", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name        Id                  Version Match",
                    "------------------------------------------------",
                    "ContosoApp  Contoso.App.Agent   5.6.7   Tag"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("show ", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Found ContosoApp [Contoso.App.Agent]",
                    "Version: 5.6.7",
                    "Publisher: Contoso Ltd",
                    "Installer Type: msi",
                    $"Product Code: {productCode}",
                    "Installer Url: https://download.contoso.example/contosoapp-5.6.7.msi"
                ]);
            }

            return new StubProcessResult(1, []);
        });

        using var httpClient = new HttpClient(new StaticHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));

        var sut = new PackageCatalogService(processRunner, httpClient);
        var results = await sut.SearchAsync(new PackageCatalogQuery
        {
            SearchTerm = $"contoso-msi-{Guid.NewGuid():N}",
            IncludeWinget = true,
            IncludeChocolatey = false,
            IncludeGitHubReleases = false,
            IncludeScoop = false,
            IncludeNuGet = false
        });

        var entry = Assert.Single(results);
        var variant = Assert.Single(entry.InstallerVariants);
        Assert.Equal(InstallerType.Msi, variant.InstallerType);
        Assert.Equal(IntuneDetectionRuleType.MsiProductCode, variant.DetectionRule.RuleType);
        Assert.Equal(productCode, variant.DetectionRule.Msi.ProductCode);
        Assert.Equal("5.6.7", variant.DetectionRule.Msi.ProductVersion);
        Assert.True(variant.IsDeterministicDetection);
    }

    [Fact]
    public async Task SearchAsync_WingetExeVariant_WithExactUninstallKey_UsesRegistryEqualityDetection()
    {
        const string uninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Contoso Agent";
        var processRunner = new RoutingProcessRunner(request =>
        {
            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("source list", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name    Argument                                      Explicit",
                    "---------------------------------------------------------------",
                    "winget  https://cdn.winget.microsoft.com/cache        false"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("search ", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name          Id                   Version Match",
                    "--------------------------------------------------",
                    "ContosoAgent  Contoso.Agent.Setup  9.1.0   Tag"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("show ", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Found ContosoAgent [Contoso.Agent.Setup]",
                    "Version: 9.1.0",
                    "Publisher: Contoso Ltd",
                    "Installer Type: exe",
                    $"Uninstall Registry Key: {uninstallKeyPath}",
                    "Display Name: Contoso Agent",
                    "AppsAndFeaturesEntries.Publisher: Contoso Ltd",
                    "Display Version: 9.1.0",
                    "Installer Url: https://download.contoso.example/contosoagent-9.1.0.exe"
                ]);
            }

            return new StubProcessResult(1, []);
        });

        using var httpClient = new HttpClient(new StaticHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));

        var sut = new PackageCatalogService(processRunner, httpClient);
        var results = await sut.SearchAsync(new PackageCatalogQuery
        {
            SearchTerm = $"contoso-exe-{Guid.NewGuid():N}",
            IncludeWinget = true,
            IncludeChocolatey = false,
            IncludeGitHubReleases = false,
            IncludeScoop = false,
            IncludeNuGet = false
        });

        var entry = Assert.Single(results);
        var variant = Assert.Single(entry.InstallerVariants);
        Assert.Equal(InstallerType.Exe, variant.InstallerType);
        Assert.Equal(IntuneDetectionRuleType.Registry, variant.DetectionRule.RuleType);
        Assert.Equal("HKEY_LOCAL_MACHINE", variant.DetectionRule.Registry.Hive);
        Assert.Equal(uninstallKeyPath, variant.DetectionRule.Registry.KeyPath);
        Assert.Equal("DisplayVersion", variant.DetectionRule.Registry.ValueName);
        Assert.Equal(IntuneDetectionOperator.Equals, variant.DetectionRule.Registry.Operator);
        Assert.Equal("9.1.0", variant.DetectionRule.Registry.Value);
        Assert.True(variant.IsDeterministicDetection);
    }

    [Fact]
    public async Task SearchAsync_WingetExeVariant_WithoutUninstallKey_PrefersStableFileDetectionOverScript()
    {
        var processRunner = new RoutingProcessRunner(request =>
        {
            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("source list", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name    Argument                                      Explicit",
                    "---------------------------------------------------------------",
                    "winget  https://cdn.winget.microsoft.com/cache        false"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("search ", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name          Id                   Version Match",
                    "--------------------------------------------------",
                    "ContosoAgent  Contoso.Agent.Setup  9.1.0   Tag"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("show ", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Found ContosoAgent [Contoso.Agent.Setup]",
                    "Version: 9.1.0",
                    "Publisher: Contoso Ltd",
                    "Installer Type: exe",
                    "Display Name: Contoso Agent",
                    "Display Version: 9.1.0",
                    "Display Icon: \"C:\\Program Files\\Contoso Agent\\Agent.exe\",0",
                    "Installer Url: https://download.contoso.example/contoso-agent-9.1.0.exe"
                ]);
            }

            return new StubProcessResult(1, []);
        });

        using var httpClient = new HttpClient(new StaticHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));

        var sut = new PackageCatalogService(processRunner, httpClient);
        var results = await sut.SearchAsync(new PackageCatalogQuery
        {
            SearchTerm = $"contoso-file-{Guid.NewGuid():N}",
            IncludeWinget = true,
            IncludeChocolatey = false,
            IncludeGitHubReleases = false,
            IncludeScoop = false,
            IncludeNuGet = false
        });

        var entry = Assert.Single(results);
        var variant = Assert.Single(entry.InstallerVariants);
        Assert.Equal(InstallerType.Exe, variant.InstallerType);
        Assert.Equal(IntuneDetectionRuleType.File, variant.DetectionRule.RuleType);
        Assert.Equal(@"C:\Program Files\Contoso Agent", variant.DetectionRule.File.Path);
        Assert.Equal("Agent.exe", variant.DetectionRule.File.FileOrFolderName);
        Assert.Equal(IntuneDetectionOperator.Equals, variant.DetectionRule.File.Operator);
        Assert.Equal("9.1.0", variant.DetectionRule.File.Value);
        Assert.True(variant.IsDeterministicDetection);
        Assert.Contains("Registry rejected", variant.DetectionGuidance, StringComparison.Ordinal);
        Assert.Contains("File accepted", variant.DetectionGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain("Script selected", variant.DetectionGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_WingetAppxVariant_UsesExactIdentityAndVersionScriptDetection()
    {
        var processRunner = new RoutingProcessRunner(request =>
        {
            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("source list", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name    Argument                                      Explicit",
                    "---------------------------------------------------------------",
                    "winget  https://cdn.winget.microsoft.com/cache        false"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("search ", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Name        Id                    Version Match",
                    "--------------------------------------------------",
                    "ContosoApp  Contoso.App.Package   2.4.0.0 Tag"
                ]);
            }

            if (request.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("show ", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Found ContosoApp [Contoso.App.Package]",
                    "Version: 2.4.0.0",
                    "Publisher: CN=Contoso",
                    "Installer Type: msix",
                    "Package Family Name: Contoso.App",
                    "Installer Url: https://download.contoso.example/contosoapp-2.4.0.0.msix"
                ]);
            }

            return new StubProcessResult(1, []);
        });

        using var httpClient = new HttpClient(new StaticHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));

        var sut = new PackageCatalogService(processRunner, httpClient);
        var results = await sut.SearchAsync(new PackageCatalogQuery
        {
            SearchTerm = $"contoso-appx-{Guid.NewGuid():N}",
            IncludeWinget = true,
            IncludeChocolatey = false,
            IncludeGitHubReleases = false,
            IncludeScoop = false,
            IncludeNuGet = false
        });

        var entry = Assert.Single(results);
        var variant = Assert.Single(entry.InstallerVariants);
        Assert.Equal(InstallerType.AppxMsix, variant.InstallerType);
        Assert.Equal(IntuneDetectionRuleType.Script, variant.DetectionRule.RuleType);
        Assert.Contains("Contoso.App", variant.DetectionRule.Script.ScriptBody, StringComparison.Ordinal);
        Assert.Contains("2.4.0.0", variant.DetectionRule.Script.ScriptBody, StringComparison.Ordinal);
        Assert.Contains("Write-Output", variant.DetectionRule.Script.ScriptBody, StringComparison.Ordinal);
        Assert.True(variant.IsDeterministicDetection);
    }

    [Fact]
    public async Task SearchAsync_GitHubRelease_WithMultipleAssets_BuildsMultipleVariants()
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
                          "name": "fabrikam-app",
                          "full_name": "fabrikam/fabrikam-app",
                          "description": "Fabrikam desktop app",
                          "html_url": "https://github.com/fabrikam/fabrikam-app",
                          "owner": {
                            "login": "fabrikam",
                            "avatar_url": "https://avatars.githubusercontent.com/u/2?v=4"
                          }
                        }
                      ]
                    }
                    """)
                };
            }

            if (url.Contains("/repos/fabrikam/fabrikam-app/releases/latest", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "tag_name": "v3.0.1",
                      "name": "v3.0.1",
                      "published_at": "2026-04-10T08:00:00Z",
                      "assets": [
                        {
                          "name": "FabrikamApp-x64.msi",
                          "browser_download_url": "https://github.com/fabrikam/fabrikam-app/releases/download/v3.0.1/FabrikamApp-x64.msi",
                          "content_type": "application/x-msi",
                          "size": 512000
                        },
                        {
                          "name": "FabrikamApp-x64.exe",
                          "browser_download_url": "https://github.com/fabrikam/fabrikam-app/releases/download/v3.0.1/FabrikamApp-x64.exe",
                          "content_type": "application/octet-stream",
                          "size": 490000
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
            SearchTerm = $"fabrikam-{Guid.NewGuid():N}",
            IncludeWinget = false,
            IncludeChocolatey = false,
            IncludeGitHubReleases = true
        });

        var entry = Assert.Single(results);
        Assert.True(entry.InstallerVariantCount >= 2);
        Assert.Contains(entry.InstallerVariants, variant => variant.InstallerType == InstallerType.Msi);
        Assert.Contains(entry.InstallerVariants, variant => variant.InstallerType == InstallerType.Exe);
        Assert.Contains(entry.InstallerVariants, variant => variant.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchAsync_NuGetSource_ReturnsNormalizedEntry()
    {
        var processRunner = new RoutingProcessRunner(request =>
        {
            if (request.FileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("nuget list source", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Registered Sources:",
                    "  1.  nuget.org [Enabled]",
                    "      https://api.nuget.org/v3/index.json"
                ]);
            }

            return new StubProcessResult(1, []);
        });

        using var httpClient = new HttpClient(new StaticHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Equals("https://api.nuget.org/v3/index.json", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "resources": [
                        {
                          "@id": "https://azuresearch-usnc.nuget.org/query",
                          "@type": "SearchQueryService"
                        },
                        {
                          "@id": "https://api.nuget.org/v3-flatcontainer/",
                          "@type": "PackageBaseAddress/3.0.0"
                        }
                      ]
                    }
                    """)
                };
            }

            if (url.Contains("azuresearch-usnc.nuget.org/query", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": "Contoso.Tool",
                          "version": "2.1.0",
                          "description": "Contoso deployment utility",
                          "authors": "Contoso",
                          "projectUrl": "https://contoso.example/tool"
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
            SearchTerm = $"contoso-{Guid.NewGuid():N}",
            IncludeWinget = false,
            IncludeChocolatey = false,
            IncludeGitHubReleases = false,
            IncludeScoop = false,
            IncludeNuGet = true
        });

        var entry = Assert.Single(results);
        Assert.Equal(PackageCatalogSource.NuGet, entry.Source);
        Assert.Equal("nuget.org", entry.SourceChannel, ignoreCase: true);
        Assert.Equal("Contoso.Tool", entry.PackageId);
        Assert.EndsWith(".nupkg", entry.InstallerDownloadUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_NuGetResults_AreServedFromCacheWithinFreshWindow()
    {
        var nugetSearchCalls = 0;
        var processRunner = new RoutingProcessRunner(request =>
        {
            if (request.FileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("nuget list source", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Registered Sources:",
                    "  1.  nuget.org [Enabled]",
                    "      https://api.nuget.org/v3/index.json"
                ]);
            }

            return new StubProcessResult(1, []);
        });

        using var httpClient = new HttpClient(new StaticHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Equals("https://api.nuget.org/v3/index.json", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "resources": [
                        {
                          "@id": "https://azuresearch-usnc.nuget.org/query",
                          "@type": "SearchQueryService"
                        },
                        {
                          "@id": "https://api.nuget.org/v3-flatcontainer/",
                          "@type": "PackageBaseAddress/3.0.0"
                        }
                      ]
                    }
                    """)
                };
            }

            if (url.Contains("azuresearch-usnc.nuget.org/query", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref nugetSearchCalls);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": "Fabrikam.CacheProbe",
                          "version": "1.0.0",
                          "description": "Cache probe package"
                        }
                      ]
                    }
                    """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var sut = new PackageCatalogService(processRunner, httpClient);
        var searchTerm = $"cache-{Guid.NewGuid():N}";
        var query = new PackageCatalogQuery
        {
            SearchTerm = searchTerm,
            IncludeWinget = false,
            IncludeChocolatey = false,
            IncludeGitHubReleases = false,
            IncludeScoop = false,
            IncludeNuGet = true
        };

        var first = await sut.SearchAsync(query);
        var second = await sut.SearchAsync(query);

        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.Equal(1, nugetSearchCalls);
    }

    [Fact]
    public async Task SearchAsync_NuGetFailure_IsCapturedInProviderDiagnostics()
    {
        var sourceChannel = "nuget.org";
        var processRunner = new RoutingProcessRunner(request =>
        {
            if (request.FileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.StartsWith("nuget list source", StringComparison.OrdinalIgnoreCase))
            {
                return new StubProcessResult(0,
                [
                    "Registered Sources:",
                    "  1.  nuget.org [Enabled]",
                    "      https://api.nuget.org/v3/index.json"
                ]);
            }

            return new StubProcessResult(1, []);
        });

        using var httpClient = new HttpClient(new StaticHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Equals("https://api.nuget.org/v3/index.json", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "resources": [
                        {
                          "@id": "https://azuresearch-usnc.nuget.org/query",
                          "@type": "SearchQueryService"
                        },
                        {
                          "@id": "https://api.nuget.org/v3-flatcontainer/",
                          "@type": "PackageBaseAddress/3.0.0"
                        }
                      ]
                    }
                    """)
                };
            }

            if (url.Contains("azuresearch-usnc.nuget.org/query", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var sut = new PackageCatalogService(processRunner, httpClient);
        var results = await sut.SearchAsync(new PackageCatalogQuery
        {
            SearchTerm = $"diag-{Guid.NewGuid():N}",
            IncludeWinget = false,
            IncludeChocolatey = false,
            IncludeGitHubReleases = false,
            IncludeScoop = false,
            IncludeNuGet = true
        });

        Assert.Empty(results);
        var diagnostics = await sut.GetProviderDiagnosticsAsync();
        var nuget = diagnostics.FirstOrDefault(item =>
            item.ProviderId.Equals("nuget", StringComparison.OrdinalIgnoreCase) &&
            item.SourceChannel.Equals(sourceChannel, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(nuget);
        Assert.True(nuget!.TotalFailures >= 1);
        Assert.True(nuget.TotalRequests >= nuget.TotalFailures);
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
