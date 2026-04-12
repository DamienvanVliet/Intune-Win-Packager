using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Process;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class ToolInstallerService : IToolInstallerService
{
    private const int ToolProbeTimeoutSeconds = 8;

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
            progress?.Report("Found an existing IntuneWinAppUtil.exe. Verifying tool health...");

            var existingHealth = await VerifyToolHealthAsync(existingPath, cancellationToken);
            if (!existingHealth.IsHealthy)
            {
                progress?.Report($"Existing tool failed health check: {existingHealth.Message}");
            }
            else
            {
                progress?.Report(existingHealth.Message);

                return new ToolInstallResult
                {
                    Success = true,
                    AlreadyInstalled = true,
                    ExitCode = 0,
                    Message = "IntuneWinAppUtil.exe is already installed and healthy.",
                    ToolPath = existingPath
                };
            }
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
                var health = await VerifyToolHealthAsync(located, cancellationToken);
                if (!health.IsHealthy)
                {
                    return new ToolInstallResult
                    {
                        Success = false,
                        ExitCode = result.ExitCode,
                        Message = $"IntuneWinAppUtil.exe was located after install, but failed health check: {health.Message}",
                        ToolPath = located
                    };
                }

                return new ToolInstallResult
                {
                    Success = true,
                    ExitCode = 0,
                    Message = "IntuneWinAppUtil.exe installed successfully and passed health check.",
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

    private async Task<(bool IsHealthy, string Message)> VerifyToolHealthAsync(string toolPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolPath))
        {
            return (false, "Tool path is empty.");
        }

        if (!File.Exists(toolPath))
        {
            return (false, "Tool file does not exist.");
        }

        try
        {
            var fileInfo = new FileInfo(toolPath);
            if (fileInfo.Length <= 0)
            {
                return (false, "Tool file is empty.");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Could not read tool file metadata: {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ToolProbeTimeoutSeconds));

        try
        {
            var probeResult = await _processRunner.RunAsync(
                new ProcessRunRequest
                {
                    FileName = toolPath,
                    Arguments = "-?",
                    WorkingDirectory = Path.GetDirectoryName(toolPath) ?? Environment.CurrentDirectory,
                    PreferLowImpact = true
                },
                cancellationToken: timeoutCts.Token);

            if (probeResult.TimedOut)
            {
                return (false, $"Tool probe timed out after {ToolProbeTimeoutSeconds}s.");
            }

            return (true, $"Tool probe completed (exit code {probeResult.ExitCode}).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Tool probe failed: {ex.Message}");
        }
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
