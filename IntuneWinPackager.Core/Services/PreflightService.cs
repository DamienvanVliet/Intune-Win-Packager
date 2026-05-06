using System.IO;
using System.Text.RegularExpressions;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Core.Utilities;
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

    private static readonly HashSet<string> GenericDetectionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup.exe",
        "installer.exe",
        "install.exe",
        "uninstall.exe",
        "update.exe",
        "updater.exe",
        "app.exe"
    };

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
            checks.Add(Error(
                "request",
                "Request",
                "Packaging request is missing.",
                titleKey: "Core.Preflight.Title.Request",
                messageKey: "Core.Preflight.Message.RequestMissing"));
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
            checks.Add(Error(
                "tool-path",
                "Tool Path",
                "Path to IntuneWinAppUtil.exe is missing.",
                titleKey: "Core.Preflight.Title.ToolPath",
                messageKey: "Core.Preflight.Message.ToolPathMissing"));
            return;
        }

        if (!File.Exists(toolPath))
        {
            checks.Add(Error(
                "tool-file",
                "Tool File",
                "IntuneWinAppUtil.exe was not found at the configured path.",
                titleKey: "Core.Preflight.Title.ToolFile",
                messageKey: "Core.Preflight.Message.ToolFileMissing"));
            return;
        }

        var fileName = Path.GetFileName(toolPath);
        if (!string.Equals(fileName, "IntuneWinAppUtil.exe", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(Warning(
                "tool-name",
                "Tool Filename",
                $"Selected executable is '{fileName}', expected IntuneWinAppUtil.exe.",
                titleKey: "Core.Preflight.Title.ToolFilename"));
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
                checks.Add(Error(
                    "tool-size",
                    "Tool Integrity",
                    "Tool file is empty or corrupted.",
                    titleKey: "Core.Preflight.Title.ToolIntegrity",
                    messageKey: "Core.Preflight.Message.ToolIntegrityCorrupt"));
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
            checks.Add(Error(
                "source-folder",
                "Source Folder",
                "Source folder is required.",
                titleKey: "Core.Preflight.Title.SourceFolder",
                messageKey: "Core.Preflight.Message.SourceFolderRequired"));
            return;
        }

        if (!Directory.Exists(sourceFolder))
        {
            checks.Add(Error(
                "source-folder",
                "Source Folder",
                "Source folder does not exist.",
                titleKey: "Core.Preflight.Title.SourceFolder",
                messageKey: "Core.Preflight.Message.SourceFolderMissing"));
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
            "Existing .intunewin files in source can slow packaging. Enable smart source staging.",
            titleKey: "Core.Preflight.Title.SourceArtifacts",
            messageKey: "Core.Preflight.Message.SourceArtifactsWarn"));
    }

    private static void AddSetupChecks(string setupFilePath, string sourceFolder, ICollection<PreflightCheck> checks)
    {
        if (string.IsNullOrWhiteSpace(setupFilePath))
        {
            checks.Add(Error(
                "setup-file",
                "Setup File",
                "Setup file is required.",
                titleKey: "Core.Preflight.Title.SetupFile",
                messageKey: "Core.Preflight.Message.SetupFileRequired"));
            return;
        }

        if (!File.Exists(setupFilePath))
        {
            checks.Add(Error(
                "setup-file",
                "Setup File",
                "Setup file was not found.",
                titleKey: "Core.Preflight.Title.SetupFile",
                messageKey: "Core.Preflight.Message.SetupFileMissing"));
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
            checks.Add(Error(
                "setup-type",
                "Installer Type",
                "Unsupported setup extension. Use .msi, .exe, .appx/.msix, or supported script types.",
                titleKey: "Core.Preflight.Title.InstallerType",
                messageKey: "Core.Preflight.Message.SetupTypeUnsupported"));
        }

        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            checks.Add(Warning(
                "setup-source",
                "Source Match",
                "Source folder is missing, setup location check skipped.",
                titleKey: "Core.Preflight.Title.SourceMatch",
                messageKey: "Core.Preflight.Message.SourceMatchSkipped"));
            return;
        }

        if (IsPathInsideFolder(setupFilePath, sourceFolder))
        {
            checks.Add(Pass("setup-source", "Source Match", "Setup file is inside source folder."));
        }
        else
        {
            checks.Add(Error(
                "setup-source",
                "Source Match",
                "Setup file must be inside the selected source folder.",
                titleKey: "Core.Preflight.Title.SourceMatch",
                messageKey: "Core.Preflight.Message.SetupOutsideSource"));
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
            checks.Add(Error(
                "output-folder",
                "Output Folder",
                "Output folder is required.",
                titleKey: "Core.Preflight.Title.OutputFolder",
                messageKey: "Core.Preflight.Message.OutputFolderRequired"));
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
                        "Output folder is inside source. Enable smart source staging or move output outside source to avoid recursion and slow packaging.",
                        titleKey: "Core.Preflight.Title.OutputVsSource",
                        messageKey: "Core.Preflight.Message.OutputInsideSource"));
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
            checks.Add(Error(
                "output-write",
                "Output Write Access",
                $"Output folder is not writable: {ex.Message}",
                titleKey: "Core.Preflight.Title.OutputWriteAccess",
                messageKey: "Core.Preflight.Message.OutputWriteDenied"));
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
                        $"Low free space on output drive ({FormatBytes(freeSpace)} available). Recommended: at least {FormatBytes(RecommendedFreeSpaceBytes)}.",
                        titleKey: "Core.Preflight.Title.DiskSpace"));
                }
                else
                {
                    checks.Add(Pass("output-space", "Disk Space", $"Free space is sufficient ({FormatBytes(freeSpace)} available)."));
                }
            }
            else
            {
                checks.Add(Warning(
                    "output-space",
                    "Disk Space",
                    "Could not determine output drive free space.",
                    titleKey: "Core.Preflight.Title.DiskSpace",
                    messageKey: "Core.Preflight.Message.OutputDiskUnknown"));
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
            ? Error(
                "install-command",
                "Install Command",
                "Install command is required.",
                titleKey: "Core.Preflight.Title.InstallCommand",
                messageKey: "Core.Preflight.Message.InstallCommandRequired")
            : ContainsPlaceholder(installCommand)
                ? Error(
                    "install-command",
                    "Install Command",
                    "Install command still contains placeholders.",
                    titleKey: "Core.Preflight.Title.InstallCommand",
                    messageKey: "Core.Preflight.Message.InstallCommandPlaceholder")
                : Pass("install-command", "Install Command", "Install command is configured."));

        checks.Add(string.IsNullOrWhiteSpace(uninstallCommand)
            ? Error(
                "uninstall-command",
                "Uninstall Command",
                "Uninstall command is required.",
                titleKey: "Core.Preflight.Title.UninstallCommand",
                messageKey: "Core.Preflight.Message.UninstallCommandRequired")
            : ContainsPlaceholder(uninstallCommand)
                ? Error(
                    "uninstall-command",
                    "Uninstall Command",
                    "Uninstall command still contains placeholders.",
                    titleKey: "Core.Preflight.Title.UninstallCommand",
                    messageKey: "Core.Preflight.Message.UninstallCommandPlaceholder")
                : Pass("uninstall-command", "Uninstall Command", "Uninstall command is configured."));
    }

    private static void AddIntuneRuleChecks(
        InstallerType installerType,
        IntuneWin32AppRules rules,
        ICollection<PreflightCheck> checks)
    {
        checks.Add(rules.MaxRunTimeMinutes is >= 1 and <= 1440
            ? Pass("intune-runtime", "Intune Runtime", $"Max run time is set to {rules.MaxRunTimeMinutes} minute(s).")
            : Error(
                "intune-runtime",
                "Intune Runtime",
                "Max run time must be between 1 and 1440 minutes.",
                titleKey: "Core.Preflight.Title.IntuneRuntime",
                messageKey: "Core.Preflight.Message.IntuneRuntimeInvalid"));

        if (installerType == InstallerType.Exe && rules.RequireSilentSwitchReview && !rules.SilentSwitchesVerified)
        {
            checks.Add(Error(
                "intune-switch-review",
                "EXE Silent Switch Review",
                "This EXE profile requires switch verification. Review vendor switches and confirm before packaging.",
                titleKey: "Core.Preflight.Title.ExeSwitchReview",
                messageKey: "Core.Preflight.Message.ExeSwitchReviewRequired"));
        }
        else
        {
            checks.Add(Pass("intune-switch-review", "EXE Silent Switch Review", "Silent switch verification requirements are satisfied."));
        }

        AddRequirementChecks(rules.Requirements, checks);

        var detection = rules.DetectionRule;
        if (detection.RuleType == IntuneDetectionRuleType.None)
        {
            checks.Add(Error(
                "detection-type",
                "Detection Rule",
                "No detection rule configured. Configure MSI, file, registry, or script detection.",
                titleKey: "Core.Preflight.Title.DetectionRule",
                messageKey: "Core.Preflight.Message.DetectionRuleMissing"));
            return;
        }

        checks.Add(Pass("detection-type", "Detection Rule", $"Detection type '{detection.RuleType}' configured."));
        AddDetectionProvenanceChecks(installerType, rules, checks);
        AddContextCorrectnessChecks(installerType, rules, detection, checks);

        switch (detection.RuleType)
        {
            case IntuneDetectionRuleType.MsiProductCode:
                if (installerType != InstallerType.Msi)
                {
                    checks.Add(Error(
                        "detection-msi-installer",
                        "MSI Detection",
                        "MSI product code detection can only be used for MSI installers.",
                        titleKey: "Core.Preflight.Title.MsiDetection",
                        messageKey: "Core.Preflight.Message.MsiDetectionInstallerMismatch"));
                }

                if (string.IsNullOrWhiteSpace(detection.Msi.ProductCode))
                {
                    checks.Add(Error(
                        "detection-msi-code",
                        "MSI Detection",
                        "Product code is required for MSI detection.",
                        titleKey: "Core.Preflight.Title.MsiDetection",
                        messageKey: "Core.Preflight.Message.MsiDetectionProductCodeRequired"));
                }
                else if (!ProductCodeRegex.IsMatch(detection.Msi.ProductCode.Trim()))
                {
                    checks.Add(Error(
                        "detection-msi-code",
                        "MSI Detection",
                        "MSI product code format is invalid.",
                        titleKey: "Core.Preflight.Title.MsiDetection",
                        messageKey: "Core.Preflight.Message.MsiDetectionProductCodeFormat"));
                }
                else
                {
                    checks.Add(Pass("detection-msi-code", "MSI Detection", "MSI product code is configured."));
                }

                if (!string.IsNullOrWhiteSpace(detection.Msi.ProductVersion))
                {
                    if (rules.DetectionIntent == DetectionDeploymentIntent.Install &&
                        detection.Msi.ProductVersionOperator != IntuneDetectionOperator.Equals)
                    {
                        checks.Add(Error(
                            "detection-msi-intent-install",
                            "MSI Detection",
                            "Install intent should use MSI ProductVersion operator Equals."));
                    }

                    if (rules.DetectionIntent == DetectionDeploymentIntent.Update &&
                        detection.Msi.ProductVersionOperator != IntuneDetectionOperator.GreaterThanOrEqual)
                    {
                        checks.Add(Error(
                            "detection-msi-intent-update",
                            "MSI Detection",
                            "Update intent should use MSI ProductVersion operator GreaterThanOrEqual."));
                    }
                }
                break;

            case IntuneDetectionRuleType.File:
                if (string.IsNullOrWhiteSpace(detection.File.Path) || string.IsNullOrWhiteSpace(detection.File.FileOrFolderName))
                {
                    checks.Add(Error(
                        "detection-file",
                        "File Detection",
                        "File detection requires both path and file/folder name.",
                        titleKey: "Core.Preflight.Title.FileDetection",
                        messageKey: "Core.Preflight.Message.FileDetectionPathOrNameRequired"));
                }
                else
                {
                    checks.Add(Pass("detection-file", "File Detection", "File detection path and file/folder are configured."));
                }

                if (detection.File.Operator != IntuneDetectionOperator.Exists && string.IsNullOrWhiteSpace(detection.File.Value))
                {
                    checks.Add(Error(
                        "detection-file-value",
                        "File Detection",
                        "Selected file detection operator requires a comparison value.",
                        titleKey: "Core.Preflight.Title.FileDetection",
                        messageKey: "Core.Preflight.Message.FileDetectionValueRequired"));
                }

                if (IsGenericDetectionPath(detection.File.Path))
                {
                    checks.Add(Error(
                        "detection-file-path-generic",
                        "File Detection",
                        "File detection path is too generic. Use a unique, vendor-specific install path.",
                        titleKey: "Core.Preflight.Title.FileDetection",
                        messageKey: "Core.Preflight.Message.FileDetectionPathTooGeneric"));
                }

                if (IsGenericDetectionName(detection.File.FileOrFolderName, detection.File.Path))
                {
                    checks.Add(Error(
                        "detection-file-name-generic",
                        "File Detection",
                        "File detection name is too generic. Target a unique binary or folder.",
                        titleKey: "Core.Preflight.Title.FileDetection",
                        messageKey: "Core.Preflight.Message.FileDetectionNameTooGeneric"));
                }
                break;

            case IntuneDetectionRuleType.Registry:
                if (string.IsNullOrWhiteSpace(detection.Registry.Hive) || string.IsNullOrWhiteSpace(detection.Registry.KeyPath))
                {
                    checks.Add(Error(
                        "detection-registry",
                        "Registry Detection",
                        "Registry detection requires hive and key path.",
                        titleKey: "Core.Preflight.Title.RegistryDetection",
                        messageKey: "Core.Preflight.Message.RegistryDetectionHiveOrKeyRequired"));
                }
                else
                {
                    checks.Add(Pass("detection-registry", "Registry Detection", "Registry hive and key path are configured."));
                }

                if (detection.Registry.Operator != IntuneDetectionOperator.Exists)
                {
                    if (string.IsNullOrWhiteSpace(detection.Registry.ValueName))
                    {
                        checks.Add(Error(
                            "detection-registry-name",
                            "Registry Detection",
                            "Registry value name is required for comparison operators.",
                            titleKey: "Core.Preflight.Title.RegistryDetection",
                            messageKey: "Core.Preflight.Message.RegistryDetectionValueNameRequired"));
                    }

                    if (string.IsNullOrWhiteSpace(detection.Registry.Value))
                    {
                        checks.Add(Error(
                            "detection-registry-value",
                            "Registry Detection",
                            "Registry comparison value is required for comparison operators.",
                            titleKey: "Core.Preflight.Title.RegistryDetection",
                            messageKey: "Core.Preflight.Message.RegistryDetectionValueRequired"));
                    }
                }

                if (installerType == InstallerType.Exe)
                {
                    if (detection.Registry.Operator != IntuneDetectionOperator.Equals)
                    {
                        checks.Add(Error(
                            "detection-registry-exe-operator",
                            "Registry Detection",
                            "For EXE detection, use Registry operator Equals with an exact value.",
                            titleKey: "Core.Preflight.Title.RegistryDetection",
                            messageKey: "Core.Preflight.Message.RegistryDetectionExeRequiresEquals"));
                    }

                    if (!detection.Registry.ValueName.Equals("DisplayVersion", StringComparison.OrdinalIgnoreCase))
                    {
                        checks.Add(Error(
                            "detection-registry-exe-name",
                            "Registry Detection",
                            "For EXE detection, use Value Name 'DisplayVersion' to enforce exact version detection.",
                            titleKey: "Core.Preflight.Title.RegistryDetection",
                            messageKey: "Core.Preflight.Message.RegistryDetectionExeRequiresDisplayVersion"));
                    }

                    if (string.IsNullOrWhiteSpace(detection.Registry.Value))
                    {
                        checks.Add(Error(
                            "detection-registry-exe-value",
                            "Registry Detection",
                            "For EXE detection, DisplayVersion value is required.",
                            titleKey: "Core.Preflight.Title.RegistryDetection",
                            messageKey: "Core.Preflight.Message.RegistryDetectionExeVersionRequired"));
                    }
                }
                break;

            case IntuneDetectionRuleType.Script:
                var normalizedScriptBody = DeterministicDetectionScript.NormalizeForIntuneScriptPolicy(detection.Script.ScriptBody);
                if (string.IsNullOrWhiteSpace(detection.Script.ScriptBody))
                {
                    checks.Add(Error(
                        "detection-script",
                        "Script Detection",
                        "Script detection requires script content.",
                        titleKey: "Core.Preflight.Title.ScriptDetection",
                        messageKey: "Core.Preflight.Message.ScriptDetectionBodyRequired"));
                }
                else if (ContainsPlaceholder(detection.Script.ScriptBody))
                {
                    checks.Add(Error(
                        "detection-script",
                        "Script Detection",
                        "Script detection still contains placeholders.",
                        titleKey: "Core.Preflight.Title.ScriptDetection",
                        messageKey: "Core.Preflight.Message.ScriptDetectionPlaceholder"));
                }
                else
                {
                    checks.Add(Pass("detection-script", "Script Detection", "Script detection content is configured."));
                }

                if (!DeterministicDetectionScript.IsIntuneCompliantSuccessSignalScript(normalizedScriptBody))
                {
                    checks.Add(Error(
                        "detection-script-stdout",
                        "Script Detection",
                        "Script detection success path must write STDOUT and exit with code 0.",
                        titleKey: "Core.Preflight.Title.ScriptDetection",
                        messageKey: "Core.Preflight.Message.ScriptDetectionSuccessSignal"));
                }

                if (installerType == InstallerType.Exe &&
                    !DeterministicDetectionScript.IsExactExeRegistryScript(normalizedScriptBody))
                {
                    checks.Add(Error(
                        "detection-script-exe-deterministic",
                        "Script Detection",
                        "For EXE installers, script detection must use exact registry equality (DisplayName, Publisher, DisplayVersion).",
                        titleKey: "Core.Preflight.Title.ScriptDetection",
                        messageKey: "Core.Preflight.Message.ScriptDetectionExeMustBeDeterministic"));
                }
                else if (installerType == InstallerType.AppxMsix &&
                         !DeterministicDetectionScript.IsExactAppxIdentityScript(normalizedScriptBody))
                {
                    checks.Add(Error(
                        "detection-script-appx-precision",
                        "Script Detection",
                        "APPX/MSIX script detection must check exact package identity and version.",
                        titleKey: "Core.Preflight.Title.ScriptDetection",
                        messageKey: "Core.Preflight.Message.ScriptDetectionAppxMustCheckVersion"));
                }
                else if (installerType != InstallerType.AppxMsix &&
                         installerType != InstallerType.Script &&
                         installerType != InstallerType.Exe)
                {
                    checks.Add(Error(
                        "detection-script-last-resort",
                        "Script Detection",
                        "Script detection is only recommended as a last resort. Use MSI, Registry, or File detection for this installer type.",
                        titleKey: "Core.Preflight.Title.ScriptDetection",
                        messageKey: "Core.Preflight.Message.ScriptDetectionLastResortOnly"));
                }

                if (rules.EnforceStrictScriptPolicy &&
                    !DeterministicDetectionScript.IsStrictIntuneScriptPolicyCompliant(normalizedScriptBody))
                {
                    checks.Add(Error(
                        "detection-script-strict-policy",
                        "Script Detection",
                        "Strict script policy failed. Script must be UTF-8 BOM, use exit 0 + STDOUT on success, include exit 1, and avoid STDERR noise commands."));
                }
                break;
        }

        if (installerType == InstallerType.Exe &&
            rules.ExeIdentityLockEnabled &&
            detection.RuleType == IntuneDetectionRuleType.Registry &&
            HasStrongExeIdentityEvidence(rules.DetectionProvenance) &&
            !HasCompositeExeIdentityRules(rules, detection))
        {
            checks.Add(Error(
                "detection-exe-composite-required",
                "EXE Detection",
                "EXE detection must include composite strict checks for DisplayVersion, DisplayName, and Publisher."));
        }
    }

    private static void AddDetectionProvenanceChecks(
        InstallerType installerType,
        IntuneWin32AppRules rules,
        ICollection<PreflightCheck> checks)
    {
        if (!rules.StrictDetectionProvenanceMode)
        {
            checks.Add(Pass("detection-provenance", "Detection Provenance", "Strict provenance mode is disabled."));
            return;
        }

        if (rules.DetectionProvenance.Count == 0)
        {
            checks.Add(Error(
                "detection-provenance",
                "Detection Provenance",
                "Strict detection mode is enabled, but no detection provenance was provided."));
            return;
        }

        checks.Add(Pass("detection-provenance", "Detection Provenance", "Detection provenance entries are present."));

        if (!rules.DetectionProvenance.Any(item => item.IsStrongEvidence))
        {
            checks.Add(Error(
                "detection-provenance-strong",
                "Detection Provenance",
                "Strict detection mode requires at least one strong evidence source."));
        }

        if (installerType == InstallerType.Exe && !HasStrongExeIdentityEvidence(rules.DetectionProvenance))
        {
            checks.Add(Error(
                "detection-provenance-exe-identity",
                "Detection Provenance",
                "Strict EXE detection requires strong DisplayName, Publisher, and DisplayVersion provenance."));
        }
    }

    private static void AddContextCorrectnessChecks(
        InstallerType installerType,
        IntuneWin32AppRules rules,
        IntuneDetectionRule detection,
        ICollection<PreflightCheck> checks)
    {
        if (rules.InstallContext == IntuneInstallContext.System)
        {
            if (detection.RuleType == IntuneDetectionRuleType.Registry &&
                detection.Registry.Hive.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(Error(
                    "context-system-hkcu",
                    "Context Validation",
                    "System install context cannot use HKCU registry detection reliably."));
            }

            if (detection.RuleType == IntuneDetectionRuleType.File &&
                IsUserScopedPath(detection.File.Path))
            {
                checks.Add(Error(
                    "context-system-userpath",
                    "Context Validation",
                    "System install context should not use user-scoped file detection paths."));
            }

            if (detection.RuleType == IntuneDetectionRuleType.Script &&
                ContainsUserScopedScriptMarkers(detection.Script.ScriptBody))
            {
                checks.Add(Error(
                    "context-system-script-userscope",
                    "Context Validation",
                    "System install context script detection references user-scoped registry/path markers."));
            }
        }

        if (detection.RuleType == IntuneDetectionRuleType.Registry &&
            detection.Registry.Check32BitOn64System &&
            detection.Registry.KeyPath.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(Error(
                "context-registry-wow6432node",
                "Context Validation",
                "Registry detection is using 32-bit view while key path already points to WOW6432Node."));
        }

        if (installerType == InstallerType.Exe &&
            rules.ExeIdentityLockEnabled &&
            rules.DetectionProvenance.Count > 0 &&
            !HasStrongExeIdentityEvidence(rules.DetectionProvenance) &&
            !rules.ExeFallbackApproved)
        {
            checks.Add(Error(
                "context-exe-identity-lock",
                "EXE Identity Lock",
                "EXE identity lock requires explicit fallback approval when exact uninstall identity evidence is unavailable."));
        }
    }

    private static bool HasStrongExeIdentityEvidence(IReadOnlyList<DetectionFieldProvenance> provenance)
    {
        var requiredFields = new[] { "DisplayName", "Publisher", "DisplayVersion" };
        foreach (var field in requiredFields)
        {
            if (!provenance.Any(item =>
                    item.IsStrongEvidence &&
                    item.FieldName.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(item.FieldValue)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasCompositeExeIdentityRules(IntuneWin32AppRules rules, IntuneDetectionRule primaryDetection)
    {
        var allRules = new List<IntuneDetectionRule> { primaryDetection };
        allRules.AddRange(rules.AdditionalDetectionRules);

        var hasDisplayVersion = allRules.Any(rule =>
            rule.RuleType == IntuneDetectionRuleType.Registry &&
            rule.Registry.ValueName.Equals("DisplayVersion", StringComparison.OrdinalIgnoreCase) &&
            rule.Registry.Operator is IntuneDetectionOperator.Equals or IntuneDetectionOperator.GreaterThanOrEqual &&
            !string.IsNullOrWhiteSpace(rule.Registry.Value));

        var hasDisplayName = allRules.Any(rule =>
            rule.RuleType == IntuneDetectionRuleType.Registry &&
            rule.Registry.ValueName.Equals("DisplayName", StringComparison.OrdinalIgnoreCase) &&
            rule.Registry.Operator == IntuneDetectionOperator.Equals &&
            !string.IsNullOrWhiteSpace(rule.Registry.Value));

        var hasPublisher = allRules.Any(rule =>
            rule.RuleType == IntuneDetectionRuleType.Registry &&
            rule.Registry.ValueName.Equals("Publisher", StringComparison.OrdinalIgnoreCase) &&
            rule.Registry.Operator == IntuneDetectionOperator.Equals &&
            !string.IsNullOrWhiteSpace(rule.Registry.Value));

        return hasDisplayVersion && hasDisplayName && hasPublisher;
    }

    private static void AddRequirementChecks(
        IntuneRequirementRules requirements,
        ICollection<PreflightCheck> checks)
    {
        if (string.IsNullOrWhiteSpace(requirements.OperatingSystemArchitecture))
        {
            checks.Add(Error(
                "requirement-architecture",
                "Requirements",
                "Operating system architecture is required.",
                titleKey: "Core.Preflight.Title.Requirements",
                messageKey: "Core.Preflight.Message.RequirementArchitectureRequired"));
        }
        else if (!SupportedArchitectures.Contains(requirements.OperatingSystemArchitecture.Trim()))
        {
            checks.Add(Error(
                "requirement-architecture",
                "Requirements",
                "Operating system architecture is invalid. Use x64, x86, or Both.",
                titleKey: "Core.Preflight.Title.Requirements",
                messageKey: "Core.Preflight.Message.RequirementArchitectureInvalid"));
        }
        else
        {
            checks.Add(Pass("requirement-architecture", "Requirements", $"Operating system architecture is set to '{requirements.OperatingSystemArchitecture}'."));
        }

        checks.Add(string.IsNullOrWhiteSpace(requirements.MinimumOperatingSystem)
            ? Error(
                "requirement-os",
                "Requirements",
                "Minimum operating system is required.",
                titleKey: "Core.Preflight.Title.Requirements",
                messageKey: "Core.Preflight.Message.RequirementMinimumOsRequired")
            : Pass("requirement-os", "Requirements", $"Minimum operating system is set to '{requirements.MinimumOperatingSystem}'."));

        checks.Add(requirements.MinimumFreeDiskSpaceMb < 0
            ? Error(
                "requirement-disk",
                "Requirements",
                "Minimum free disk space cannot be negative.",
                titleKey: "Core.Preflight.Title.Requirements",
                messageKey: "Core.Preflight.Message.RequirementDiskNegative")
            : Pass("requirement-disk", "Requirements", requirements.MinimumFreeDiskSpaceMb > 0
                ? $"Minimum free disk space is set to {requirements.MinimumFreeDiskSpaceMb} MB."
                : "Minimum free disk space is not configured (optional)."));

        checks.Add(requirements.MinimumMemoryMb < 0
            ? Error(
                "requirement-memory",
                "Requirements",
                "Minimum memory cannot be negative.",
                titleKey: "Core.Preflight.Title.Requirements",
                messageKey: "Core.Preflight.Message.RequirementMemoryNegative")
            : Pass("requirement-memory", "Requirements", requirements.MinimumMemoryMb > 0
                ? $"Minimum memory is set to {requirements.MinimumMemoryMb} MB."
                : "Minimum memory is not configured (optional)."));

        checks.Add(requirements.MinimumCpuSpeedMhz < 0
            ? Error(
                "requirement-cpu",
                "Requirements",
                "Minimum CPU speed cannot be negative.",
                titleKey: "Core.Preflight.Title.Requirements",
                messageKey: "Core.Preflight.Message.RequirementCpuNegative")
            : Pass("requirement-cpu", "Requirements", requirements.MinimumCpuSpeedMhz > 0
                ? $"Minimum CPU speed is set to {requirements.MinimumCpuSpeedMhz} MHz."
                : "Minimum CPU speed is not configured (optional)."));

        checks.Add(requirements.MinimumLogicalProcessors < 0
            ? Error(
                "requirement-processors",
                "Requirements",
                "Minimum logical processors cannot be negative.",
                titleKey: "Core.Preflight.Title.Requirements",
                messageKey: "Core.Preflight.Message.RequirementProcessorsNegative")
            : Pass("requirement-processors", "Requirements", requirements.MinimumLogicalProcessors > 0
                ? $"Minimum logical processors is set to {requirements.MinimumLogicalProcessors}."
                : "Minimum logical processors is not configured (optional)."));

        if (string.IsNullOrWhiteSpace(requirements.RequirementScriptBody))
        {
            checks.Add(Pass("requirement-script", "Requirement Script", "Requirement script is not configured (optional)."));
        }
        else if (ContainsPlaceholder(requirements.RequirementScriptBody))
        {
            checks.Add(Error(
                "requirement-script",
                "Requirement Script",
                "Requirement script still contains placeholders.",
                titleKey: "Core.Preflight.Title.RequirementScript",
                messageKey: "Core.Preflight.Message.RequirementScriptPlaceholder"));
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

    private static PreflightCheck Pass(string key, string title, string message, string titleKey = "", string messageKey = "")
    {
        return new PreflightCheck
        {
            Key = key,
            Title = title,
            TitleKey = titleKey,
            Message = message,
            MessageKey = messageKey,
            Passed = true,
            Severity = PreflightSeverity.Info
        };
    }

    private static PreflightCheck Warning(string key, string title, string message, string titleKey = "", string messageKey = "")
    {
        return new PreflightCheck
        {
            Key = key,
            Title = title,
            TitleKey = titleKey,
            Message = message,
            MessageKey = messageKey,
            Passed = false,
            Severity = PreflightSeverity.Warning
        };
    }

    private static PreflightCheck Error(string key, string title, string message, string titleKey = "", string messageKey = "")
    {
        return new PreflightCheck
        {
            Key = key,
            Title = title,
            TitleKey = titleKey,
            Message = message,
            MessageKey = messageKey,
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

    private static bool IsGenericDetectionPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Replace('/', '\\');
        return normalized.Equals(@"%ProgramFiles%", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(@"%ProgramFiles(x86)%", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(@"C:\Program Files", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(@"C:\Program Files (x86)", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(@"C:\Windows", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserScopedPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Replace('/', '\\');
        return normalized.Contains("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("%APPDATA%", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsUserScopedScriptMarkers(string? scriptBody)
    {
        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            return false;
        }

        var hasUserScope = scriptBody.Contains("HKCU:\\", StringComparison.OrdinalIgnoreCase) ||
                           scriptBody.Contains("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) ||
                           scriptBody.Contains("$env:LOCALAPPDATA", StringComparison.OrdinalIgnoreCase) ||
                           scriptBody.Contains("$env:APPDATA", StringComparison.OrdinalIgnoreCase);
        var hasMachineScope = scriptBody.Contains("HKLM:\\", StringComparison.OrdinalIgnoreCase) ||
                              scriptBody.Contains("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) ||
                              scriptBody.Contains("%ProgramFiles%", StringComparison.OrdinalIgnoreCase) ||
                              scriptBody.Contains("C:\\Program Files", StringComparison.OrdinalIgnoreCase);
        return hasUserScope && !hasMachineScope;
    }

    private static bool IsGenericDetectionName(string? value, string? path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!GenericDetectionNames.Contains(value.Trim()))
        {
            return false;
        }

        return !IsSpecificInstallDetectionPath(path);
    }

    private static bool IsSpecificInstallDetectionPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsGenericDetectionPath(value))
        {
            return false;
        }

        var normalized = value.Trim().Replace('/', '\\');
        return normalized.Contains("%ProgramFiles%", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("%ProgramFiles(x86)%", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\Program Files\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\Program Files (x86)\", StringComparison.OrdinalIgnoreCase);
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
