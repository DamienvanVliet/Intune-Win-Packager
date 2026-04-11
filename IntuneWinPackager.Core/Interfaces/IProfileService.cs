using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface IProfileService
{
    Task<IReadOnlyList<PackageProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);

    Task SaveProfileAsync(PackageProfile profile, CancellationToken cancellationToken = default);

    Task<PackageProfile?> GetProfileAsync(string profileName, CancellationToken cancellationToken = default);
}
