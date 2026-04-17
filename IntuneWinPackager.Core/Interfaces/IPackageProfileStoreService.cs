using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Core.Interfaces;

public interface IPackageProfileStoreService
{
    Task<IReadOnlyList<CatalogPackageProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);

    Task SaveProfileAsync(CatalogPackageProfile profile, CancellationToken cancellationToken = default);

    Task PromoteProfileAsync(
        PackageCatalogSource source,
        string sourceChannel,
        string packageId,
        string version,
        string installerSha256,
        CancellationToken cancellationToken = default);
}
