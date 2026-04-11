using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface IPackagingWorkflowService
{
    Task<PackagingResult> PackageAsync(PackagingRequest request, IProgress<string>? logProgress = null, CancellationToken cancellationToken = default);
}
