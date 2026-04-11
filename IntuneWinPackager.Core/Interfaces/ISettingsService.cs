using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface ISettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
