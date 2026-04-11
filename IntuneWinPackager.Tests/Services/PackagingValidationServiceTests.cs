using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Tests.Services;

public class PackagingValidationServiceTests
{
    [Fact]
    public void Validate_ReturnsErrors_WhenSetupFileIsOutsideSourceFolder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var sourceFolder = Path.Combine(tempRoot, "source");
        var outsideFolder = Path.Combine(tempRoot, "outside");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outsideFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(outsideFolder, "installer.exe");
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

        var sut = new PackagingValidationService();

        var result = sut.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, message =>
            message.Contains("inside the selected source folder", StringComparison.OrdinalIgnoreCase));

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public void Validate_ReturnsSuccess_WhenAllRequiredFieldsAreValid()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "app.msi");
        var toolPath = Path.Combine(tempRoot, "IntuneWinAppUtil.exe");

        File.WriteAllText(setupFilePath, "dummy");
        File.WriteAllText(toolPath, "dummy");

        var request = new PackagingRequest
        {
            IntuneWinAppUtilPath = toolPath,
            InstallerType = InstallerType.Msi,
            Configuration = new PackageConfiguration
            {
                SourceFolder = sourceFolder,
                SetupFilePath = setupFilePath,
                OutputFolder = outputFolder,
                InstallCommand = "msiexec /i \"app.msi\" /qn /norestart",
                UninstallCommand = "msiexec /x \"app.msi\" /qn /norestart"
            }
        };

        var sut = new PackagingValidationService();

        var result = sut.Validate(request);

        Assert.True(result.IsValid);

        Directory.Delete(tempRoot, recursive: true);
    }
}
