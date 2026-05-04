using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface ICuratedDetectionProfileService
{
    Task<CuratedDetectionProfile?> FindBestMatchAsync(
        DetectionProfileQuery query,
        CancellationToken cancellationToken = default);
}

