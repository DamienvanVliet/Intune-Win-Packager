using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Tests.Services;

public sealed class CuratedDetectionProfileServiceTests
{
    [Fact]
    public async Task FindBestMatchAsync_ReturnsNull_WhenNoExternalProvenProfileStoreIsConfigured()
    {
        var sut = new CuratedDetectionProfileService();

        var profile = await sut.FindBestMatchAsync(new DetectionProfileQuery
        {
            PackageId = "any.vendor.package",
            Name = "Any Package",
            Publisher = "Any Vendor",
            Version = "1.0.0",
            InstallerType = InstallerType.Exe
        });

        Assert.Null(profile);
    }

    [Fact]
    public async Task FindBestMatchAsync_HonorsCancellation()
    {
        var sut = new CuratedDetectionProfileService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.FindBestMatchAsync(new DetectionProfileQuery(), cancellation.Token));
    }
}
