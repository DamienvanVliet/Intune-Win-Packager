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
                UninstallCommand = "\"ClientSetup.exe\" /uninstall /quiet"
            }
        };

        var sut = new PackagingWorkflowService(
            new PackagingValidationService(),
            new FakeProcessRunner(exitCode: 0));

        var result = await sut.PackageAsync(request);

        Assert.True(result.Success);
        Assert.Equal(expectedOutput, result.OutputPackagePath);

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
                UninstallCommand = "\"ClientSetup.exe\" /uninstall /quiet"
            }
        };

        var sut = new PackagingWorkflowService(
            new PackagingValidationService(),
            new FakeProcessRunner(exitCode: 0));

        var result = await sut.PackageAsync(request);

        Assert.False(result.Success);
        Assert.Contains("no .intunewin", result.Message, StringComparison.OrdinalIgnoreCase);

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
