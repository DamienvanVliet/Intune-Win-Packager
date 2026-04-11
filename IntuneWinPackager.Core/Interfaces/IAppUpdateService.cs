using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface IAppUpdateService
{
    Task<AppUpdateInfo> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default);

    Task<AppUpdateInstallResult> DownloadAndLaunchInstallerAsync(
        AppUpdateInfo updateInfo,
        IProgress<string>? logProgress = null,
        bool silentInstall = false,
        CancellationToken cancellationToken = default);
}
