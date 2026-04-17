using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface IPackageCatalogService
{
    Task<IReadOnlyList<PackageCatalogEntry>> SearchAsync(PackageCatalogQuery query, CancellationToken cancellationToken = default);

    Task<PackageCatalogEntry?> GetDetailsAsync(PackageCatalogEntry entry, CancellationToken cancellationToken = default);

    Task<PackageCatalogDownloadResult> DownloadInstallerAsync(
        PackageCatalogEntry entry,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
