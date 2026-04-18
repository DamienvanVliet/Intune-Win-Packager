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

    Task<string> ResolveCachedIconPathAsync(
        PackageCatalogEntry entry,
        string? installerPath = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogProviderDiagnostic>> GetProviderDiagnosticsAsync(CancellationToken cancellationToken = default);
}
