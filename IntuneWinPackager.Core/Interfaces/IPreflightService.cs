using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface IPreflightService
{
    Task<PreflightResult> RunAsync(PackagingRequest request, CancellationToken cancellationToken = default);
}
