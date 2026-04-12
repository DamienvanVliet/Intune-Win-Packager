using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Core.Services;
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
                UninstallCommand = "msiexec /x \"installer.msi\" /qn"
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
                UninstallCommand = "\"installer.exe\" /uninstall /quiet"
            }
        };

        var sut = new PreflightService(new FakeProcessRunner(0));

        var result = await sut.RunAsync(request);

        Assert.False(result.HasErrors);
        Assert.True(result.TotalCount >= 8);
        Assert.Equal(result.TotalCount, result.PassedCount + result.Checks.Count(check => !check.Passed));

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
