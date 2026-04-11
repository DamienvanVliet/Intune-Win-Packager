using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface IToolInstallerService
{
    Task<ToolInstallResult> InstallOrLocateAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
