using System.Text.RegularExpressions;

namespace IntuneWinPackager.Tests.Services;

public class UiRegressionSmokeTests
{
    [Fact]
    public void MainWindow_ContainsExpectedTabs_Icons_AndDensityScale()
    {
        var xaml = File.ReadAllText(GetPath("IntuneWinPackager.App", "MainWindow.xaml"));

        Assert.Contains("Ui.Tab.Packaging", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Tab.Store", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Tab.ToolsChecks", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Tab.UpdatesChanges", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Tab.Settings", xaml, StringComparison.Ordinal);

        Assert.Contains("Segoe MDL2 Assets", xaml, StringComparison.Ordinal);
        Assert.Contains("&#xE8A5;", xaml, StringComparison.Ordinal); // package
        Assert.Contains("&#xE719;", xaml, StringComparison.Ordinal); // store
        Assert.Contains("&#xE9D9;", xaml, StringComparison.Ordinal); // tools/check
        Assert.Contains("&#xE895;", xaml, StringComparison.Ordinal); // updates
        Assert.Contains("&#xE713;", xaml, StringComparison.Ordinal); // settings

        Assert.Contains("Density.Scale", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Density", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowStoreAdvancedDetails", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Store.Badge.Upgrade", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Store.Trust.HashMatched", xaml, StringComparison.Ordinal);
        Assert.Contains("InfoIconBorderStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Tooltip.StartPackaging", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.CommandPreview.Install", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Workflow.Title", xaml, StringComparison.Ordinal);
        Assert.Contains("RestartAsAdministratorCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ShouldShowRestartAsAdministrator", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding IntuneWinAppUtilPath, UpdateSourceTrigger=PropertyChanged}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BrowseToolPathCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding InstallCommandPreview, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding UninstallCommandPreview, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding TestDetectionCommand}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding ProofAndPackageCommand}\"", xaml, StringComparison.Ordinal);

        var sandboxInstallCommandIndex = xaml.IndexOf("Command=\"{Binding TestSandboxInstallCommand}\"", StringComparison.Ordinal);
        var sandboxUninstallCommandIndex = xaml.IndexOf("Command=\"{Binding TestSandboxUninstallCommand}\"", StringComparison.Ordinal);
        var packageCommandIndex = xaml.IndexOf("Command=\"{Binding PackageCommand}\"", StringComparison.Ordinal);
        Assert.True(sandboxInstallCommandIndex >= 0, "Sandbox install test command should be visible.");
        Assert.True(sandboxUninstallCommandIndex >= 0, "Sandbox uninstall test command should be visible.");
        Assert.True(packageCommandIndex >= 0, "Package command should be visible.");
        Assert.True(
            sandboxInstallCommandIndex < packageCommandIndex && sandboxUninstallCommandIndex < packageCommandIndex,
            "Sandbox install/uninstall tests should be presented before Start Packaging in the quick actions.");
    }

    [Fact]
    public void SandboxProofUi_UsesManualClose_Progress_And_AutoAppliesDetection()
    {
        var xaml = File.ReadAllText(GetPath("IntuneWinPackager.App", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(GetPath("IntuneWinPackager.App", "ViewModels", "MainViewModel.cs"));
        var sandboxService = File.ReadAllText(GetPath("IntuneWinPackager.Infrastructure", "Services", "SandboxProofService.cs"));
        var watcherStart = viewModel.IndexOf("private async Task WatchSandboxProofResultAsync", StringComparison.Ordinal);
        var watcherEnd = viewModel.IndexOf("private async Task<SandboxProofDetectionResult>", watcherStart, StringComparison.Ordinal);
        var watcher = viewModel[watcherStart..watcherEnd];

        Assert.Contains("OpenSandboxResultsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("TestSandboxInstallCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("TestSandboxUninstallCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("CloseSandboxCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("SandboxProofProgressValue", xaml, StringComparison.Ordinal);
        Assert.Contains("SandboxProofProgressText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseActiveSandboxAsync", watcher, StringComparison.Ordinal);
        Assert.Contains("TryApplySandboxProofDetectionResult(result, showStatus: true);", watcher, StringComparison.Ordinal);
        Assert.DoesNotContain("DetectionRuleType == IntuneDetectionRuleType.None", watcher, StringComparison.Ordinal);
        Assert.Contains("private async Task CloseSandboxAsync()", viewModel, StringComparison.Ordinal);
        Assert.Contains("CloseActiveSandboxAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("CloseActiveWindowsSandboxProcesses", sandboxService, StringComparison.Ordinal);
        Assert.Contains("Run the app as administrator", sandboxService, StringComparison.Ordinal);
        Assert.Contains("WaitForExit(3000)", sandboxService, StringComparison.Ordinal);
        Assert.Contains("WindowsSandboxRemoteSession", sandboxService, StringComparison.Ordinal);
    }

    [Fact]
    public void Localization_English_And_Dutch_StayInSync()
    {
        var englishKeys = ExtractResourceKeys(GetPath("IntuneWinPackager.App", "Localization", "Strings.en.xaml"));
        var dutchKeys = ExtractResourceKeys(GetPath("IntuneWinPackager.App", "Localization", "Strings.nl.xaml"));

        var missingInDutch = englishKeys.Except(dutchKeys).ToList();
        var missingInEnglish = dutchKeys.Except(englishKeys).ToList();

        Assert.True(missingInDutch.Count == 0, $"Missing Dutch keys: {string.Join(", ", missingInDutch)}");
        Assert.True(missingInEnglish.Count == 0, $"Missing English keys: {string.Join(", ", missingInEnglish)}");
    }

    [Fact]
    public void CatalogSearchAndDetails_GuardAgainstStaleAsyncResults()
    {
        var viewModel = File.ReadAllText(GetPath("IntuneWinPackager.App", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("private int _catalogSearchRequestId;", viewModel, StringComparison.Ordinal);
        Assert.Contains("private int _catalogDetailsRequestId;", viewModel, StringComparison.Ordinal);
        Assert.Contains("requestId != _catalogSearchRequestId", viewModel, StringComparison.Ordinal);
        Assert.Contains("requestId != _catalogDetailsRequestId || !IsSelectedCatalogEntry(entry)", viewModel, StringComparison.Ordinal);
        Assert.Contains("if (requestId == _catalogDetailsRequestId)", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupSelection_RefreshesInstallerMetadataOnce()
    {
        var viewModel = File.ReadAllText(GetPath("IntuneWinPackager.App", "ViewModels", "MainViewModel.cs"));
        var selectStart = viewModel.IndexOf("private async Task SelectSetupFileAsync", StringComparison.Ordinal);
        var selectEnd = viewModel.IndexOf("private void ResetPackageSpecificStateForSetupChange", selectStart, StringComparison.Ordinal);
        var selectMethod = viewModel[selectStart..selectEnd];

        Assert.Contains("_suppressSetupRefresh = true;", selectMethod, StringComparison.Ordinal);
        Assert.Contains("_suppressSetupRefresh = wasSuppressingSetupRefresh;", selectMethod, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(selectMethod, "HandleSetupFileChangedAsync"));
        Assert.Contains("private int _setupRefreshRequestId;", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsCurrentSetupRefresh(requestId, filePath)", viewModel, StringComparison.Ordinal);
        Assert.Contains("_ = RefreshSetupFilePathAsync(value);", viewModel, StringComparison.Ordinal);
        Assert.Contains("Setup refresh failed:", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Color_And_Density_Dictionaries_HaveMatchingKeys()
    {
        var lightColorKeys = ExtractResourceKeys(GetPath("IntuneWinPackager.App", "Styles", "Colors.Light.xaml"));
        var darkColorKeys = ExtractResourceKeys(GetPath("IntuneWinPackager.App", "Styles", "Colors.Dark.xaml"));
        Assert.Equal(lightColorKeys, darkColorKeys);

        var comfortableDensityKeys = ExtractResourceKeys(GetPath("IntuneWinPackager.App", "Styles", "Density.Comfortable.xaml"));
        var compactDensityKeys = ExtractResourceKeys(GetPath("IntuneWinPackager.App", "Styles", "Density.Compact.xaml"));
        Assert.Equal(comfortableDensityKeys, compactDensityKeys);
    }

    private static SortedSet<string> ExtractResourceKeys(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var matches = Regex.Matches(content, "x:Key=\"([^\"]+)\"");
        return new SortedSet<string>(
            matches
            .Select(match => match.Groups[1].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray(),
            StringComparer.Ordinal);
    }

    private static string GetPath(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "IntuneWinPackager.sln")))
        {
            current = current.Parent;
        }

        if (current is null)
        {
            throw new DirectoryNotFoundException("Could not locate repository root for UI smoke tests.");
        }

        return Path.Combine(new[] { current.FullName }.Concat(parts).ToArray());
    }
}
