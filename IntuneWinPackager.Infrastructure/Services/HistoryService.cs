using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Infrastructure.Persistence;
using IntuneWinPackager.Infrastructure.Support;
using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class HistoryService : IHistoryService
{
    private readonly JsonFileStore _store = new();

    public async Task<IReadOnlyList<PackageHistoryEntry>> GetRecentAsync(int maxCount = 20, CancellationToken cancellationToken = default)
    {
        DataPathProvider.EnsureBaseDirectory();

        var entries = await _store.ReadAsync(DataPathProvider.HistoryFilePath, new List<PackageHistoryEntry>(), cancellationToken);

        return entries
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(Math.Max(1, maxCount))
            .ToList();
    }

    public async Task AddEntryAsync(PackageHistoryEntry entry, int maxCount = 50, CancellationToken cancellationToken = default)
    {
        DataPathProvider.EnsureBaseDirectory();

        var entries = (await _store.ReadAsync(DataPathProvider.HistoryFilePath, new List<PackageHistoryEntry>(), cancellationToken)).ToList();
        entries.Insert(0, entry);

        var trimmedEntries = entries
            .OrderByDescending(existing => existing.TimestampUtc)
            .Take(Math.Max(1, maxCount))
            .ToList();

        await _store.WriteAsync(DataPathProvider.HistoryFilePath, trimmedEntries, cancellationToken);
    }
}
