using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Services;

public sealed class CuratedDetectionProfileService : ICuratedDetectionProfileService
{
    public Task<CuratedDetectionProfile?> FindBestMatchAsync(
        DetectionProfileQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<CuratedDetectionProfile?>(null);
    }
}
