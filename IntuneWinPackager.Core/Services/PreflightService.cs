using System.IO;
using System.Text.RegularExpressions;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;

namespace IntuneWinPackager.Core.Services;

public sealed class PreflightService : IPreflightService
{
    private const long RecommendedFreeSpaceBytes = 2L * 1024 * 1024 * 1024;
    private const int ToolProbeTimeoutSeconds = 8;
    private static readonly Regex ProductCodeRegex = new("^\\{[0-9A-Fa-f\\-]{36}\\}$", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly HashSet<string> SupportedArchitectures = new(StringComparer.OrdinalIgnoreCase)
    {
        "x64",
        "x86",
        "Both"
    };

    private static readonly HashSet<string> SupportedExtensions =
    [
        ".msi",
        ".exe",
        ".appx",
        ".appxbundle",
        ".msix",
        ".msixbundle",
        ".ps1",
        ".cmd",
        ".bat",
        ".vbs",
        ".wsf"
    ];

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
        AddSourceChecks(request.Configuration.SourceFolder, request.Configuration.UseSmartSourceStaging, checks);
        AddSetupChecks(request.Configuration.SetupFilePath, request.Configuration.SourceFolder, checks);
        AddOutputChecks(request.Configuration.OutputFolder, request.Configuration.SourceFolder, request.Configuration.UseSmartSourceStaging, checks);
        AddCommandChecks(request.Configuration.InstallCommand, request.Configuration.UninstallCommand, checks);
        AddIntuneRuleChecks(request.InstallerType, request.Configuration.IntuneRules, checks);

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

    private static void AddSourceChecks(string sourceFolder, bool useSmartSourceStaging, ICollection<PreflightCheck> checks)
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

        var packageArtifacts = Directory
            .EnumerateFiles(sourceFolder, "*.intunewin", SearchOption.AllDirectories)
            .Take(5)
            .ToList();

        if (packageArtifacts.Count == 0)
        {
            checks.Add(Pass("source-artifacts", "Source Artifacts", "No existing .intunewin files found in source tree."));
            return;
        }

        if (useSmartSourceStaging)
        {
            checks.Add(Pass(
                "source-artifacts",
                "Source Artifacts",
                $"{packageArtifacts.Count}+ existing .intunewin artifact(s) detected; smart source staging will isolate packaging."));
            return;
        }

        checks.Add(Warning(
            "source-artifacts",
            "Source Artifacts",
            "Existing .intunewin files in source can slow packaging. Enable smart source staging."));
    }

    private static void AddSetupChecks(string setupFilePath, string sourceFolder, ICollection<PreflightCheck> checks)
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

        var extension = Path.GetExtension(setupFilePath).ToLowerInvariant();
        if (SupportedExtensions.Contains(extension))
        {
            checks.Add(Pass("setup-type", "Installer Type", $"Detected supported setup extension '{extension}'."));
        }
        else
        {
            checks.Add(Error("setup-type", "Installer Type", "Unsupported setup extension. Use .msi, .exe, .appx/.msix, or supported script types."));
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

    private static void AddOutputChecks(
        string outputFolder,
        string sourceFolder,
        bool useSmartSourceStaging,
        ICollection<PreflightCheck> checks)
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

        if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder))
        {
            var fullSourcePath = Path.GetFullPath(sourceFolder);
            if (IsPathInsideFolder(fullOutputPath, fullSourcePath))
            {
                checks.Add(useSmartSourceStaging
                    ? Pass(
                        "output-source-overlap",
                        "Output vs Source",
                        "Output folder is inside source, but smart source staging will prevent recursive packaging.")
                    : Error(
                        "output-source-overlap",
                        "Output vs Source",
                        "Output folder is inside source. Enable smart source staging or move output outside source to avoid recursion and slow packaging."));
            }
            else
            {
                checks.Add(Pass("output-source-overlap", "Output vs Source", "Output folder is separated from source folder."));
            }
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

    private static void AddCommandChecks(string installCommand, string uninstallCommand, ICollection<PreflightCheck> checks)
    {
        checks.Add(string.IsNullOrWhiteSpace(installCommand)
            ? Error("install-command", "Install Command", "Install command is required.")
            : ContainsPlaceholder(installCommand)
                ? Error("install-command", "Install Command", "Install command still contains placeholders.")
                : Pass("install-command", "Install Command", "Install command is configured."));

        checks.Add(string.IsNullOrWhiteSpace(uninstallCommand)
            ? Error("uninstall-command", "Uninstall Command", "Uninstall command is required.")
            : ContainsPlaceholder(uninstallCommand)
                ? Error("uninstall-command", "Uninstall Command", "Uninstall command still contains placeholders.")
                : Pass("uninstall-command", "Uninstall Command", "Uninstall command is configured."));
    }

    private static void AddIntuneRuleChecks(
        InstallerType installerType,
        IntuneWin32AppRules rules,
        ICollection<PreflightCheck> checks)
    {
        checks.Add(rules.MaxRunTimeMinutes is >= 1 and <= 1440
            ? Pass("intune-runtime", "Intune Runtime", $"Max run time is set to {rules.MaxRunTimeMinutes} minute(s).")
            : Error("intune-runtime", "Intune Runtime", "Max run time must be between 1 and 1440 minutes."));

        if (installerType == InstallerType.Exe && rules.RequireSilentSwitchReview && !rules.SilentSwitchesVerified)
        {
            checks.Add(Error(
                "intune-switch-review",
                "EXE Silent Switch Review",
                "This EXE profile requires switch verification. Review vendor switches and confirm before packaging."));
        }
        else
        {
            checks.Add(Pass("intune-switch-review", "EXE Silent Switch Review", "Silent switch verification requirements are satisfied."));
        }

        AddRequirementChecks(rules.Requirements, checks);

        var detection = rules.DetectionRule;
        if (detection.RuleType == IntuneDetectionRuleType.None)
        {
            checks.Add(Error("detection-type", "Detection Rule", "No detection rule configured. Configure MSI, file, registry, or script detection."));
            return;
        }

        checks.Add(Pass("detection-type", "Detection Rule", $"Detection type '{detection.RuleType}' configured."));

        switch (detection.RuleType)
        {
            case IntuneDetectionRuleType.MsiProductCode:
                if (installerType != InstallerType.Msi)
                {
                    checks.Add(Error("detection-msi-installer", "MSI Detection", "MSI product code detection can only be used for MSI installers."));
                }

                if (string.IsNullOrWhiteSpace(detection.Msi.ProductCode))
                {
                    checks.Add(Error("detection-msi-code", "MSI Detection", "Product code is required for MSI detection."));
                }
                else if (!ProductCodeRegex.IsMatch(detection.Msi.ProductCode.Trim()))
                {
                    checks.Add(Error("detection-msi-code", "MSI Detection", "MSI product code format is invalid."));
                }
                else
                {
                    checks.Add(Pass("detection-msi-code", "MSI Detection", "MSI product code is configured."));
                }
                break;

            case IntuneDetectionRuleType.File:
                if (string.IsNullOrWhiteSpace(detection.File.Path) || string.IsNullOrWhiteSpace(detection.File.FileOrFolderName))
                {
                    checks.Add(Error("detection-file", "File Detection", "File detection requires both path and file/folder name."));
                }
                else
                {
                    checks.Add(Pass("detection-file", "File Detection", "File detection path and file/folder are configured."));
                }

                if (detection.File.Operator != IntuneDetectionOperator.Exists && string.IsNullOrWhiteSpace(detection.File.Value))
                {
                    checks.Add(Error("detection-file-value", "File Detection", "Selected file detection operator requires a comparison value."));
                }
                break;

            case IntuneDetectionRuleType.Registry:
                if (string.IsNullOrWhiteSpace(detection.Registry.Hive) || string.IsNullOrWhiteSpace(detection.Registry.KeyPath))
                {
                    checks.Add(Error("detection-registry", "Registry Detection", "Registry detection requires hive and key path."));
                }
                else
                {
                    checks.Add(Pass("detection-registry", "Registry Detection", "Registry hive and key path are configured."));
                }

                if (detection.Registry.Operator != IntuneDetectionOperator.Exists)
                {
                    if (string.IsNullOrWhiteSpace(detection.Registry.ValueName))
                    {
                        checks.Add(Error("detection-registry-name", "Registry Detection", "Registry value name is required for comparison operators."));
                    }

                    if (string.IsNullOrWhiteSpace(detection.Registry.Value))
                    {
                        checks.Add(Error("detection-registry-value", "Registry Detection", "Registry comparison value is required for comparison operators."));
                    }
                }
                break;

            case IntuneDetectionRuleType.Script:
                if (string.IsNullOrWhiteSpace(detection.Script.ScriptBody))
                {
                    checks.Add(Error("detection-script", "Script Detection", "Script detection requires script content."));
                }
                else if (ContainsPlaceholder(detection.Script.ScriptBody))
                {
                    checks.Add(Error("detection-script", "Script Detection", "Script detection still contains placeholders."));
                }
                else
                {
                    checks.Add(Pass("detection-script", "Script Detection", "Script detection content is configured."));
                }
                break;
        }
    }

    private static void AddRequirementChecks(
        IntuneRequirementRules requirements,
        ICollection<PreflightCheck> checks)
    {
        if (string.IsNullOrWhiteSpace(requirements.OperatingSystemArchitecture))
        {
            checks.Add(Error("requirement-architecture", "Requirements", "Operating system architecture is required."));
        }
        else if (!SupportedArchitectures.Contains(requirements.OperatingSystemArchitecture.Trim()))
        {
            checks.Add(Error("requirement-architecture", "Requirements", "Operating system architecture is invalid. Use x64, x86, or Both."));
        }
        else
        {
            checks.Add(Pass("requirement-architecture", "Requirements", $"Operating system architecture is set to '{requirements.OperatingSystemArchitecture}'."));
        }

        checks.Add(string.IsNullOrWhiteSpace(requirements.MinimumOperatingSystem)
            ? Error("requirement-os", "Requirements", "Minimum operating system is required.")
            : Pass("requirement-os", "Requirements", $"Minimum operating system is set to '{requirements.MinimumOperatingSystem}'."));

        checks.Add(requirements.MinimumFreeDiskSpaceMb < 0
            ? Error("requirement-disk", "Requirements", "Minimum free disk space cannot be negative.")
            : Pass("requirement-disk", "Requirements", requirements.MinimumFreeDiskSpaceMb > 0
                ? $"Minimum free disk space is set to {requirements.MinimumFreeDiskSpaceMb} MB."
                : "Minimum free disk space is not configured (optional)."));

        checks.Add(requirements.MinimumMemoryMb < 0
            ? Error("requirement-memory", "Requirements", "Minimum memory cannot be negative.")
            : Pass("requirement-memory", "Requirements", requirements.MinimumMemoryMb > 0
                ? $"Minimum memory is set to {requirements.MinimumMemoryMb} MB."
                : "Minimum memory is not configured (optional)."));

        checks.Add(requirements.MinimumCpuSpeedMhz < 0
            ? Error("requirement-cpu", "Requirements", "Minimum CPU speed cannot be negative.")
            : Pass("requirement-cpu", "Requirements", requirements.MinimumCpuSpeedMhz > 0
                ? $"Minimum CPU speed is set to {requirements.MinimumCpuSpeedMhz} MHz."
                : "Minimum CPU speed is not configured (optional)."));

        checks.Add(requirements.MinimumLogicalProcessors < 0
            ? Error("requirement-processors", "Requirements", "Minimum logical processors cannot be negative.")
            : Pass("requirement-processors", "Requirements", requirements.MinimumLogicalProcessors > 0
                ? $"Minimum logical processors is set to {requirements.MinimumLogicalProcessors}."
                : "Minimum logical processors is not configured (optional)."));

        if (string.IsNullOrWhiteSpace(requirements.RequirementScriptBody))
        {
            checks.Add(Pass("requirement-script", "Requirement Script", "Requirement script is not configured (optional)."));
        }
        else if (ContainsPlaceholder(requirements.RequirementScriptBody))
        {
            checks.Add(Error("requirement-script", "Requirement Script", "Requirement script still contains placeholders."));
        }
        else
        {
            checks.Add(Pass("requirement-script", "Requirement Script", "Requirement script content is configured."));
        }
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
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var fileFullPath = Path.GetFullPath(filePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fileFullPath.Equals(folderFullPath, StringComparison.OrdinalIgnoreCase) ||
               fileFullPath.StartsWith(folderFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPlaceholder(string value)
    {
        return PlaceholderRegex.IsMatch(value);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }
}
