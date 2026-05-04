using IntuneWinPackager.Core.Utilities;

namespace IntuneWinPackager.Tests.Services;

public sealed class DeterministicDetectionScriptTests
{
    [Fact]
    public void BuildExactExeRegistryScript_ReturnsRecognizableDeterministicScript()
    {
        var script = DeterministicDetectionScript.BuildExactExeRegistryScript(
            "Contoso Agent",
            "Contoso Ltd",
            "9.8.7");

        Assert.Contains(DeterministicDetectionScript.ExeRegistryExactMarker, script, StringComparison.Ordinal);
        Assert.Contains("DisplayName -eq $displayName", script, StringComparison.Ordinal);
        Assert.Contains("DisplayVersion -eq $displayVersion", script, StringComparison.Ordinal);
        Assert.Contains("Write-Output", script, StringComparison.Ordinal);
        Assert.True(DeterministicDetectionScript.IsExactExeRegistryScript(script));
        Assert.True(DeterministicDetectionScript.IsIntuneCompliantSuccessSignalScript(script));
    }

    [Fact]
    public void BuildExactAppxIdentityScript_ReturnsRecognizableDeterministicScript()
    {
        var script = DeterministicDetectionScript.BuildExactAppxIdentityScript(
            "Contoso.App",
            "1.2.3.4",
            "CN=Contoso");

        Assert.Contains(DeterministicDetectionScript.AppxIdentityExactMarker, script, StringComparison.Ordinal);
        Assert.Contains("Get-AppxPackage", script, StringComparison.Ordinal);
        Assert.Contains("Version.ToString() -eq $expectedVersion", script, StringComparison.Ordinal);
        Assert.Contains("Write-Output", script, StringComparison.Ordinal);
        Assert.True(DeterministicDetectionScript.IsExactAppxIdentityScript(script));
        Assert.True(DeterministicDetectionScript.IsIntuneCompliantSuccessSignalScript(script));
    }

    [Fact]
    public void IsExactExeRegistryScript_ReturnsFalse_ForGenericScript()
    {
        const string genericScript = "if (Test-Path 'C:\\Program Files\\Contoso') { exit 0 } exit 1";
        Assert.False(DeterministicDetectionScript.IsExactExeRegistryScript(genericScript));
        Assert.False(DeterministicDetectionScript.IsIntuneCompliantSuccessSignalScript(genericScript));
    }
}
