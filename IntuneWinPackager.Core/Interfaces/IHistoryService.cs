using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface IHistoryService
{
    Task<IReadOnlyList<PackageHistoryEntry>> GetRecentAsync(int maxCount = 20, CancellationToken cancellationToken = default);

    Task AddEntryAsync(PackageHistoryEntry entry, int maxCount = 50, CancellationToken cancellationToken = default);
}
