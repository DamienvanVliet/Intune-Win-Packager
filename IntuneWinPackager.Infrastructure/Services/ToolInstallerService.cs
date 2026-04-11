using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Process;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class ToolInstallerService : IToolInstallerService
{
    private static readonly string[] WingetPackageIds =
    {
        "Microsoft.Win32ContentPrepTool"
    };

    private readonly IProcessRunner _processRunner;
    private readonly IToolLocatorService _toolLocatorService;

    public ToolInstallerService(IProcessRunner processRunner, IToolLocatorService toolLocatorService)
    {
        _processRunner = processRunner;
        _toolLocatorService = toolLocatorService;
    }

    public async Task<ToolInstallResult> InstallOrLocateAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var existingPath = _toolLocatorService.LocateToolPath();
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            return new ToolInstallResult
            {
                Success = true,
                AlreadyInstalled = true,
                ExitCode = 0,
                Message = "IntuneWinAppUtil.exe is already installed.",
                ToolPath = existingPath
            };
        }

        var exitCodes = new List<int>();

        foreach (var packageId in WingetPackageIds)
        {
            progress?.Report($"Installing {packageId} with winget...");

            ProcessRunResult result;
            try
            {
                result = await _processRunner.RunAsync(
                    new ProcessRunRequest
                    {
                        FileName = "winget",
                        Arguments = $"install --id {packageId} -e --accept-package-agreements --accept-source-agreements --disable-interactivity --silent",
                        WorkingDirectory = Environment.CurrentDirectory
                    },
                    new Progress<ProcessOutputLine>(line =>
                    {
                        if (!string.IsNullOrWhiteSpace(line.Text))
                        {
                            progress?.Report(line.Text);
                        }
                    }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                return new ToolInstallResult
                {
                    Success = false,
                    ExitCode = -1,
                    Message = $"Failed to start winget install: {ex.Message}"
                };
            }

            exitCodes.Add(result.ExitCode);

            if (result.ExitCode != 0)
            {
                progress?.Report($"winget exited with code {result.ExitCode} for {packageId}.");
                continue;
            }

            var located = await LocateWithRetryAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(located))
            {
                return new ToolInstallResult
                {
                    Success = true,
                    ExitCode = 0,
                    Message = "IntuneWinAppUtil.exe installed successfully.",
                    ToolPath = located
                };
            }

            return new ToolInstallResult
            {
                Success = false,
                ExitCode = 0,
                Message = "Installation completed but IntuneWinAppUtil.exe could not be located automatically. Use Auto Locate or set the path manually."
            };
        }

        var lastExit = exitCodes.LastOrDefault(-1);
        return new ToolInstallResult
        {
            Success = false,
            ExitCode = lastExit,
            Message = "Could not install Microsoft Win32 Content Prep Tool via winget. Try running winget manually or verify internet access and package sources."
        };
    }

    private async Task<string?> LocateWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var located = _toolLocatorService.LocateToolPath();
            if (!string.IsNullOrWhiteSpace(located))
            {
                return located;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return null;
    }
}
