using System.IO.Compression;
using System.Diagnostics;
using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using Microsoft.Win32;

namespace IntuneWinPackager.Tests.Services;

public class InstallerCommandServiceTests
{
    [Fact]
    public void CreateSuggestion_ForMsi_UsesProductCodeForUninstall()
    {
        var sut = new InstallerCommandService();

        var suggestion = sut.CreateSuggestion(
            setupFilePath: @"C:\Temp\AcmeAgent.msi",
            installerType: InstallerType.Msi,
            msiMetadata: new MsiMetadata
            {
                ProductCode = "{12345678-ABCD-4321-DCBA-876543210000}"
            });

        Assert.Equal("msiexec /i \"AcmeAgent.msi\" /quiet", suggestion.InstallCommand);
        Assert.Equal("msiexec /x {12345678-ABCD-4321-DCBA-876543210000} /quiet", suggestion.UninstallCommand);
        Assert.False(suggestion.ParameterProbeDetected);
    }

    [Fact]
    public void CreateSuggestion_ForExe_UsesSelectedSilentPreset()
    {
        var sut = new InstallerCommandService();
        var preset = sut.GetExeSilentPresets().First(p => p.InstallArguments == "/S");

        var suggestion = sut.CreateSuggestion(
            setupFilePath: @"C:\Temp\AcmeSetup.exe",
            installerType: InstallerType.Exe,
            preset: preset);

        Assert.Equal("\"AcmeSetup.exe\" /S", suggestion.InstallCommand);
        Assert.Equal("\"AcmeSetup.exe\" /S", suggestion.UninstallCommand);
        Assert.False(suggestion.SuggestedRules.RequireSilentSwitchReview);
        Assert.True(suggestion.SuggestedRules.SilentSwitchesVerified);
    }

    [Fact]
    public void CreateSuggestion_ForUnknownExe_UsesPlaceholderCommandsAndRequiresReview()
    {
        var sut = new InstallerCommandService();

        var suggestion = sut.CreateSuggestion(
            setupFilePath: @"C:\Temp\AcmeSetup.exe",
            installerType: InstallerType.Exe);

        Assert.Equal("\"AcmeSetup.exe\" <silent-args>", suggestion.InstallCommand);
        Assert.Equal("\"AcmeSetup.exe\" <uninstall-args>", suggestion.UninstallCommand);
        Assert.Equal(IntuneDetectionRuleType.None, suggestion.SuggestedRules.DetectionRule.RuleType);
        Assert.True(suggestion.SuggestedRules.RequireSilentSwitchReview);
        Assert.False(suggestion.SuggestedRules.SilentSwitchesVerified);
        Assert.Equal(SuggestionConfidenceLevel.Low, suggestion.ConfidenceLevel);
        Assert.False(suggestion.ParameterProbeDetected);
    }

    [Fact]
    public void DetectInstallerType_RecognizesAppxAndScriptTypes()
    {
        var sut = new InstallerCommandService();

        Assert.Equal(InstallerType.AppxMsix, sut.DetectInstallerType(@"C:\Temp\Claude.msix"));
        Assert.Equal(InstallerType.AppxMsix, sut.DetectInstallerType(@"C:\Temp\Company.appxbundle"));
        Assert.Equal(InstallerType.Script, sut.DetectInstallerType(@"C:\Temp\Install.ps1"));
        Assert.Equal(InstallerType.Script, sut.DetectInstallerType(@"C:\Temp\Install.cmd"));
    }

    [Fact]
    public void CreateSuggestion_ForAppxMsixWithIdentity_UsesDeterministicScriptDetection()
    {
        var sut = new InstallerCommandService();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-appx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var packagePath = Path.Combine(tempRoot, "Contoso.msix");

        try
        {
            CreateAppxLikeArchive(
                packagePath,
                identityName: "Contoso.App",
                publisher: "CN=Contoso",
                version: "2.1.3.0");

            var suggestion = sut.CreateSuggestion(
                setupFilePath: packagePath,
                installerType: InstallerType.AppxMsix);

            Assert.Contains("Add-AppxPackage", suggestion.InstallCommand, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Get-AppxPackage", suggestion.UninstallCommand, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(IntuneDetectionRuleType.Script, suggestion.SuggestedRules.DetectionRule.RuleType);
            Assert.Contains("Contoso.App", suggestion.SuggestedRules.DetectionRule.Script.ScriptBody, StringComparison.Ordinal);
            Assert.Contains("Version.ToString()", suggestion.SuggestedRules.DetectionRule.Script.ScriptBody, StringComparison.Ordinal);
            Assert.Contains("2.1.3.0", suggestion.SuggestedRules.DetectionRule.Script.ScriptBody, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateSuggestion_ForAppxMsixWithoutIdentity_LeavesDetectionUnset()
    {
        var sut = new InstallerCommandService();

        var suggestion = sut.CreateSuggestion(
            setupFilePath: @"C:\Temp\MissingManifest.msix",
            installerType: InstallerType.AppxMsix);

        Assert.Equal(IntuneDetectionRuleType.None, suggestion.SuggestedRules.DetectionRule.RuleType);
    }

    [Fact]
    public void CreateSuggestion_ReusesVerifiedKnowledge_ByHashAndVersion()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-knowledge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var cachePath = Path.Combine(tempRoot, "installer-knowledge.json");
        var setupFile = Path.Combine(tempRoot, "VendorSetup.exe");
        File.WriteAllText(setupFile, "dummy-exe-content");

        try
        {
            var sut = new InstallerCommandService(cachePath);
            var verifiedRules = new IntuneWin32AppRules
            {
                RequireSilentSwitchReview = true,
                SilentSwitchesVerified = true,
                AppliedTemplateName = "EXE - NSIS",
                TemplateGuidance = "Verified in local testing.",
                DetectionRule = new IntuneDetectionRule
                {
                    RuleType = IntuneDetectionRuleType.Registry,
                    Registry = new RegistryDetectionRule
                    {
                        Hive = "HKEY_LOCAL_MACHINE",
                        KeyPath = @"SOFTWARE\Vendor\App",
                        Operator = IntuneDetectionOperator.Exists
                    }
                }
            };

            sut.SaveVerifiedKnowledge(
                setupFilePath: setupFile,
                installerType: InstallerType.Exe,
                installCommand: "\"VendorSetup.exe\" /S",
                uninstallCommand: "\"C:\\Program Files\\Vendor\\uninstall.exe\" /S",
                intuneRules: verifiedRules);

            var suggestion = sut.CreateSuggestion(setupFile, InstallerType.Exe);

            Assert.True(suggestion.UsedKnowledgeCache);
            Assert.Equal(SuggestionConfidenceLevel.High, suggestion.ConfidenceLevel);
            Assert.Equal("\"VendorSetup.exe\" /S", suggestion.InstallCommand);
            Assert.Equal("\"C:\\Program Files\\Vendor\\uninstall.exe\" /S", suggestion.UninstallCommand);
            Assert.Equal(IntuneDetectionRuleType.Registry, suggestion.SuggestedRules.DetectionRule.RuleType);
            Assert.False(suggestion.SuggestedRules.RequireSilentSwitchReview);
            Assert.True(suggestion.SuggestedRules.SilentSwitchesVerified);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateSuggestion_ForMsiUpdateIntent_UsesGreaterThanOrEqualVersionOperator()
    {
        var sut = new InstallerCommandService();

        var suggestion = sut.CreateSuggestion(
            setupFilePath: @"C:\Temp\AcmeAgent.msi",
            installerType: InstallerType.Msi,
            msiMetadata: new MsiMetadata
            {
                ProductCode = "{12345678-ABCD-4321-DCBA-876543210000}",
                ProductVersion = "5.4.3"
            },
            detectionIntent: DetectionDeploymentIntent.Update);

        Assert.Equal(IntuneDetectionRuleType.MsiProductCode, suggestion.SuggestedRules.DetectionRule.RuleType);
        Assert.Equal(IntuneDetectionOperator.GreaterThanOrEqual, suggestion.SuggestedRules.DetectionRule.Msi.ProductVersionOperator);
    }

    [Fact]
    public void CreateSuggestion_ForAppxUpdateIntent_UsesGreaterThanOrEqualInScript()
    {
        var sut = new InstallerCommandService();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-appx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var packagePath = Path.Combine(tempRoot, "Contoso.msix");

        try
        {
            CreateAppxLikeArchive(
                packagePath,
                identityName: "Contoso.App",
                publisher: "CN=Contoso",
                version: "2.1.3.0");

            var suggestion = sut.CreateSuggestion(
                setupFilePath: packagePath,
                installerType: InstallerType.AppxMsix,
                detectionIntent: DetectionDeploymentIntent.Update);

            Assert.Equal(IntuneDetectionRuleType.Script, suggestion.SuggestedRules.DetectionRule.RuleType);
            Assert.Contains("$versionOperator = \"GreaterThanOrEqual\"", suggestion.SuggestedRules.DetectionRule.Script.ScriptBody, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateSuggestion_ForExeWithInstalledEvidence_AddsCompositeDetectionRules()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(exePath))
        {
            return;
        }

        var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
        var displayName = FirstNonEmpty(versionInfo.ProductName, versionInfo.FileDescription);
        var publisher = versionInfo.CompanyName ?? string.Empty;
        var displayVersion = NormalizeVersion(versionInfo.ProductVersion ?? versionInfo.FileVersion);
        if (string.IsNullOrWhiteSpace(displayName) ||
            string.IsNullOrWhiteSpace(publisher) ||
            string.IsNullOrWhiteSpace(displayVersion))
        {
            return;
        }

        var subKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\IwpTest-{Guid.NewGuid():N}";
        Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        using var created = Registry.CurrentUser.CreateSubKey(subKey);
        created?.SetValue("DisplayName", displayName);
        created?.SetValue("Publisher", publisher);
        created?.SetValue("DisplayVersion", displayVersion);
        created?.SetValue("UninstallString", "\"C:\\Dummy\\uninstall.exe\" /S");

        try
        {
            var sut = new InstallerCommandService();
            var suggestion = sut.CreateSuggestion(
                setupFilePath: exePath,
                installerType: InstallerType.Exe);

            Assert.Equal(IntuneDetectionRuleType.Registry, suggestion.SuggestedRules.DetectionRule.RuleType);
            Assert.Equal("DisplayVersion", suggestion.SuggestedRules.DetectionRule.Registry.ValueName);
            Assert.Contains(suggestion.SuggestedRules.AdditionalDetectionRules, rule =>
                rule.RuleType == IntuneDetectionRuleType.Registry &&
                rule.Registry.ValueName.Equals("DisplayName", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(suggestion.SuggestedRules.AdditionalDetectionRules, rule =>
                rule.RuleType == IntuneDetectionRuleType.Registry &&
                rule.Registry.ValueName.Equals("Publisher", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void CreateSuggestion_DoesNotReuseVerifiedKnowledgeAcrossDifferentSourceOrArchitectureHints()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-knowledge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var cachePath = Path.Combine(tempRoot, "installer-knowledge.json");
        var setupFile = Path.Combine(tempRoot, "VendorSetup.exe");
        File.WriteAllText(setupFile, "dummy-exe-content");

        try
        {
            var sut = new InstallerCommandService(cachePath);
            var verifiedRules = new IntuneWin32AppRules
            {
                RequireSilentSwitchReview = false,
                SilentSwitchesVerified = true,
                DetectionRule = new IntuneDetectionRule
                {
                    RuleType = IntuneDetectionRuleType.Registry,
                    Registry = new RegistryDetectionRule
                    {
                        Hive = "HKEY_LOCAL_MACHINE",
                        KeyPath = @"SOFTWARE\Vendor\App",
                        ValueName = "DisplayVersion",
                        Operator = IntuneDetectionOperator.Equals,
                        Value = "1.2.3"
                    }
                }
            };

            sut.SaveVerifiedKnowledge(
                setupFilePath: setupFile,
                installerType: InstallerType.Exe,
                installCommand: "\"VendorSetup.exe\" /S",
                uninstallCommand: "\"C:\\Program Files\\Vendor\\uninstall.exe\" /S",
                intuneRules: verifiedRules,
                sourceChannelHint: "winget",
                installerArchitectureHint: "x64");

            var sameContext = sut.CreateSuggestion(
                setupFile,
                InstallerType.Exe,
                detectionIntent: DetectionDeploymentIntent.Install,
                sourceChannelHint: "winget",
                installerArchitectureHint: "x64");
            Assert.True(sameContext.UsedKnowledgeCache);

            var differentSource = sut.CreateSuggestion(
                setupFile,
                InstallerType.Exe,
                detectionIntent: DetectionDeploymentIntent.Install,
                sourceChannelHint: "chocolatey",
                installerArchitectureHint: "x64");
            Assert.False(differentSource.UsedKnowledgeCache);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var normalized = version.Trim();
        var plusIndex = normalized.IndexOf('+');
        if (plusIndex > 0)
        {
            normalized = normalized[..plusIndex];
        }

        return normalized;
    }

    private static void CreateAppxLikeArchive(
        string packagePath,
        string identityName,
        string publisher,
        string version)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        var manifestEntry = archive.CreateEntry("AppxManifest.xml");
        using var stream = manifestEntry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write($"""
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="{identityName}" Publisher="{publisher}" Version="{version}" />
</Package>
""");
    }
}
