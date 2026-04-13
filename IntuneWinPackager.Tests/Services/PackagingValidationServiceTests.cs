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
                UninstallCommand = "msiexec /x \"app.msi\" /qn /norestart",
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

        var sut = new PackagingValidationService();

        var result = sut.Validate(request);

        Assert.True(result.IsValid);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public void Validate_ReturnsError_WhenDetectionRuleIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "app.exe");
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
                InstallCommand = "\"app.exe\" /S",
                UninstallCommand = "\"app.exe\" /S",
                IntuneRules = new IntuneWin32AppRules
                {
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.None
                    }
                }
            }
        };

        var sut = new PackagingValidationService();

        var result = sut.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("detection rule", StringComparison.OrdinalIgnoreCase));

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public void Validate_ReturnsError_WhenExeSwitchReviewIsRequiredButNotConfirmed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "app.exe");
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
                InstallCommand = "\"app.exe\" /S",
                UninstallCommand = "\"app.exe\" /S",
                IntuneRules = new IntuneWin32AppRules
                {
                    RequireSilentSwitchReview = true,
                    SilentSwitchesVerified = false,
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.File,
                        File = new FileDetectionRule
                        {
                            Path = @"%ProgramFiles%\\Contoso",
                            FileOrFolderName = "app.exe",
                            Operator = IntuneDetectionOperator.Exists
                        }
                    }
                }
            }
        };

        var sut = new PackagingValidationService();

        var result = sut.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("switches", StringComparison.OrdinalIgnoreCase));

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public void Validate_ReturnsSuccess_ForAppxWithScriptDetectionRule()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "app.msix");
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
                InstallCommand = "powershell.exe -ExecutionPolicy Bypass -Command \"Add-AppxPackage -Path \\\"app.msix\\\"\"",
                UninstallCommand = "powershell.exe -ExecutionPolicy Bypass -Command \"Get-AppxPackage -Name 'Contoso.App' | Remove-AppxPackage\"",
                IntuneRules = new IntuneWin32AppRules
                {
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Script,
                        Script = new ScriptDetectionRule
                        {
                            ScriptBody = "$p = Get-AppxPackage -Name 'Contoso.App' -ErrorAction SilentlyContinue\nif ($null -ne $p) { exit 0 }\nexit 1"
                        }
                    }
                }
            }
        };

        var sut = new PackagingValidationService();

        var result = sut.Validate(request);

        Assert.True(result.IsValid);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public void Validate_ReturnsError_WhenRequirementScriptContainsPlaceholder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-test-{Guid.NewGuid():N}");
        var sourceFolder = Path.Combine(tempRoot, "source");
        var outputFolder = Path.Combine(tempRoot, "output");

        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(outputFolder);

        var setupFilePath = Path.Combine(sourceFolder, "app.exe");
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
                InstallCommand = "\"app.exe\" /S",
                UninstallCommand = "\"app.exe\" /S",
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
                            FileOrFolderName = "app.exe",
                            Operator = IntuneDetectionOperator.Exists
                        }
                    }
                }
            }
        };

        var sut = new PackagingValidationService();

        var result = sut.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Requirement script still contains placeholders", StringComparison.OrdinalIgnoreCase));

        Directory.Delete(tempRoot, recursive: true);
    }
}
