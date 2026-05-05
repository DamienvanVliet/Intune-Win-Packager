using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;

namespace IntuneWinPackager.Tests.Services;

public class PackagingWorkflowServiceTests
{
    [Fact]
    public async Task PackageAsync_ReturnsSuccess_WhenProcessSucceedsAndOutputExists()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "ClientSetup.exe");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");
        var expectedOutput = Path.Combine(outputFolder, "ClientSetup.intunewin");

        File.WriteAllText(setupFilePath, "dummy");
        File.WriteAllText(toolPath, "dummy");
        File.WriteAllText(expectedOutput, "dummy");

        var request = new PackagingRequest
        {
            IntuneWinAppUtilPath = toolPath,
            InstallerType = InstallerType.Exe,
            Configuration = new PackageConfiguration
            {
                SourceFolder = sourceFolder,
                SetupFilePath = setupFilePath,
                OutputFolder = outputFolder,
                InstallCommand = "\"ClientSetup.exe\" /quiet",
                UninstallCommand = "\"ClientSetup.exe\" /uninstall /quiet",
                IntuneRules = new IntuneWin32AppRules
                {
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.File,
                        File = new FileDetectionRule
                        {
                            Path = @"%ProgramFiles%\\Contoso",
                            FileOrFolderName = "ClientSetup.exe",
                            Operator = IntuneDetectionOperator.Exists
                        }
                    }
                }
            }
        };

        var sut = new PackagingWorkflowService(
            new PackagingValidationService(),
            new FakeProcessRunner(exitCode: 0));

        var result = await sut.PackageAsync(request);

        Assert.True(result.Success);
        Assert.Equal(expectedOutput, result.OutputPackagePath);
        Assert.False(string.IsNullOrWhiteSpace(result.OutputMetadataPath));
        Assert.True(File.Exists(result.OutputMetadataPath));
        var metadataJson = await File.ReadAllTextAsync(result.OutputMetadataPath);
        Assert.Contains("\"hardLinkedFileCount\"", metadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(result.OutputChecklistPath));
        Assert.True(File.Exists(result.OutputChecklistPath));
        Assert.Contains("Manual Portal Steps", result.IntunePortalChecklist, StringComparison.Ordinal);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task PackageAsync_ReturnsFailure_WhenOutputIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "ClientSetup.exe");
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
                InstallCommand = "\"ClientSetup.exe\" /quiet",
                UninstallCommand = "\"ClientSetup.exe\" /uninstall /quiet",
                IntuneRules = new IntuneWin32AppRules
                {
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.File,
                        File = new FileDetectionRule
                        {
                            Path = @"%ProgramFiles%\\Contoso",
                            FileOrFolderName = "ClientSetup.exe",
                            Operator = IntuneDetectionOperator.Exists
                        }
                    }
                }
            }
        };

        var sut = new PackagingWorkflowService(
            new PackagingValidationService(),
            new FakeProcessRunner(exitCode: 0));

        var result = await sut.PackageAsync(request);

        Assert.False(result.Success);
        Assert.Contains("no .intunewin", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.OutputMetadataPath);
        Assert.Null(result.OutputChecklistPath);
        Assert.Equal(string.Empty, result.IntunePortalChecklist);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task PackageAsync_UsesSetupFileNameOnly_InIntuneWinAppUtilArguments()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var nestedFolder = Path.Combine(sourceFolder, "nested");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(nestedFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(nestedFolder, "ClientSetup.exe");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");
        var expectedOutput = Path.Combine(outputFolder, "ClientSetup.intunewin");

        await File.WriteAllTextAsync(setupFilePath, "dummy");
        await File.WriteAllTextAsync(toolPath, "dummy");
        await File.WriteAllTextAsync(expectedOutput, "dummy output payload");

        var request = BuildValidRequest(toolPath, sourceFolder, setupFilePath, outputFolder);
        var fakeRunner = new CapturingProcessRunner(exitCode: 0);
        var sut = new PackagingWorkflowService(new PackagingValidationService(), fakeRunner);

        var result = await sut.PackageAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(fakeRunner.LastRequest);
        Assert.Contains("-s \"ClientSetup.exe\"", fakeRunner.LastRequest!.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(setupFilePath, fakeRunner.LastRequest.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-c \"", fakeRunner.LastRequest.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain($"-c \"{sourceFolder}\"", fakeRunner.LastRequest.Arguments, StringComparison.OrdinalIgnoreCase);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task PackageAsync_ReturnsFailure_WhenOutputFolderIsInsideSourceFolder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(sourceFolder, "out");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "ClientSetup.exe");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");

        await File.WriteAllTextAsync(setupFilePath, "dummy");
        await File.WriteAllTextAsync(toolPath, "dummy");

        var request = BuildValidRequest(toolPath, sourceFolder, setupFilePath, outputFolder);
        var sut = new PackagingWorkflowService(
            new PackagingValidationService(),
            new CapturingProcessRunner(exitCode: 0));

        var result = await sut.PackageAsync(request);

        Assert.False(result.Success);
        Assert.Contains("Output folder cannot be inside source folder", result.Message, StringComparison.OrdinalIgnoreCase);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task PackageAsync_ReturnsFailure_WhenOutputPackageIsSuspiciouslySmall()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "ClientSetup.exe");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");
        var expectedOutput = Path.Combine(outputFolder, "ClientSetup.intunewin");

        await File.WriteAllBytesAsync(setupFilePath, new byte[230 * 1024]);
        await File.WriteAllTextAsync(toolPath, "dummy");
        await File.WriteAllBytesAsync(expectedOutput, new byte[2 * 1024]);

        var request = BuildValidRequest(toolPath, sourceFolder, setupFilePath, outputFolder);
        var sut = new PackagingWorkflowService(
            new PackagingValidationService(),
            new CapturingProcessRunner(exitCode: 0));

        var result = await sut.PackageAsync(request);

        Assert.False(result.Success);
        Assert.Contains("suspiciously small", result.Message, StringComparison.OrdinalIgnoreCase);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task PackageAsync_IgnoresStaleExpectedOutputArtifact()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "ClientSetup.exe");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");
        var staleOutput = Path.Combine(outputFolder, "ClientSetup.intunewin");

        await File.WriteAllBytesAsync(setupFilePath, new byte[120 * 1024]);
        await File.WriteAllTextAsync(toolPath, "dummy");
        await File.WriteAllBytesAsync(staleOutput, new byte[110 * 1024]);
        File.SetLastWriteTimeUtc(staleOutput, DateTime.UtcNow.AddDays(-2));

        var request = BuildValidRequest(toolPath, sourceFolder, setupFilePath, outputFolder);
        var sut = new PackagingWorkflowService(
            new PackagingValidationService(),
            new CapturingProcessRunner(exitCode: 0));

        var result = await sut.PackageAsync(request);

        Assert.False(result.Success);
        Assert.Contains("no .intunewin", result.Message, StringComparison.OrdinalIgnoreCase);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task PackageAsync_UsesUniqueSetupAlias_WhenRootFileNameCollidesInStaging()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var nestedFolder = Path.Combine(sourceFolder, "nested");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(nestedFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(nestedFolder, "ClientSetup.exe");
        var collidingRootFilePath = Path.Combine(sourceFolder, "ClientSetup.exe");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");
        var outputPackage = Path.Combine(outputFolder, "generated.intunewin");

        await File.WriteAllBytesAsync(setupFilePath, new byte[180 * 1024]);
        await File.WriteAllTextAsync(collidingRootFilePath, "different-root-file");
        await File.WriteAllTextAsync(toolPath, "dummy");
        await File.WriteAllBytesAsync(outputPackage, new byte[150 * 1024]);

        var request = BuildValidRequest(toolPath, sourceFolder, setupFilePath, outputFolder);
        var fakeRunner = new CapturingProcessRunner(exitCode: 0);
        var sut = new PackagingWorkflowService(new PackagingValidationService(), fakeRunner);

        var result = await sut.PackageAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(fakeRunner.LastRequest);
        Assert.Contains("__iwp__", fakeRunner.LastRequest!.Arguments, StringComparison.OrdinalIgnoreCase);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task PackageAsync_DoesNotBlock_WhenScriptReferenceUsesRuntimeEnvironmentVariables()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "ClientSetup.exe");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");
        var expectedOutput = Path.Combine(outputFolder, "ClientSetup.intunewin");

        await File.WriteAllBytesAsync(setupFilePath, new byte[128 * 1024]);
        await File.WriteAllTextAsync(toolPath, "dummy");
        await File.WriteAllBytesAsync(expectedOutput, new byte[96 * 1024]);

        var request = BuildValidRequest(
            toolPath,
            sourceFolder,
            setupFilePath,
            outputFolder,
            "powershell.exe -ExecutionPolicy Bypass -File \"%TEMP%\\InstallClient.ps1\"",
            "powershell.exe -ExecutionPolicy Bypass -File \"$env:TEMP\\UninstallClient.ps1\"");

        var sut = new PackagingWorkflowService(
            new PackagingValidationService(),
            new CapturingProcessRunner(exitCode: 0));

        var result = await sut.PackageAsync(request);

        Assert.True(result.Success);

        Directory.Delete(tempRoot, recursive: true);
    }

    private static PackagingRequest BuildValidRequest(
        string toolPath,
        string sourceFolder,
        string setupFilePath,
        string outputFolder,
        string installCommand = "\"ClientSetup.exe\" /quiet",
        string uninstallCommand = "\"ClientSetup.exe\" /uninstall /quiet")
    {
        return new PackagingRequest
        {
            IntuneWinAppUtilPath = toolPath,
            InstallerType = InstallerType.Exe,
            Configuration = new PackageConfiguration
            {
                SourceFolder = sourceFolder,
                SetupFilePath = setupFilePath,
                OutputFolder = outputFolder,
                InstallCommand = installCommand,
                UninstallCommand = uninstallCommand,
                IntuneRules = new IntuneWin32AppRules
                {
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.File,
                        File = new FileDetectionRule
                        {
                            Path = @"%ProgramFiles%\\Contoso",
                            FileOrFolderName = "ClientSetup.exe",
                            Operator = IntuneDetectionOperator.Exists
                        }
                    }
                }
            }
        };
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
            outputProgress?.Report(new ProcessOutputLine
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Severity = LogSeverity.Info,
                Text = "Fake process execution"
            });

            return Task.FromResult(new ProcessRunResult
            {
                ExitCode = _exitCode,
                TimedOut = false
            });
        }
    }

    private sealed class CapturingProcessRunner : IProcessRunner
    {
        private readonly int _exitCode;

        public CapturingProcessRunner(int exitCode)
        {
            _exitCode = exitCode;
        }

        public ProcessRunRequest? LastRequest { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            IProgress<ProcessOutputLine>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            outputProgress?.Report(new ProcessOutputLine
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Severity = LogSeverity.Info,
                Text = "Fake process execution"
            });

            return Task.FromResult(new ProcessRunResult
            {
                ExitCode = _exitCode,
                TimedOut = false
            });
        }
    }
}
