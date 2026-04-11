using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Infrastructure.Persistence;
using IntuneWinPackager.Infrastructure.Support;
using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class SettingsService : ISettingsService
{
    private readonly JsonFileStore _store = new();

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        DataPathProvider.EnsureBaseDirectory();
        return await _store.ReadAsync(DataPathProvider.SettingsFilePath, new AppSettings(), cancellationToken);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        DataPathProvider.EnsureBaseDirectory();
        await _store.WriteAsync(DataPathProvider.SettingsFilePath, settings, cancellationToken);
    }
}
