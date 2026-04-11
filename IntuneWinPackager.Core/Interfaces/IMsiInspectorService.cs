using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface IMsiInspectorService
{
    Task<MsiMetadata?> InspectAsync(string msiPath, CancellationToken cancellationToken = default);
}
