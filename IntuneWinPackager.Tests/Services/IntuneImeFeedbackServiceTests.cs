using IntuneWinPackager.Core.Services;

namespace IntuneWinPackager.Tests.Services;

public sealed class IntuneImeFeedbackServiceTests
{
    [Fact]
    public async Task AnalyzeRecentDetectionFailuresAsync_ReturnsRecommendationsFromImeLogs()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-ime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var logPath = Path.Combine(tempRoot, "IntuneManagementExtension.log");
        await File.WriteAllTextAsync(logPath, """
[Win32App][Contoso.App] Detection failed with code 0x87D1041C.
[Win32App][Contoso.App] Detection used HKEY_CURRENT_USER path and no output from script stdout.
""");

        try
        {
            var sut = new IntuneImeFeedbackService(tempRoot);
            var feedback = await sut.AnalyzeRecentDetectionFailuresAsync("Contoso.App");

            Assert.NotEmpty(feedback);
            Assert.Contains(feedback, item => item.Signal.Contains("0x87D1041C", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(feedback, item => item.Recommendation.Contains("HKLM", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
