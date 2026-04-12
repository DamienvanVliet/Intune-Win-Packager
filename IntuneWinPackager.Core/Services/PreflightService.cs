using System.IO;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;

namespace IntuneWinPackager.Core.Services;

public sealed class PreflightService : IPreflightService
{
    private const long RecommendedFreeSpaceBytes = 2L * 1024 * 1024 * 1024;
    private const int ToolProbeTimeoutSeconds = 8;

    private readonly IProcessRunner _processRunner;

    public PreflightService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<PreflightResult> RunAsync(PackagingRequest request, CancellationToken cancellationToken = default)
    {
        var checks = new List<PreflightCheck>();

        if (request is null)
        {
            checks.Add(Error("request", "Request", "Packaging request is missing."));
            return new PreflightResult { Checks = checks };
        }

        await AddToolChecksAsync(request.IntuneWinAppUtilPath, checks, cancellationToken);
        AddSourceChecks(request.Configuration.SourceFolder, checks);
        AddSetupChecks(request.Configuration.SetupFilePath, request.Configuration.SourceFolder, checks);
        AddOutputChecks(request.Configuration.OutputFolder, checks);
        AddCommandChecks(request.Configuration.InstallCommand, request.Configuration.UninstallCommand, checks);

        return new PreflightResult { Checks = checks };
    }

    private async Task AddToolChecksAsync(
        string toolPath,
        ICollection<PreflightCheck> checks,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolPath))
        {
            checks.Add(Error("tool-path", "Tool Path", "Path to IntuneWinAppUtil.exe is missing."));
            return;
        }

        if (!File.Exists(toolPath))
        {
            checks.Add(Error("tool-file", "Tool File", "IntuneWinAppUtil.exe was not found at the configured path."));
            return;
        }

        var fileName = Path.GetFileName(toolPath);
        if (!string.Equals(fileName, "IntuneWinAppUtil.exe", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(Warning("tool-name", "Tool Filename", $"Selected executable is '{fileName}', expected IntuneWinAppUtil.exe."));
        }
        else
        {
            checks.Add(Pass("tool-name", "Tool Filename", "IntuneWinAppUtil.exe selected."));
        }

        try
        {
            var fileInfo = new FileInfo(toolPath);
            if (fileInfo.Length <= 0)
            {
                checks.Add(Error("tool-size", "Tool Integrity", "Tool file is empty or corrupted."));
                return;
            }

            checks.Add(Pass("tool-size", "Tool Integrity", $"Tool file size looks valid ({FormatBytes(fileInfo.Length)})."));
        }
        catch (Exception ex)
        {
            checks.Add(Error("tool-size", "Tool Integrity", $"Could not read tool file metadata: {ex.Message}"));
            return;
        }

        var probe = await ProbeToolExecutionAsync(toolPath, cancellationToken);
        checks.Add(probe.Passed
            ? Pass("tool-exec", "Tool Execution", probe.Message)
            : Error("tool-exec", "Tool Execution", probe.Message));
    }

    private void AddSourceChecks(string sourceFolder, ICollection<PreflightCheck> checks)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder))
        {
            checks.Add(Error("source-folder", "Source Folder", "Source folder is required."));
            return;
        }

        if (!Directory.Exists(sourceFolder))
        {
            checks.Add(Error("source-folder", "Source Folder", "Source folder does not exist."));
            return;
        }

        checks.Add(Pass("source-folder", "Source Folder", "Source folder exists."));
    }

    private void AddSetupChecks(string setupFilePath, string sourceFolder, ICollection<PreflightCheck> checks)
    {
        if (string.IsNullOrWhiteSpace(setupFilePath))
        {
            checks.Add(Error("setup-file", "Setup File", "Setup file is required."));
            return;
        }

        if (!File.Exists(setupFilePath))
        {
            checks.Add(Error("setup-file", "Setup File", "Setup file was not found."));
            return;
        }

        checks.Add(Pass("setup-file", "Setup File", "Setup file exists."));

        var extension = Path.GetExtension(setupFilePath);
        if (string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(Pass("setup-type", "Installer Type", $"Detected supported installer extension '{extension}'."));
        }
        else
        {
            checks.Add(Error("setup-type", "Installer Type", "Only .msi and .exe setup files are supported."));
        }

        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            checks.Add(Warning("setup-source", "Source Match", "Source folder is missing, setup location check skipped."));
            return;
        }

        if (IsPathInsideFolder(setupFilePath, sourceFolder))
        {
            checks.Add(Pass("setup-source", "Source Match", "Setup file is inside source folder."));
        }
        else
        {
            checks.Add(Error("setup-source", "Source Match", "Setup file must be inside the selected source folder."));
        }
    }

    private void AddOutputChecks(string outputFolder, ICollection<PreflightCheck> checks)
    {
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            checks.Add(Error("output-folder", "Output Folder", "Output folder is required."));
            return;
        }

        string fullOutputPath;
        try
        {
            fullOutputPath = Path.GetFullPath(outputFolder);
        }
        catch (Exception ex)
        {
            checks.Add(Error("output-folder", "Output Folder", $"Output folder path is invalid: {ex.Message}"));
            return;
        }

        try
        {
            Directory.CreateDirectory(fullOutputPath);
            checks.Add(Pass("output-folder", "Output Folder", "Output folder is available."));
        }
        catch (Exception ex)
        {
            checks.Add(Error("output-folder", "Output Folder", $"Could not create output folder: {ex.Message}"));
            return;
        }

        var writeProbePath = Path.Combine(fullOutputPath, $".iwp-preflight-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(writeProbePath, "preflight");
            File.Delete(writeProbePath);
            checks.Add(Pass("output-write", "Output Write Access", "Output folder is writable."));
        }
        catch (Exception ex)
        {
            checks.Add(Error("output-write", "Output Write Access", $"Output folder is not writable: {ex.Message}"));
            return;
        }

        try
        {
            var root = Path.GetPathRoot(fullOutputPath);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                var freeSpace = drive.AvailableFreeSpace;
                if (freeSpace < RecommendedFreeSpaceBytes)
                {
                    checks.Add(Warning(
                        "output-space",
                        "Disk Space",
                        $"Low free space on output drive ({FormatBytes(freeSpace)} available). Recommended: at least {FormatBytes(RecommendedFreeSpaceBytes)}."));
                }
                else
                {
                    checks.Add(Pass("output-space", "Disk Space", $"Free space is sufficient ({FormatBytes(freeSpace)} available)."));
                }
            }
            else
            {
                checks.Add(Warning("output-space", "Disk Space", "Could not determine output drive free space."));
            }
        }
        catch (Exception ex)
        {
            checks.Add(Warning("output-space", "Disk Space", $"Could not read free space: {ex.Message}"));
        }
    }

    private void AddCommandChecks(string installCommand, string uninstallCommand, ICollection<PreflightCheck> checks)
    {
        checks.Add(string.IsNullOrWhiteSpace(installCommand)
            ? Error("install-command", "Install Command", "Install command is required.")
            : Pass("install-command", "Install Command", "Install command is configured."));

        checks.Add(string.IsNullOrWhiteSpace(uninstallCommand)
            ? Error("uninstall-command", "Uninstall Command", "Uninstall command is required.")
            : Pass("uninstall-command", "Uninstall Command", "Uninstall command is configured."));
    }

    private async Task<(bool Passed, string Message)> ProbeToolExecutionAsync(string toolPath, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ToolProbeTimeoutSeconds));

        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessRunRequest
                {
                    FileName = toolPath,
                    Arguments = "-?",
                    WorkingDirectory = Path.GetDirectoryName(toolPath) ?? Environment.CurrentDirectory,
                    PreferLowImpact = true
                },
                cancellationToken: timeoutCts.Token);

            if (result.TimedOut)
            {
                return (false, $"Tool probe timed out after {ToolProbeTimeoutSeconds}s. Reinstall the Microsoft Win32 Content Prep Tool.");
            }

            return (true, $"Tool executed successfully (probe exit code {result.ExitCode}).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Failed to execute tool probe: {ex.Message}");
        }
    }

    private static PreflightCheck Pass(string key, string title, string message)
    {
        return new PreflightCheck
        {
            Key = key,
            Title = title,
            Message = message,
            Passed = true,
            Severity = PreflightSeverity.Info
        };
    }

    private static PreflightCheck Warning(string key, string title, string message)
    {
        return new PreflightCheck
        {
            Key = key,
            Title = title,
            Message = message,
            Passed = false,
            Severity = PreflightSeverity.Warning
        };
    }

    private static PreflightCheck Error(string key, string title, string message)
    {
        return new PreflightCheck
        {
            Key = key,
            Title = title,
            Message = message,
            Passed = false,
            Severity = PreflightSeverity.Error
        };
    }

    private static bool IsPathInsideFolder(string filePath, string folderPath)
    {
        var folderFullPath = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var fileFullPath = Path.GetFullPath(filePath);

        return fileFullPath.StartsWith(folderFullPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = bytes;
        var index = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0} {units[index]}";
    }
}
