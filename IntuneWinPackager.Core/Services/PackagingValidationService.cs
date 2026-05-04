using System.IO;
using System.Text.RegularExpressions;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Core.Utilities;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Core.Services;

public sealed class PackagingValidationService : IValidationService
{
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

    public ValidationResult Validate(PackagingRequest request)
    {
        var issues = new List<ValidationIssue>();

        if (request is null)
        {
            AddIssue(issues, "Core.Validation.RequestRequired", "Packaging request is required.");
            return ValidationResult.FromIssues(issues);
        }

        if (string.IsNullOrWhiteSpace(request.IntuneWinAppUtilPath))
        {
            AddIssue(issues, "Core.Validation.ToolPathRequired", "Path to IntuneWinAppUtil.exe is required.");
        }
        else if (!File.Exists(request.IntuneWinAppUtilPath))
        {
            AddIssue(issues, "Core.Validation.ToolPathNotFound", "IntuneWinAppUtil.exe was not found at the configured path.");
        }

        var config = request.Configuration;

        if (string.IsNullOrWhiteSpace(config.SourceFolder))
        {
            AddIssue(issues, "Core.Validation.SourceFolderRequired", "Source folder is required.");
        }
        else if (!Directory.Exists(config.SourceFolder))
        {
            AddIssue(issues, "Core.Validation.SourceFolderMissing", "Selected source folder does not exist.");
        }

        if (string.IsNullOrWhiteSpace(config.SetupFilePath))
        {
            AddIssue(issues, "Core.Validation.SetupFileRequired", "Setup file is required.");
        }
        else if (!File.Exists(config.SetupFilePath))
        {
            AddIssue(issues, "Core.Validation.SetupFileMissing", "Selected setup file was not found.");
        }

        var extension = Path.GetExtension(config.SetupFilePath ?? string.Empty).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            AddIssue(
                issues,
                "Core.Validation.SetupFileTypeUnsupported",
                "Supported setup file types: .msi, .exe, .appx, .appxbundle, .msix, .msixbundle, .ps1, .cmd, .bat, .vbs, .wsf.");
        }

        if (request.InstallerType == InstallerType.Unknown)
        {
            AddIssue(issues, "Core.Validation.InstallerTypeUnknown", "Installer type could not be determined from the selected file.");
        }

        if (string.IsNullOrWhiteSpace(config.OutputFolder))
        {
            AddIssue(issues, "Core.Validation.OutputFolderRequired", "Output folder is required.");
        }
        else
        {
            try
            {
                _ = Path.GetFullPath(config.OutputFolder);
            }
            catch
            {
                AddIssue(issues, "Core.Validation.OutputFolderInvalid", "Output folder path is invalid.");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.SourceFolder) && !string.IsNullOrWhiteSpace(config.SetupFilePath))
        {
            if (!IsPathInsideFolder(config.SetupFilePath, config.SourceFolder))
            {
                AddIssue(issues, "Core.Validation.SetupFileOutsideSource", "Setup file must be inside the selected source folder.");
            }
        }

        if (string.IsNullOrWhiteSpace(config.InstallCommand))
        {
            AddIssue(issues, "Core.Validation.InstallCommandRequired", "Install command is required.");
        }
        else if (ContainsPlaceholder(config.InstallCommand))
        {
            AddIssue(
                issues,
                "Core.Validation.InstallCommandHasPlaceholder",
                "Install command still contains placeholders. Replace placeholders with production-ready arguments.");
        }

        if (string.IsNullOrWhiteSpace(config.UninstallCommand))
        {
            AddIssue(issues, "Core.Validation.UninstallCommandRequired", "Uninstall command is required.");
        }
        else if (ContainsPlaceholder(config.UninstallCommand))
        {
            AddIssue(
                issues,
                "Core.Validation.UninstallCommandHasPlaceholder",
                "Uninstall command still contains placeholders. Replace placeholders with production-ready arguments.");
        }

        ValidateIntuneRules(request.InstallerType, config.IntuneRules, issues);

        return ValidationResult.FromIssues(issues);
    }

    private static void ValidateIntuneRules(
        InstallerType installerType,
        IntuneWin32AppRules rules,
        ICollection<ValidationIssue> issues)
    {
        if (rules.MaxRunTimeMinutes is < 1 or > 1440)
        {
            AddIssue(issues, "Core.Validation.MaxRunTimeInvalid", "Maximum run time must be between 1 and 1440 minutes.");
        }

        if (installerType == InstallerType.Exe &&
            rules.RequireSilentSwitchReview &&
            !rules.SilentSwitchesVerified)
        {
            AddIssue(
                issues,
                "Core.Validation.ExeSwitchVerificationRequired",
                "Silent install/uninstall switches must be verified for this EXE installer profile.");
        }

        ValidateRequirementRules(rules.Requirements, issues);

        var detection = rules.DetectionRule;
        if (detection.RuleType == IntuneDetectionRuleType.None)
        {
            AddIssue(issues, "Core.Validation.DetectionRuleRequired", "A detection rule is required for reliable Intune deployment.");
            return;
        }

        switch (detection.RuleType)
        {
            case IntuneDetectionRuleType.MsiProductCode:
                if (installerType != InstallerType.Msi)
                {
                    AddIssue(issues, "Core.Validation.MsiDetectionInstallerMismatch", "MSI product code detection can only be used for MSI installers.");
                }

                if (string.IsNullOrWhiteSpace(detection.Msi.ProductCode))
                {
                    AddIssue(issues, "Core.Validation.MsiDetectionProductCodeRequired", "MSI detection requires a product code.");
                }
                else if (!ProductCodeRegex.IsMatch(detection.Msi.ProductCode.Trim()))
                {
                    AddIssue(
                        issues,
                        "Core.Validation.MsiDetectionProductCodeFormat",
                        "MSI product code format is invalid. Expected format: {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}.");
                }
                break;

            case IntuneDetectionRuleType.File:
                if (string.IsNullOrWhiteSpace(detection.File.Path))
                {
                    AddIssue(issues, "Core.Validation.FileDetectionPathRequired", "File detection requires a folder path.");
                }

                if (string.IsNullOrWhiteSpace(detection.File.FileOrFolderName))
                {
                    AddIssue(issues, "Core.Validation.FileDetectionNameRequired", "File detection requires a file or folder name.");
                }

                if (detection.File.Operator != IntuneDetectionOperator.Exists &&
                    string.IsNullOrWhiteSpace(detection.File.Value))
                {
                    AddIssue(issues, "Core.Validation.FileDetectionValueRequired", "File detection operator requires a comparison value.");
                }

                if (IsGenericDetectionPath(detection.File.Path))
                {
                    AddIssue(
                        issues,
                        "Core.Validation.FileDetectionPathTooGeneric",
                        "File detection path is too generic. Use a unique, vendor-specific install path.");
                }

                if (IsGenericDetectionName(detection.File.FileOrFolderName))
                {
                    AddIssue(
                        issues,
                        "Core.Validation.FileDetectionNameTooGeneric",
                        "File detection name is too generic. Target a unique binary or folder.");
                }
                break;

            case IntuneDetectionRuleType.Registry:
                if (string.IsNullOrWhiteSpace(detection.Registry.Hive))
                {
                    AddIssue(issues, "Core.Validation.RegistryDetectionHiveRequired", "Registry detection requires a hive.");
                }

                if (string.IsNullOrWhiteSpace(detection.Registry.KeyPath))
                {
                    AddIssue(issues, "Core.Validation.RegistryDetectionKeyPathRequired", "Registry detection requires a key path.");
                }

                if (detection.Registry.Operator != IntuneDetectionOperator.Exists &&
                    string.IsNullOrWhiteSpace(detection.Registry.ValueName))
                {
                    AddIssue(issues, "Core.Validation.RegistryDetectionValueNameRequired", "Registry comparison detection requires a value name.");
                }

                if (detection.Registry.Operator != IntuneDetectionOperator.Exists &&
                    string.IsNullOrWhiteSpace(detection.Registry.Value))
                {
                    AddIssue(issues, "Core.Validation.RegistryDetectionValueRequired", "Registry comparison detection requires a comparison value.");
                }

                if (installerType == InstallerType.Exe)
                {
                    if (detection.Registry.Operator != IntuneDetectionOperator.Equals)
                    {
                        AddIssue(
                            issues,
                            "Core.Validation.RegistryDetectionExeRequiresEquals",
                            "For EXE detection, use Registry operator Equals with an exact value.");
                    }

                    if (!detection.Registry.ValueName.Equals("DisplayVersion", StringComparison.OrdinalIgnoreCase))
                    {
                        AddIssue(
                            issues,
                            "Core.Validation.RegistryDetectionExeRequiresDisplayVersion",
                            "For EXE detection, use Value Name 'DisplayVersion' to enforce exact version detection.");
                    }

                    if (string.IsNullOrWhiteSpace(detection.Registry.Value))
                    {
                        AddIssue(
                            issues,
                            "Core.Validation.RegistryDetectionExeVersionRequired",
                            "For EXE detection, DisplayVersion value is required.");
                    }
                }
                break;

            case IntuneDetectionRuleType.Script:
                if (string.IsNullOrWhiteSpace(detection.Script.ScriptBody))
                {
                    AddIssue(issues, "Core.Validation.ScriptDetectionBodyRequired", "Script detection requires script content.");
                }
                else if (ContainsPlaceholder(detection.Script.ScriptBody))
                {
                    AddIssue(
                        issues,
                        "Core.Validation.ScriptDetectionHasPlaceholder",
                        "Script detection still contains placeholders. Replace placeholders with production detection logic.");
                }

                if (!DeterministicDetectionScript.IsIntuneCompliantSuccessSignalScript(detection.Script.ScriptBody))
                {
                    AddIssue(
                        issues,
                        "Core.Validation.ScriptDetectionIntuneSuccessSignalRequired",
                        "Script detection must write output to STDOUT and exit with code 0 on success.");
                }

                if (installerType == InstallerType.Exe &&
                    !DeterministicDetectionScript.IsExactExeRegistryScript(detection.Script.ScriptBody))
                {
                    AddIssue(
                        issues,
                        "Core.Validation.ScriptDetectionExeMustBeDeterministic",
                        "For EXE installers, script detection must use exact registry equality (DisplayName, Publisher, DisplayVersion).");
                }
                else if (installerType == InstallerType.AppxMsix &&
                         !DeterministicDetectionScript.IsExactAppxIdentityScript(detection.Script.ScriptBody))
                {
                    AddIssue(
                        issues,
                        "Core.Validation.ScriptDetectionAppxMustCheckVersion",
                        "APPX/MSIX script detection must check exact package identity and version.");
                }
                else if (installerType != InstallerType.AppxMsix &&
                         installerType != InstallerType.Script &&
                         installerType != InstallerType.Exe)
                {
                    AddIssue(
                        issues,
                        "Core.Validation.ScriptDetectionLastResortOnly",
                        "Script detection is only recommended as a last resort. Use MSI, Registry, or File detection for this installer type.");
                }
                break;
        }
    }

    private static void ValidateRequirementRules(
        IntuneRequirementRules requirements,
        ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(requirements.OperatingSystemArchitecture))
        {
            AddIssue(issues, "Core.Validation.RequirementArchitectureRequired", "Requirement architecture is required.");
        }
        else if (!SupportedArchitectures.Contains(requirements.OperatingSystemArchitecture.Trim()))
        {
            AddIssue(issues, "Core.Validation.RequirementArchitectureInvalid", "Requirement architecture is invalid. Use x64, x86, or Both.");
        }

        if (string.IsNullOrWhiteSpace(requirements.MinimumOperatingSystem))
        {
            AddIssue(issues, "Core.Validation.RequirementMinimumOsRequired", "Minimum operating system requirement is required.");
        }

        if (requirements.MinimumFreeDiskSpaceMb < 0)
        {
            AddIssue(issues, "Core.Validation.RequirementDiskNegative", "Minimum free disk space cannot be negative.");
        }

        if (requirements.MinimumMemoryMb < 0)
        {
            AddIssue(issues, "Core.Validation.RequirementMemoryNegative", "Minimum memory cannot be negative.");
        }

        if (requirements.MinimumCpuSpeedMhz < 0)
        {
            AddIssue(issues, "Core.Validation.RequirementCpuNegative", "Minimum CPU speed cannot be negative.");
        }

        if (requirements.MinimumLogicalProcessors < 0)
        {
            AddIssue(issues, "Core.Validation.RequirementProcessorsNegative", "Minimum logical processors cannot be negative.");
        }

        if (!string.IsNullOrWhiteSpace(requirements.RequirementScriptBody) &&
            ContainsPlaceholder(requirements.RequirementScriptBody))
        {
            AddIssue(
                issues,
                "Core.Validation.RequirementScriptHasPlaceholder",
                "Requirement script still contains placeholders. Replace placeholders with production requirement logic.");
        }
    }

    private static void AddIssue(ICollection<ValidationIssue> issues, string key, string message)
    {
        issues.Add(new ValidationIssue
        {
            Key = key,
            Message = message
        });
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

    private static bool IsGenericDetectionName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return GenericDetectionNames.Contains(value.Trim());
    }

    private static bool IsPathInsideFolder(string filePath, string folderPath)
    {
        var folderFullPath = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var fileFullPath = Path.GetFullPath(filePath);

        return fileFullPath.StartsWith(folderFullPath, StringComparison.OrdinalIgnoreCase);
    }
}

