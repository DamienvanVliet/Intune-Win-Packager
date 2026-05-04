using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Tests.Services;

public sealed class CuratedDetectionProfileServiceTests
{
    [Fact]
    public async Task FindBestMatchAsync_ReturnsVerifiedProfile_ForKnownPackage()
    {
        var sut = new CuratedDetectionProfileService();

        var profile = await sut.FindBestMatchAsync(new DetectionProfileQuery
        {
            PackageId = "7zip.7zip",
            Name = "7-Zip",
            Publisher = "Igor Pavlov",
            Version = "24.09",
            InstallerType = InstallerType.Exe
        });

        Assert.NotNull(profile);
        Assert.Equal("verified", profile!.ConfidenceLabel);
        Assert.Equal(InstallerType.Exe, profile.InstallerType);
        Assert.Contains(profile.Rules.AdditionalDetectionRules, rule =>
            rule.RuleType == IntuneDetectionRuleType.Registry &&
            rule.Registry.ValueName.Equals("DisplayName", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FindBestMatchAsync_ReturnsNull_ForUnknownPackage()
    {
        var sut = new CuratedDetectionProfileService();

        var profile = await sut.FindBestMatchAsync(new DetectionProfileQuery
        {
            PackageId = "unknown.vendor.app",
            Name = "Unknown App",
            Publisher = "Unknown Vendor",
            Version = "1.0.0",
            InstallerType = InstallerType.Exe
        });

        Assert.Null(profile);
    }
}
