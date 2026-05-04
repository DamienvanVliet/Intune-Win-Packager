using System.IO.Compression;
using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

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
