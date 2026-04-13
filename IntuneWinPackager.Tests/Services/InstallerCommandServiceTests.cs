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
    }

    [Fact]
    public void CreateSuggestion_ForExe_UsesSelectedSilentPreset()
    {
        var sut = new InstallerCommandService();
        var preset = sut.GetExeSilentPresets().First(p => p.Name == "NSIS");

        var suggestion = sut.CreateSuggestion(
            setupFilePath: @"C:\Temp\AcmeSetup.exe",
            installerType: InstallerType.Exe,
            preset: preset);

        Assert.Equal("\"AcmeSetup.exe\" /S", suggestion.InstallCommand);
        Assert.Equal("\"AcmeSetup.exe\" /S", suggestion.UninstallCommand);
        Assert.True(suggestion.SuggestedRules.RequireSilentSwitchReview);
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
    public void CreateSuggestion_ForAppxMsix_UsesPowerShellAndScriptDetection()
    {
        var sut = new InstallerCommandService();

        var suggestion = sut.CreateSuggestion(
            setupFilePath: @"C:\Temp\Contoso.msix",
            installerType: InstallerType.AppxMsix);

        Assert.Contains("Add-AppxPackage", suggestion.InstallCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Get-AppxPackage", suggestion.UninstallCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(IntuneDetectionRuleType.Script, suggestion.SuggestedRules.DetectionRule.RuleType);
        Assert.Contains("<detection-script>", suggestion.SuggestedRules.DetectionRule.Script.ScriptBody, StringComparison.Ordinal);
    }
}
