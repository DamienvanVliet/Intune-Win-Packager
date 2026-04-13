using System.IO;
using System.Text.RegularExpressions;
using IntuneWinPackager.Core.Interfaces;
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

    public ValidationResult Validate(PackagingRequest request)
    {
        var errors = new List<string>();

        if (request is null)
        {
            errors.Add("Packaging request is required.");
            return ValidationResult.FromErrors(errors);
        }

        if (string.IsNullOrWhiteSpace(request.IntuneWinAppUtilPath))
        {
            errors.Add("Path to IntuneWinAppUtil.exe is required.");
        }
        else if (!File.Exists(request.IntuneWinAppUtilPath))
        {
            errors.Add("IntuneWinAppUtil.exe was not found at the configured path.");
        }

        var config = request.Configuration;

        if (string.IsNullOrWhiteSpace(config.SourceFolder))
        {
            errors.Add("Source folder is required.");
        }
        else if (!Directory.Exists(config.SourceFolder))
        {
            errors.Add("Selected source folder does not exist.");
        }

        if (string.IsNullOrWhiteSpace(config.SetupFilePath))
        {
            errors.Add("Setup file is required.");
        }
        else if (!File.Exists(config.SetupFilePath))
        {
            errors.Add("Selected setup file was not found.");
        }

        var extension = Path.GetExtension(config.SetupFilePath ?? string.Empty).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            errors.Add("Supported setup file types: .msi, .exe, .appx, .appxbundle, .msix, .msixbundle, .ps1, .cmd, .bat, .vbs, .wsf.");
        }

        if (request.InstallerType == InstallerType.Unknown)
        {
            errors.Add("Installer type could not be determined from the selected file.");
        }

        if (string.IsNullOrWhiteSpace(config.OutputFolder))
        {
            errors.Add("Output folder is required.");
        }
        else
        {
            try
            {
                _ = Path.GetFullPath(config.OutputFolder);
            }
            catch
            {
                errors.Add("Output folder path is invalid.");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.SourceFolder) && !string.IsNullOrWhiteSpace(config.SetupFilePath))
        {
            if (!IsPathInsideFolder(config.SetupFilePath, config.SourceFolder))
            {
                errors.Add("Setup file must be inside the selected source folder.");
            }
        }

        if (string.IsNullOrWhiteSpace(config.InstallCommand))
        {
            errors.Add("Install command is required.");
        }
        else if (ContainsPlaceholder(config.InstallCommand))
        {
            errors.Add("Install command still contains placeholders. Replace placeholders with production-ready arguments.");
        }

        if (string.IsNullOrWhiteSpace(config.UninstallCommand))
        {
            errors.Add("Uninstall command is required.");
        }
        else if (ContainsPlaceholder(config.UninstallCommand))
        {
            errors.Add("Uninstall command still contains placeholders. Replace placeholders with production-ready arguments.");
        }

        ValidateIntuneRules(request.InstallerType, config.IntuneRules, errors);

        return ValidationResult.FromErrors(errors);
    }

    private static void ValidateIntuneRules(
        InstallerType installerType,
        IntuneWin32AppRules rules,
        ICollection<string> errors)
    {
        if (rules.MaxRunTimeMinutes is < 1 or > 1440)
        {
            errors.Add("Maximum run time must be between 1 and 1440 minutes.");
        }

        if (installerType == InstallerType.Exe &&
            rules.RequireSilentSwitchReview &&
            !rules.SilentSwitchesVerified)
        {
            errors.Add("Silent install/uninstall switches must be verified for this EXE installer profile.");
        }

        ValidateRequirementRules(rules.Requirements, errors);

        var detection = rules.DetectionRule;
        if (detection.RuleType == IntuneDetectionRuleType.None)
        {
            errors.Add("A detection rule is required for reliable Intune deployment.");
            return;
        }

        switch (detection.RuleType)
        {
            case IntuneDetectionRuleType.MsiProductCode:
                if (installerType != InstallerType.Msi)
                {
                    errors.Add("MSI product code detection can only be used for MSI installers.");
                }

                if (string.IsNullOrWhiteSpace(detection.Msi.ProductCode))
                {
                    errors.Add("MSI detection requires a product code.");
                }
                else if (!ProductCodeRegex.IsMatch(detection.Msi.ProductCode.Trim()))
                {
                    errors.Add("MSI product code format is invalid. Expected format: {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}.");
                }
                break;

            case IntuneDetectionRuleType.File:
                if (string.IsNullOrWhiteSpace(detection.File.Path))
                {
                    errors.Add("File detection requires a folder path.");
                }

                if (string.IsNullOrWhiteSpace(detection.File.FileOrFolderName))
                {
                    errors.Add("File detection requires a file or folder name.");
                }

                if (detection.File.Operator != IntuneDetectionOperator.Exists &&
                    string.IsNullOrWhiteSpace(detection.File.Value))
                {
                    errors.Add("File detection operator requires a comparison value.");
                }
                break;

            case IntuneDetectionRuleType.Registry:
                if (string.IsNullOrWhiteSpace(detection.Registry.Hive))
                {
                    errors.Add("Registry detection requires a hive.");
                }

                if (string.IsNullOrWhiteSpace(detection.Registry.KeyPath))
                {
                    errors.Add("Registry detection requires a key path.");
                }

                if (detection.Registry.Operator != IntuneDetectionOperator.Exists &&
                    string.IsNullOrWhiteSpace(detection.Registry.ValueName))
                {
                    errors.Add("Registry comparison detection requires a value name.");
                }

                if (detection.Registry.Operator != IntuneDetectionOperator.Exists &&
                    string.IsNullOrWhiteSpace(detection.Registry.Value))
                {
                    errors.Add("Registry comparison detection requires a comparison value.");
                }
                break;

            case IntuneDetectionRuleType.Script:
                if (string.IsNullOrWhiteSpace(detection.Script.ScriptBody))
                {
                    errors.Add("Script detection requires script content.");
                }
                else if (ContainsPlaceholder(detection.Script.ScriptBody))
                {
                    errors.Add("Script detection still contains placeholders. Replace placeholders with production detection logic.");
                }
                break;
        }
    }

    private static void ValidateRequirementRules(
        IntuneRequirementRules requirements,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(requirements.OperatingSystemArchitecture))
        {
            errors.Add("Requirement architecture is required.");
        }
        else if (!SupportedArchitectures.Contains(requirements.OperatingSystemArchitecture.Trim()))
        {
            errors.Add("Requirement architecture is invalid. Use x64, x86, or Both.");
        }

        if (string.IsNullOrWhiteSpace(requirements.MinimumOperatingSystem))
        {
            errors.Add("Minimum operating system requirement is required.");
        }

        if (requirements.MinimumFreeDiskSpaceMb < 0)
        {
            errors.Add("Minimum free disk space cannot be negative.");
        }

        if (requirements.MinimumMemoryMb < 0)
        {
            errors.Add("Minimum memory cannot be negative.");
        }

        if (requirements.MinimumCpuSpeedMhz < 0)
        {
            errors.Add("Minimum CPU speed cannot be negative.");
        }

        if (requirements.MinimumLogicalProcessors < 0)
        {
            errors.Add("Minimum logical processors cannot be negative.");
        }

        if (!string.IsNullOrWhiteSpace(requirements.RequirementScriptBody) &&
            ContainsPlaceholder(requirements.RequirementScriptBody))
        {
            errors.Add("Requirement script still contains placeholders. Replace placeholders with production requirement logic.");
        }
    }

    private static bool ContainsPlaceholder(string value)
    {
        return PlaceholderRegex.IsMatch(value);
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
