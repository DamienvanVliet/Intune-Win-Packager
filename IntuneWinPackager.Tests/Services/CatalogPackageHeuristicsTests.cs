using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Tests.Services;

public class CatalogPackageHeuristicsTests
{
    [Fact]
    public void BuildMatchPatterns_IncludesNamePackageIdAndSegments()
    {
        var entry = new PackageCatalogEntry
        {
            Source = PackageCatalogSource.Winget,
            PackageId = "Discord.Discord",
            Name = "Discord"
        };

        var patterns = CatalogPackageHeuristics.BuildMatchPatterns(entry);

        Assert.Contains("Discord", patterns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Discord.Discord", patterns, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDetectionScript_AddsWeightedMatchingSignals()
    {
        var entry = new PackageCatalogEntry
        {
            Source = PackageCatalogSource.Winget,
            PackageId = "Git.Git",
            Name = "Git",
            Publisher = "Git for Windows Project",
            Version = "2.53.0"
        };

        var script = CatalogPackageHeuristics.BuildDetectionScript(entry);

        Assert.Contains("$minScore = 55", script, StringComparison.Ordinal);
        Assert.Contains("$publisherHints", script, StringComparison.Ordinal);
        Assert.Contains("$versionHints", script, StringComparison.Ordinal);
        Assert.Contains("$score += 70", script, StringComparison.Ordinal);
        Assert.Contains("if ($bestScore -ge $minScore) { exit 0 }", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDetectionScript_EscapesSingleQuotes()
    {
        var entry = new PackageCatalogEntry
        {
            Source = PackageCatalogSource.Winget,
            PackageId = "Contoso.OBrienApp",
            Name = "O'Brien Tool"
        };

        var script = CatalogPackageHeuristics.BuildDetectionScript(entry);

        Assert.Contains("O''Brien Tool", script, StringComparison.Ordinal);
    }
}
