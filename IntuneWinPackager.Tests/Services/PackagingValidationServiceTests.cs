using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Core.Utilities;
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
    public void Validate_ReturnsSuccess_ForSpecificUninstallerFileDetection()
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
                        RuleType = IntuneDetectionRuleType.File,
                        File = new FileDetectionRule
                        {
                            Path = @"%ProgramFiles%\\Contoso Agent",
                            FileOrFolderName = "uninstall.exe",
                            Operator = IntuneDetectionOperator.Exists
                        }
                    }
                }
            }
        };

        var sut = new PackagingValidationService();

        var result = sut.Validate(request);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public void Validate_ReturnsSuccess_ForExeWithMsiProductCodeDetection()
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
                InstallCommand = "\"app.exe\" /quiet",
                UninstallCommand = "msiexec /x {12345678-1234-1234-1234-123456789ABC} /qn",
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

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));

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
                            ScriptBody = DeterministicDetectionScript.BuildExactAppxIdentityScript(
                                "Contoso.App",
                                "1.2.3.4",
                                "CN=Contoso")
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
    public void Validate_ReturnsError_WhenExeRegistryDetectionIsNotDeterministic()
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
                        RuleType = IntuneDetectionRuleType.Registry,
                        Registry = new RegistryDetectionRule
                        {
                            Hive = "HKEY_LOCAL_MACHINE",
                            KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ContosoApp",
                            Operator = IntuneDetectionOperator.Exists
                        }
                    }
                }
            }
        };

        var sut = new PackagingValidationService();
        var result = sut.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("DisplayVersion", StringComparison.OrdinalIgnoreCase));

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public void Validate_ReturnsError_WhenScriptDetectionIsUsedForExe()
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
                        RuleType = IntuneDetectionRuleType.Script,
                        Script = new ScriptDetectionRule
                        {
                            ScriptBody = "if (Test-Path 'C:\\Program Files\\Contoso') { exit 0 } exit 1"
                        }
                    }
                }
            }
        };

        var sut = new PackagingValidationService();
        var result = sut.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("exact registry equality", StringComparison.OrdinalIgnoreCase));

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public void Validate_ReturnsError_WhenScriptDetectionHasNoIntuneSuccessSignal()
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
                            ScriptBody = """
                                         $packageName = "Contoso.App"
                                         $expectedVersion = "1.2.3.4"
                                         $match = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Where-Object {
                                             $_.Version.ToString() -eq $expectedVersion
                                         } | Select-Object -First 1
                                         if ($null -ne $match) { exit 2 }
                                         exit 1
                                         """
                        }
                    }
                }
            }
        };

        var sut = new PackagingValidationService();
        var result = sut.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("STDOUT", StringComparison.OrdinalIgnoreCase));

        Directory.Delete(tempRoot, recursive: true);
    }


    [Fact]
    public void Validate_ReturnsSuccess_WhenExeUsesDeterministicExactRegistryScript()
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
                        RuleType = IntuneDetectionRuleType.Script,
                        Script = new ScriptDetectionRule
                        {
                            ScriptBody = DeterministicDetectionScript.BuildExactExeRegistryScript(
                                "Contoso Agent",
                                "Contoso Ltd",
                                "1.2.3")
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

    [Fact]
    public void Validate_ReturnsError_WhenStrictDetectionProvenanceIsEnabledWithWeakEvidence()
    {
        var request = BuildBaseExeRequest();
        request = request with
        {
            Configuration = request.Configuration with
            {
                IntuneRules = request.Configuration.IntuneRules with
                {
                    StrictDetectionProvenanceMode = true,
                    DetectionProvenance =
                    [
                        new DetectionFieldProvenance
                        {
                            FieldName = "DisplayName",
                            FieldValue = "Contoso App",
                            Source = DetectionProvenanceSource.HeuristicFallback,
                            IsStrongEvidence = false
                        }
                    ]
                }
            }
        };

        var sut = new PackagingValidationService();
        var result = sut.Validate(request);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Strict detection mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsError_WhenExeIdentityLockHasNoApproval()
    {
        var request = BuildBaseExeRequest();
        request = request with
        {
            Configuration = request.Configuration with
            {
                IntuneRules = request.Configuration.IntuneRules with
                {
                    ExeIdentityLockEnabled = true,
                    ExeFallbackApproved = false,
                    DetectionProvenance =
                    [
                        new DetectionFieldProvenance
                        {
                            FieldName = "DisplayName",
                            FieldValue = "Contoso App",
                            Source = DetectionProvenanceSource.HeuristicFallback,
                            IsStrongEvidence = false
                        }
                    ]
                }
            }
        };

        var sut = new PackagingValidationService();
        var result = sut.Validate(request);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("fallback approval", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsError_WhenSystemContextUsesHkcuRegistryDetection()
    {
        var request = BuildBaseExeRequest();
        request = request with
        {
            Configuration = request.Configuration with
            {
                IntuneRules = request.Configuration.IntuneRules with
                {
                    InstallContext = IntuneInstallContext.System,
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Registry,
                        Registry = new RegistryDetectionRule
                        {
                            Hive = "HKEY_CURRENT_USER",
                            KeyPath = @"SOFTWARE\Contoso\App",
                            ValueName = "DisplayVersion",
                            Operator = IntuneDetectionOperator.Equals,
                            Value = "1.0.0"
                        }
                    }
                }
            }
        };

        var sut = new PackagingValidationService();
        var result = sut.Validate(request);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("HKCU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsError_WhenStrictScriptPolicyFails()
    {
        var request = BuildBaseExeRequest();
        request = request with
        {
            Configuration = request.Configuration with
            {
                IntuneRules = request.Configuration.IntuneRules with
                {
                    EnforceStrictScriptPolicy = true,
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Script,
                        Script = new ScriptDetectionRule
                        {
                            ScriptBody = DeterministicDetectionScript.Utf8Bom + "Write-Error 'bad'; Write-Output 'ok'; exit 0; exit 1"
                        }
                    }
                }
            }
        };

        var sut = new PackagingValidationService();
        var result = sut.Validate(request);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Strict script policy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_NormalizesScriptDetectionPolicyBeforeBlockingPackaging()
    {
        var request = BuildBaseExeRequest();
        var script = DeterministicDetectionScript
            .BuildExactExeRegistryScript("Contoso App", "Contoso Ltd", "1.0.0")
            .TrimStart('\uFEFF');

        request = request with
        {
            Configuration = request.Configuration with
            {
                IntuneRules = request.Configuration.IntuneRules with
                {
                    EnforceStrictScriptPolicy = true,
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Script,
                        Script = new ScriptDetectionRule
                        {
                            ScriptBody = script
                        }
                    }
                }
            }
        };

        var sut = new PackagingValidationService();
        var result = sut.Validate(request);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    private static PackagingRequest BuildBaseExeRequest()
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

        return new PackagingRequest
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
                        RuleType = IntuneDetectionRuleType.Registry,
                        Registry = new RegistryDetectionRule
                        {
                            Hive = "HKEY_LOCAL_MACHINE",
                            KeyPath = @"SOFTWARE\Contoso\App",
                            ValueName = "DisplayVersion",
                            Operator = IntuneDetectionOperator.Equals,
                            Value = "1.0.0"
                        }
                    }
                }
            }
        };
    }
}
