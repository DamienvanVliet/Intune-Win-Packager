using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Core.Utilities;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;

namespace IntuneWinPackager.Tests.Services;

public class PreflightServiceTests
{
    [Fact]
    public async Task RunAsync_ReturnsBlockingError_WhenToolPathIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "installer.msi");
        File.WriteAllText(setupFilePath, "dummy");

        var request = new PackagingRequest
        {
            IntuneWinAppUtilPath = Path.Combine(tempRoot, "missing", "IntuneWinAppUtil.exe"),
            InstallerType = InstallerType.Msi,
            Configuration = new PackageConfiguration
            {
                SourceFolder = sourceFolder,
                SetupFilePath = setupFilePath,
                OutputFolder = outputFolder,
                InstallCommand = "msiexec /i \"installer.msi\" /qn",
                UninstallCommand = "msiexec /x \"installer.msi\" /qn",
                IntuneRules = new IntuneWin32AppRules
                {
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.MsiProductCode,
                        Msi = new MsiDetectionRule
                        {
                            ProductCode = "{12345678-1234-1234-1234-123456789ABC}"
                        }
                    }
                }
            }
        };

        var sut = new PreflightService(new FakeProcessRunner(0));

        var result = await sut.RunAsync(request);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Checks, check =>
            check.Key == "tool-file" &&
            check.Severity == PreflightSeverity.Error &&
            !check.Passed);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task RunAsync_ReturnsReady_WhenConfigurationIsValid()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "installer.exe");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");
        File.WriteAllText(setupFilePath, "dummy");
        File.WriteAllText(toolPath, "dummy");

        var request = new PackagingRequest
        {
            IntuneWinAppUtilPath = toolPath,
            InstallerType = InstallerType.Exe,
            Configuration = new PackageConfiguration
            {
                SourceFolder = sourceFolder,
                SetupFilePath = setupFilePath,
                OutputFolder = outputFolder,
                InstallCommand = "\"installer.exe\" /quiet",
                UninstallCommand = "\"installer.exe\" /uninstall /quiet",
                IntuneRules = new IntuneWin32AppRules
                {
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.File,
                        File = new FileDetectionRule
                        {
                            Path = @"%ProgramFiles%\\Contoso",
                            FileOrFolderName = "ContosoAgent.exe",
                            Operator = IntuneDetectionOperator.Exists
                        }
                    }
                }
            }
        };

        var sut = new PreflightService(new FakeProcessRunner(0));

        var result = await sut.RunAsync(request);

        Assert.False(result.HasErrors);
        Assert.True(result.TotalCount >= 8);
        Assert.Equal(result.TotalCount, result.PassedCount + result.Checks.Count(check => !check.Passed));

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task RunAsync_ReturnsError_WhenOutputFolderIsInsideSourceAndSmartStagingIsDisabled()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(sourceFolder, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "installer.exe");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");
        File.WriteAllText(setupFilePath, "dummy");
        File.WriteAllText(toolPath, "dummy");

        var request = new PackagingRequest
        {
            IntuneWinAppUtilPath = toolPath,
            InstallerType = InstallerType.Exe,
            Configuration = new PackageConfiguration
            {
                SourceFolder = sourceFolder,
                SetupFilePath = setupFilePath,
                OutputFolder = outputFolder,
                UseSmartSourceStaging = false,
                InstallCommand = "\"installer.exe\" /quiet",
                UninstallCommand = "\"installer.exe\" /uninstall /quiet",
                IntuneRules = new IntuneWin32AppRules
                {
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.File,
                        File = new FileDetectionRule
                        {
                            Path = @"%ProgramFiles%\\Contoso",
                            FileOrFolderName = "installer.exe",
                            Operator = IntuneDetectionOperator.Exists
                        }
                    }
                }
            }
        };

        var sut = new PreflightService(new FakeProcessRunner(0));

        var result = await sut.RunAsync(request);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Checks, check =>
            check.Key == "output-source-overlap" &&
            check.Severity == PreflightSeverity.Error &&
            !check.Passed);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task RunAsync_ReturnsReady_ForAppxWithScriptDetection()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "sample.msix");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");
        File.WriteAllText(setupFilePath, "dummy");
        File.WriteAllText(toolPath, "dummy");

        var request = new PackagingRequest
        {
            IntuneWinAppUtilPath = toolPath,
            InstallerType = InstallerType.AppxMsix,
            Configuration = new PackageConfiguration
            {
                SourceFolder = sourceFolder,
                SetupFilePath = setupFilePath,
                OutputFolder = outputFolder,
                InstallCommand = "powershell.exe -ExecutionPolicy Bypass -Command \"Add-AppxPackage -Path \\\"sample.msix\\\"\"",
                UninstallCommand = "powershell.exe -ExecutionPolicy Bypass -Command \"Get-AppxPackage -Name 'Contoso.App' | Remove-AppxPackage\"",
                IntuneRules = new IntuneWin32AppRules
                {
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Script,
                        Script = new ScriptDetectionRule
                        {
                            ScriptBody = "$pkg = Get-AppxPackage -Name 'Contoso.App' -ErrorAction SilentlyContinue | Where-Object { $_.Version.ToString() -eq '1.2.3.4' }\nif ($null -ne $pkg) { exit 0 }\nexit 1"
                        }
                    }
                }
            }
        };

        var sut = new PreflightService(new FakeProcessRunner(0));

        var result = await sut.RunAsync(request);

        Assert.False(result.HasErrors);
        Assert.Contains(result.Checks, check => check.Key == "detection-script" && check.Passed);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task RunAsync_ReturnsError_WhenScriptDetectionIsUsedForExe()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "installer.exe");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");
        File.WriteAllText(setupFilePath, "dummy");
        File.WriteAllText(toolPath, "dummy");

        var request = new PackagingRequest
        {
            IntuneWinAppUtilPath = toolPath,
            InstallerType = InstallerType.Exe,
            Configuration = new PackageConfiguration
            {
                SourceFolder = sourceFolder,
                SetupFilePath = setupFilePath,
                OutputFolder = outputFolder,
                InstallCommand = "\"installer.exe\" /S",
                UninstallCommand = "\"installer.exe\" /S",
                IntuneRules = new IntuneWin32AppRules
                {
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Script,
                        Script = new ScriptDetectionRule
                        {
                            ScriptBody = "if (Test-Path 'C:\\Program Files\\Contoso') { exit 0 } exit 1"
                        }
                    }
                }
            }
        };

        var sut = new PreflightService(new FakeProcessRunner(0));
        var result = await sut.RunAsync(request);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Checks, check => check.Key == "detection-script-exe-deterministic" && !check.Passed);

        Directory.Delete(tempRoot, recursive: true);
    }


    [Fact]
    public async Task RunAsync_ReturnsReady_WhenExeUsesDeterministicExactRegistryScript()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "installer.exe");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");
        File.WriteAllText(setupFilePath, "dummy");
        File.WriteAllText(toolPath, "dummy");

        var request = new PackagingRequest
        {
            IntuneWinAppUtilPath = toolPath,
            InstallerType = InstallerType.Exe,
            Configuration = new PackageConfiguration
            {
                SourceFolder = sourceFolder,
                SetupFilePath = setupFilePath,
                OutputFolder = outputFolder,
                InstallCommand = "\"installer.exe\" /S",
                UninstallCommand = "\"installer.exe\" /S",
                IntuneRules = new IntuneWin32AppRules
                {
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Script,
                        Script = new ScriptDetectionRule
                        {
                            ScriptBody = DeterministicDetectionScript.BuildExactExeRegistryScript(
                                "Contoso Agent",
                                "Contoso Ltd",
                                "5.4.3")
                        }
                    }
                }
            }
        };

        var sut = new PreflightService(new FakeProcessRunner(0));
        var result = await sut.RunAsync(request);

        Assert.False(result.HasErrors);
        Assert.DoesNotContain(result.Checks, check => check.Key == "detection-script-exe-deterministic" && !check.Passed);

        Directory.Delete(tempRoot, recursive: true);
    }
    [Fact]
    public async Task RunAsync_ReturnsError_WhenRequirementScriptContainsPlaceholder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "installer.exe");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");
        File.WriteAllText(setupFilePath, "dummy");
        File.WriteAllText(toolPath, "dummy");

        var request = new PackagingRequest
        {
            IntuneWinAppUtilPath = toolPath,
            InstallerType = InstallerType.Exe,
            Configuration = new PackageConfiguration
            {
                SourceFolder = sourceFolder,
                SetupFilePath = setupFilePath,
                OutputFolder = outputFolder,
                InstallCommand = "\"installer.exe\" /S",
                UninstallCommand = "\"installer.exe\" /S",
                IntuneRules = new IntuneWin32AppRules
                {
                    Requirements = new IntuneRequirementRules
                    {
                        OperatingSystemArchitecture = "x64",
                        MinimumOperatingSystem = "Windows 10 22H2",
                        RequirementScriptBody = "<requirement-script>"
                    },
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.File,
                        File = new FileDetectionRule
                        {
                            Path = @"%ProgramFiles%\\Contoso",
                            FileOrFolderName = "installer.exe",
                            Operator = IntuneDetectionOperator.Exists
                        }
                    }
                }
            }
        };

        var sut = new PreflightService(new FakeProcessRunner(0));

        var result = await sut.RunAsync(request);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Checks, check =>
            check.Key == "requirement-script" &&
            check.Severity == PreflightSeverity.Error &&
            !check.Passed);

        Directory.Delete(tempRoot, recursive: true);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly int _exitCode;

        public FakeProcessRunner(int exitCode)
        {
            _exitCode = exitCode;
        }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            IProgress<ProcessOutputLine>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ProcessRunResult
            {
                ExitCode = _exitCode,
                TimedOut = false
            });
        }
    }
}

