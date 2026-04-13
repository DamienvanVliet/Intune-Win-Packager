using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using Microsoft.Win32;

namespace IntuneWinPackager.Core.Services;

public sealed class InstallerCommandService : IInstallerCommandService
{
    private const string PlaceholderSilentArgs = "<silent-args>";
    private const string PlaceholderUninstallArgs = "<uninstall-args>";
    private const string PlaceholderPackageName = "<package-name>";
    private const string PlaceholderDetectionScript = "<detection-script>";

    private static readonly Regex GuidRegex = new("\\{[0-9A-Fa-f\\-]{36}\\}", RegexOptions.Compiled);

    private static readonly IReadOnlyList<SilentInstallPreset> ExePresets = new List<SilentInstallPreset>
    {
        new()
        {
            Name = "Inno Setup",
            Description = "Use when installer framework is Inno Setup.",
            InstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
            UninstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            RequiresVerification = true,
            Guidance = "Template for Inno Setup. Verify against the vendor build and documentation before production use."
        },
        new()
        {
            Name = "NSIS",
            Description = "Use when installer framework is NSIS.",
            InstallArguments = "/S",
            UninstallArguments = "/S",
            RequiresVerification = true,
            Guidance = "Template for NSIS. Verify against the vendor build and documentation before production use."
        },
        new()
        {
            Name = "InstallShield",
            Description = "Use when installer framework is InstallShield.",
            InstallArguments = "/s /v\"/qn REBOOT=ReallySuppress\"",
            UninstallArguments = "/s /x /v\"/qn REBOOT=ReallySuppress\"",
            RequiresVerification = true,
            Guidance = "Template for InstallShield. Verify against the vendor build and documentation before production use."
        },
        new()
        {
            Name = "WiX Burn",
            Description = "Use for WiX Burn bootstrapper installers.",
            InstallArguments = "/quiet /norestart",
            UninstallArguments = "/uninstall /quiet /norestart",
            RequiresVerification = true,
            Guidance = "Template for WiX Burn bootstrapper packages. Verify against the vendor build and documentation before production use."
        },
        new()
        {
            Name = "Custom (Manual)",
            Description = "Manual template for unknown EXE installer frameworks.",
            InstallArguments = PlaceholderSilentArgs,
            UninstallArguments = PlaceholderUninstallArgs,
            RequiresVerification = true,
            Guidance = "Unknown EXE framework. Determine vendor-specific switches and update commands before deployment."
        }
    };

    private static readonly IReadOnlyList<ExeFrameworkTemplate> ExeFrameworkTemplates = new List<ExeFrameworkTemplate>
    {
        new()
        {
            Framework = ExeInstallerFramework.InnoSetup,
            Name = "EXE - Inno Setup",
            InstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
            UninstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            DetectionMarkers = ["inno setup", "innounp", "isxdl"],
            Guidance = "Inno Setup framework detected from installer metadata/signatures."
        },
        new()
        {
            Framework = ExeInstallerFramework.Nsis,
            Name = "EXE - NSIS",
            InstallArguments = "/S",
            UninstallArguments = "/S",
            DetectionMarkers = ["nullsoft", "nsis error", "software\\nsis"],
            Guidance = "NSIS framework detected from installer metadata/signatures."
        },
        new()
        {
            Framework = ExeInstallerFramework.InstallShield,
            Name = "EXE - InstallShield",
            InstallArguments = "/s /v\"/qn REBOOT=ReallySuppress\"",
            UninstallArguments = "/s /x /v\"/qn REBOOT=ReallySuppress\"",
            DetectionMarkers = ["installshield", "isscript"],
            Guidance = "InstallShield framework detected from installer metadata/signatures."
        },
        new()
        {
            Framework = ExeInstallerFramework.WixBurn,
            Name = "EXE - WiX Burn",
            InstallArguments = "/quiet /norestart",
            UninstallArguments = "/uninstall /quiet /norestart",
            DetectionMarkers = ["bootstrapperapplicationdata", "wixstdba", "burnpipe", "wixburn"],
            Guidance = "WiX Burn bootstrapper detected from installer metadata/signatures."
        }
    };

    public InstallerType DetectInstallerType(string setupFilePath)
    {
        if (string.IsNullOrWhiteSpace(setupFilePath))
        {
            return InstallerType.Unknown;
        }

        var extension = Path.GetExtension(setupFilePath);
        return extension.ToLowerInvariant() switch
        {
            ".msi" => InstallerType.Msi,
            ".exe" => InstallerType.Exe,
            ".appx" or ".appxbundle" or ".msix" or ".msixbundle" => InstallerType.AppxMsix,
            ".ps1" or ".cmd" or ".bat" or ".vbs" or ".wsf" => InstallerType.Script,
            _ => InstallerType.Unknown
        };
    }

    public IReadOnlyList<SilentInstallPreset> GetExeSilentPresets() => ExePresets;

    public CommandSuggestion CreateSuggestion(
        string setupFilePath,
        InstallerType installerType,
        MsiMetadata? msiMetadata = null,
        SilentInstallPreset? preset = null)
    {
        var setupFileName = Path.GetFileName(setupFilePath);
        if (string.IsNullOrWhiteSpace(setupFileName))
        {
            return new CommandSuggestion();
        }

        return installerType switch
        {
            InstallerType.Msi => CreateMsiSuggestion(setupFileName, msiMetadata),
            InstallerType.Exe => CreateExeSuggestion(setupFilePath, setupFileName, preset),
            InstallerType.AppxMsix => CreateAppxMsixSuggestion(setupFilePath, setupFileName),
            InstallerType.Script => CreateScriptSuggestion(setupFileName),
            _ => new CommandSuggestion
            {
                InstallCommand = $"\"{setupFileName}\"",
                UninstallCommand = PlaceholderUninstallArgs,
                SuggestedRules = new IntuneWin32AppRules
                {
                    AppliedTemplateName = "Unknown Installer",
                    TemplateGuidance = "Installer type could not be determined. Configure commands and detection manually.",
                    RequireSilentSwitchReview = true,
                    SilentSwitchesVerified = false,
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.None
                    }
                }
            }
        };
    }

    private static CommandSuggestion CreateMsiSuggestion(string setupFileName, MsiMetadata? msiMetadata)
    {
        var installCommand = $"msiexec /i \"{setupFileName}\" /quiet";
        var uninstallTarget = string.IsNullOrWhiteSpace(msiMetadata?.ProductCode)
            ? $"\"{setupFileName}\""
            : msiMetadata.ProductCode;

        var detectionRule = string.IsNullOrWhiteSpace(msiMetadata?.ProductCode)
            ? new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.None
            }
            : new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.MsiProductCode,
                Msi = new MsiDetectionRule
                {
                    ProductCode = msiMetadata.ProductCode,
                    ProductVersion = msiMetadata.ProductVersion
                }
            };

        var guidance = string.IsNullOrWhiteSpace(msiMetadata?.ProductCode)
            ? "MSI metadata was incomplete. Configure a detection rule before deployment."
            : "MSI metadata detected. Product code based detection rule was prefilled.";

        return new CommandSuggestion
        {
            InstallCommand = installCommand,
            UninstallCommand = $"msiexec /x {uninstallTarget} /quiet",
            SuggestedRules = new IntuneWin32AppRules
            {
                InstallContext = IntuneInstallContext.System,
                RestartBehavior = IntuneRestartBehavior.DetermineBehaviorBasedOnReturnCodes,
                MaxRunTimeMinutes = 60,
                RequireSilentSwitchReview = false,
                SilentSwitchesVerified = true,
                AppliedTemplateName = "MSI Standard (msiexec)",
                TemplateGuidance = guidance,
                DetectionRule = detectionRule
            }
        };
    }

    private static CommandSuggestion CreateExeSuggestion(string setupFilePath, string setupFileName, SilentInstallPreset? preset)
    {
        var template = preset is null
            ? DetectExeFrameworkTemplate(setupFilePath)
            : BuildTemplateFromPreset(preset);

        var quotedSetup = $"\"{setupFileName}\"";
        var installCommand = BuildCommand(quotedSetup, template.InstallArguments);
        var uninstallCommand = BuildCommand(quotedSetup, template.UninstallArguments);

        var installedEvidence = TryResolveInstalledAppEvidence(setupFilePath);
        var guidanceParts = new List<string>
        {
            template.Guidance,
            "EXE switches and uninstall behavior must be verified for the exact vendor build."
        };

        var detectionRule = new IntuneDetectionRule
        {
            RuleType = IntuneDetectionRuleType.None
        };

        if (installedEvidence is not null)
        {
            detectionRule = installedEvidence.DetectionRule;
            uninstallCommand = installedEvidence.UninstallCommand;
            guidanceParts.Add($"Local installed footprint matched '{installedEvidence.DisplayName}' and was used for registry detection + uninstall suggestion.");
        }
        else
        {
            guidanceParts.Add("No high-confidence installed footprint match found. Configure detection manually (file/registry/script)."
            );
        }

        return new CommandSuggestion
        {
            InstallCommand = installCommand,
            UninstallCommand = uninstallCommand,
            SuggestedRules = new IntuneWin32AppRules
            {
                InstallContext = IntuneInstallContext.System,
                RestartBehavior = IntuneRestartBehavior.DetermineBehaviorBasedOnReturnCodes,
                MaxRunTimeMinutes = 60,
                RequireSilentSwitchReview = true,
                SilentSwitchesVerified = false,
                AppliedTemplateName = template.Name,
                TemplateGuidance = string.Join(" ", guidanceParts),
                DetectionRule = detectionRule
            }
        };
    }

    private static CommandSuggestion CreateAppxMsixSuggestion(string setupFilePath, string setupFileName)
    {
        var packageIdentityName = TryReadAppxIdentityName(setupFilePath);
        var quotedSetup = $"\"{setupFileName}\"";

        var installCommand =
            $"powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Add-AppxPackage -Path {quotedSetup}\"";

        var uninstallCommand = string.IsNullOrWhiteSpace(packageIdentityName)
            ? $"powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Get-AppxPackage -Name '{PlaceholderPackageName}' | Remove-AppxPackage\""
            : $"powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Get-AppxPackage -Name '{packageIdentityName}' | Remove-AppxPackage\"";

        var detectionRule = new IntuneDetectionRule
        {
            RuleType = IntuneDetectionRuleType.Script,
            Script = new ScriptDetectionRule
            {
                ScriptBody = string.IsNullOrWhiteSpace(packageIdentityName)
                    ? BuildPlaceholderDetectionScript("Define AppX/MSIX detection script")
                    : BuildAppxDetectionScript(packageIdentityName),
                RunAs32BitOn64System = false,
                EnforceSignatureCheck = false
            }
        };

        var guidance = string.IsNullOrWhiteSpace(packageIdentityName)
            ? "APPX/MSIX detected. Package identity could not be extracted, update uninstall command and detection script manually."
            : "APPX/MSIX detected. Package identity was extracted and script detection was prefilled.";

        return new CommandSuggestion
        {
            InstallCommand = installCommand,
            UninstallCommand = uninstallCommand,
            SuggestedRules = new IntuneWin32AppRules
            {
                InstallContext = IntuneInstallContext.User,
                RestartBehavior = IntuneRestartBehavior.NoSpecificAction,
                MaxRunTimeMinutes = 30,
                RequireSilentSwitchReview = false,
                SilentSwitchesVerified = true,
                AppliedTemplateName = "APPX/MSIX (PowerShell Add-AppxPackage)",
                TemplateGuidance = guidance,
                DetectionRule = detectionRule
            }
        };
    }

    private static CommandSuggestion CreateScriptSuggestion(string setupFileName)
    {
        var extension = Path.GetExtension(setupFileName).ToLowerInvariant();

        var installCommand = extension switch
        {
            ".ps1" => $"powershell.exe -ExecutionPolicy Bypass -File \"{setupFileName}\"",
            ".cmd" or ".bat" => $"cmd.exe /c \"{setupFileName}\"",
            ".vbs" => $"cscript.exe //B \"{setupFileName}\"",
            _ => $"\"{setupFileName}\""
        };

        var uninstallCommand = extension switch
        {
            ".ps1" => "powershell.exe -ExecutionPolicy Bypass -File \"<uninstall-script.ps1>\"",
            ".cmd" or ".bat" => "cmd.exe /c \"<uninstall-script.cmd>\"",
            ".vbs" => "cscript.exe //B \"<uninstall-script.vbs>\"",
            _ => PlaceholderUninstallArgs
        };

        return new CommandSuggestion
        {
            InstallCommand = installCommand,
            UninstallCommand = uninstallCommand,
            SuggestedRules = new IntuneWin32AppRules
            {
                InstallContext = IntuneInstallContext.System,
                RestartBehavior = IntuneRestartBehavior.DetermineBehaviorBasedOnReturnCodes,
                MaxRunTimeMinutes = 60,
                RequireSilentSwitchReview = false,
                SilentSwitchesVerified = true,
                AppliedTemplateName = "Script Installer",
                TemplateGuidance = "Script-based installer detected. Confirm execution context, idempotency, and provide a script detection rule.",
                DetectionRule = new IntuneDetectionRule
                {
                    RuleType = IntuneDetectionRuleType.Script,
                    Script = new ScriptDetectionRule
                    {
                        ScriptBody = BuildPlaceholderDetectionScript("Define script installer detection logic"),
                        RunAs32BitOn64System = false,
                        EnforceSignatureCheck = false
                    }
                }
            }
        };
    }

    private static ExeFrameworkTemplate DetectExeFrameworkTemplate(string setupFilePath)
    {
        var markersText = ReadBinaryMarkerText(setupFilePath);
        var fileName = Path.GetFileNameWithoutExtension(setupFilePath)?.ToLowerInvariant() ?? string.Empty;
        var versionInfo = TryGetVersionInfo(setupFilePath);
        var productName = versionInfo?.ProductName?.ToLowerInvariant() ?? string.Empty;
        var fileDescription = versionInfo?.FileDescription?.ToLowerInvariant() ?? string.Empty;

        foreach (var template in ExeFrameworkTemplates)
        {
            if (template.DetectionMarkers.Any(marker =>
                    markersText.Contains(marker, StringComparison.Ordinal) ||
                    fileName.Contains(marker, StringComparison.Ordinal) ||
                    productName.Contains(marker, StringComparison.Ordinal) ||
                    fileDescription.Contains(marker, StringComparison.Ordinal)))
            {
                return template;
            }
        }

        return new ExeFrameworkTemplate
        {
            Framework = ExeInstallerFramework.Unknown,
            Name = "EXE - Unknown Framework",
            InstallArguments = PlaceholderSilentArgs,
            UninstallArguments = PlaceholderUninstallArgs,
            Guidance = "Installer framework could not be determined. Provide vendor-specific silent install/uninstall switches."
        };
    }

    private static ExeFrameworkTemplate BuildTemplateFromPreset(SilentInstallPreset preset)
    {
        return new ExeFrameworkTemplate
        {
            Framework = ExeInstallerFramework.Manual,
            Name = $"EXE - {preset.Name}",
            InstallArguments = preset.InstallArguments,
            UninstallArguments = preset.UninstallArguments,
            Guidance = preset.Guidance
        };
    }

    private static string BuildCommand(string quotedSetupFileName, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return quotedSetupFileName;
        }

        return $"{quotedSetupFileName} {arguments}".Trim();
    }

    private static string ReadBinaryMarkerText(string setupFilePath)
    {
        try
        {
            const int maxBytes = 4 * 1024 * 1024;
            using var stream = File.OpenRead(setupFilePath);
            var length = (int)Math.Min(maxBytes, Math.Max(stream.Length, 0L));
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return string.Empty;
            }

            return Encoding.Latin1.GetString(buffer, 0, read).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static FileVersionInfo? TryGetVersionInfo(string setupFilePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(setupFilePath);
        }
        catch
        {
            return null;
        }
    }

    private static InstalledAppEvidence? TryResolveInstalledAppEvidence(string setupFilePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return TryResolveInstalledAppEvidenceWindows(setupFilePath);
    }

    [SupportedOSPlatform("windows")]
    private static InstalledAppEvidence? TryResolveInstalledAppEvidenceWindows(string setupFilePath)
    {
        var version = TryGetVersionInfo(setupFilePath);
        var fileBaseName = Path.GetFileNameWithoutExtension(setupFilePath) ?? string.Empty;

        var terms = new[]
        {
            Normalize(version?.ProductName),
            Normalize(version?.FileDescription),
            Normalize(fileBaseName)
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        var company = Normalize(version?.CompanyName);

        RegistryUninstallEntry? bestMatch = null;
        var bestScore = 0;

        foreach (var entry in EnumerateUninstallEntries())
        {
            var score = ScoreUninstallEntry(entry, terms, company);
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestMatch = entry;
        }

        if (bestMatch is null || bestScore < 55)
        {
            return null;
        }

        return new InstalledAppEvidence
        {
            DisplayName = bestMatch.DisplayName,
            DetectionRule = new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.Registry,
                Registry = new RegistryDetectionRule
                {
                    Hive = bestMatch.HiveName,
                    KeyPath = bestMatch.KeyPath,
                    Operator = IntuneDetectionOperator.Exists
                }
            },
            UninstallCommand = NormalizeUninstallCommand(bestMatch.UninstallString)
        };
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<RegistryUninstallEntry> EnumerateUninstallEntries()
    {
        var roots = new[]
        {
            (Hive: RegistryHive.LocalMachine, View: RegistryView.Registry64, Path: @"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"),
            (Hive: RegistryHive.LocalMachine, View: RegistryView.Registry32, Path: @"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"),
            (Hive: RegistryHive.CurrentUser, View: RegistryView.Default, Path: @"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall")
        };

        foreach (var root in roots)
        {
            RegistryKey? uninstallRoot = null;
            try
            {
                uninstallRoot = RegistryKey
                    .OpenBaseKey(root.Hive, root.View)
                    .OpenSubKey(root.Path);
            }
            catch
            {
                // Ignore root access issues.
            }

            if (uninstallRoot is null)
            {
                continue;
            }

            using (uninstallRoot)
            {
                foreach (var subKeyName in uninstallRoot.GetSubKeyNames())
                {
                    RegistryKey? appKey = null;
                    try
                    {
                        appKey = uninstallRoot.OpenSubKey(subKeyName);
                    }
                    catch
                    {
                        // Ignore inaccessible subkeys.
                    }

                    if (appKey is null)
                    {
                        continue;
                    }

                    using (appKey)
                    {
                        var displayName = appKey.GetValue("DisplayName")?.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            continue;
                        }

                        yield return new RegistryUninstallEntry
                        {
                            HiveName = root.Hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE",
                            KeyPath = $"{root.Path}\\{subKeyName}",
                            DisplayName = displayName,
                            Publisher = appKey.GetValue("Publisher")?.ToString() ?? string.Empty,
                            UninstallString = appKey.GetValue("QuietUninstallString")?.ToString()
                                ?? appKey.GetValue("UninstallString")?.ToString()
                                ?? string.Empty
                        };
                    }
                }
            }
        }
    }

    private static int ScoreUninstallEntry(RegistryUninstallEntry entry, IReadOnlyCollection<string> terms, string company)
    {
        if (string.IsNullOrWhiteSpace(entry.DisplayName))
        {
            return 0;
        }

        var displayName = Normalize(entry.DisplayName);
        var publisher = Normalize(entry.Publisher);

        var score = 0;

        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term) || term.Length < 3)
            {
                continue;
            }

            if (displayName.Equals(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 70;
                continue;
            }

            if (displayName.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 35;
            }
        }

        if (!string.IsNullOrWhiteSpace(company) && publisher.Contains(company, StringComparison.OrdinalIgnoreCase))
        {
            score += 15;
        }

        if (!string.IsNullOrWhiteSpace(entry.UninstallString))
        {
            score += 5;
        }

        return score;
    }

    private static string NormalizeUninstallCommand(string uninstallString)
    {
        var trimmed = uninstallString.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return PlaceholderUninstallArgs;
        }

        if (trimmed.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var guidMatch = GuidRegex.Match(trimmed);
            if (guidMatch.Success)
            {
                return $"msiexec /x {guidMatch.Value} /quiet";
            }
        }

        return trimmed;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return builder.ToString().Trim();
    }

    private static string? TryReadAppxIdentityName(string setupFilePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(setupFilePath);
            var manifest = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase));

            if (manifest is null)
            {
                return null;
            }

            using var stream = manifest.Open();
            var document = XDocument.Load(stream);
            var identity = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Identity");
            return identity?.Attribute("Name")?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildAppxDetectionScript(string packageIdentityName)
    {
        return string.Join(Environment.NewLine,
        [
            "$package = Get-AppxPackage -Name \"" + packageIdentityName + "\" -ErrorAction SilentlyContinue",
            "if ($null -ne $package) {",
            "    Write-Output \"Detected\"",
            "    exit 0",
            "}",
            "exit 1"
        ]);
    }

    private static string BuildPlaceholderDetectionScript(string reason)
    {
        return string.Join(Environment.NewLine,
        [
            "# " + reason,
            "# Replace this script with your production detection logic.",
            PlaceholderDetectionScript,
            "exit 1"
        ]);
    }

    private sealed record ExeFrameworkTemplate
    {
        public ExeInstallerFramework Framework { get; init; } = ExeInstallerFramework.Unknown;

        public string Name { get; init; } = string.Empty;

        public string InstallArguments { get; init; } = string.Empty;

        public string UninstallArguments { get; init; } = string.Empty;

        public IReadOnlyList<string> DetectionMarkers { get; init; } = [];

        public string Guidance { get; init; } = string.Empty;
    }

    private sealed record RegistryUninstallEntry
    {
        public string HiveName { get; init; } = string.Empty;

        public string KeyPath { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string Publisher { get; init; } = string.Empty;

        public string UninstallString { get; init; } = string.Empty;
    }

    private sealed record InstalledAppEvidence
    {
        public string DisplayName { get; init; } = string.Empty;

        public IntuneDetectionRule DetectionRule { get; init; } = new();

        public string UninstallCommand { get; init; } = string.Empty;
    }

    private enum ExeInstallerFramework
    {
        Unknown = 0,
        InnoSetup = 1,
        Nsis = 2,
        InstallShield = 3,
        WixBurn = 4,
        Manual = 5
    }
}
