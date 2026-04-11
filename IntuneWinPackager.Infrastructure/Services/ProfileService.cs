using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Infrastructure.Persistence;
using IntuneWinPackager.Infrastructure.Support;
using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class ProfileService : IProfileService
{
    private readonly JsonFileStore _store = new();

    public async Task<IReadOnlyList<PackageProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        DataPathProvider.EnsureBaseDirectory();
        var profiles = await _store.ReadAsync(DataPathProvider.ProfilesFilePath, new List<PackageProfile>(), cancellationToken);

        return profiles
            .OrderByDescending(profile => profile.UpdatedAtUtc)
            .ToList();
    }

    public async Task SaveProfileAsync(PackageProfile profile, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException("Profile name is required.", nameof(profile));
        }

        DataPathProvider.EnsureBaseDirectory();

        var profiles = (await _store.ReadAsync(DataPathProvider.ProfilesFilePath, new List<PackageProfile>(), cancellationToken)).ToList();

        var existingIndex = profiles.FindIndex(existing =>
            string.Equals(existing.Name, profile.Name, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            profiles[existingIndex] = profile;
        }
        else
        {
            profiles.Add(profile);
        }

        await _store.WriteAsync(DataPathProvider.ProfilesFilePath, profiles, cancellationToken);
    }

    public async Task<PackageProfile?> GetProfileAsync(string profileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return null;
        }

        var profiles = await GetProfilesAsync(cancellationToken);
        return profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase));
    }
}
