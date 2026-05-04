using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Core.Interfaces;

public interface IDetectionTestService
{
    Task<DetectionTestResult> TestAsync(
        InstallerType installerType,
        IntuneDetectionRule detectionRule,
        CancellationToken cancellationToken = default);
}
