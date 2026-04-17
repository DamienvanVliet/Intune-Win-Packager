using System.Text.RegularExpressions;

namespace IntuneWinPackager.Tests.Services;

public class UiRegressionSmokeTests
{
    [Fact]
    public void MainWindow_ContainsExpectedTabs_Icons_AndDensityScale()
    {
        var xaml = File.ReadAllText(GetPath("IntuneWinPackager.App", "MainWindow.xaml"));

        Assert.Contains("Ui.Tab.Packaging", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Tab.ToolsChecks", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Tab.UpdatesChanges", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Tab.Settings", xaml, StringComparison.Ordinal);

        Assert.Contains("Segoe MDL2 Assets", xaml, StringComparison.Ordinal);
        Assert.Contains("&#xE8A5;", xaml, StringComparison.Ordinal); // package
        Assert.Contains("&#xE9D9;", xaml, StringComparison.Ordinal); // tools/check
        Assert.Contains("&#xE895;", xaml, StringComparison.Ordinal); // updates
        Assert.Contains("&#xE713;", xaml, StringComparison.Ordinal); // settings

        Assert.Contains("Density.Scale", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Density", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowStoreAdvancedDetails", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Store.Badge.Upgrade", xaml, StringComparison.Ordinal);
        Assert.Contains("Ui.Store.Trust.HashMatched", xaml, StringComparison.Ordinal);
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
