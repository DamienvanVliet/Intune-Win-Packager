using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using Microsoft.Win32;

namespace IntuneWinPackager.Core.Services;

public sealed class InstallerCommandService : IInstallerCommandService
{
    private const int ProbeTimeoutMs = 2200;
    private const int MaxKnowledgeEntries = 400;
    private const int SuggestionConfidenceHighThreshold = 85;
    private const int SuggestionConfidenceMediumThreshold = 60;

    private const string PlaceholderSilentArgs = "<silent-args>";
    private const string PlaceholderUninstallArgs = "<uninstall-args>";
    private const string PlaceholderPackageName = "<package-name>";
    private const string PlaceholderDetectionScript = "<detection-script>";

    private static readonly JsonSerializerOptions KnowledgeSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly Regex GuidRegex = new("\\{[0-9A-Fa-f\\-]{36}\\}", RegexOptions.Compiled);
    private static readonly Regex HelpSwitchTokenRegex = new(@"(?<!\w)(--?[a-z][a-z0-9\-]*|/[a-z][a-z0-9\-]*)(?:[ =][^\r\n\t ]+)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly object _knowledgeLock = new();
    private readonly string _knowledgeFilePath;
    private readonly Dictionary<string, string> _shaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ParameterProbeResult> _probeCache = new(StringComparer.OrdinalIgnoreCase);

    private bool _knowledgeLoaded;
    private KnowledgeStore _knowledgeStore = new();

    private static readonly IReadOnlyList<SilentInstallPreset> ExePresets = new List<SilentInstallPreset>
    {
        new()
        {
            Name = "Most Common (Very Silent)",
            Description = "First fallback for many setup-style installers.",
            InstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
            UninstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            RequiresVerification = true,
            Guidance = "Fallback profile. Use this when the default command does not work. Verify with one local test."
        },
        new()
        {
            Name = "Alternative (/S Silent)",
            Description = "Use when the installer supports /S style silent mode.",
            InstallArguments = "/S",
            UninstallArguments = "/S",
            RequiresVerification = true,
            Guidance = "Fallback profile for /S installers. Verify with one local test."
        },
        new()
        {
            Name = "Alternative (Shield Quiet)",
            Description = "Quiet mode profile for shield-style installers.",
            InstallArguments = "/s /v\"/qn REBOOT=ReallySuppress\"",
            UninstallArguments = "/s /x /v\"/qn REBOOT=ReallySuppress\"",
            RequiresVerification = true,
            Guidance = "Fallback profile for shield-style installers. Verify with one local test."
        },
        new()
        {
            Name = "Alternative (Bundle Quiet)",
            Description = "Quiet mode profile for bundle/bootstrapper installers.",
            InstallArguments = "/quiet /norestart",
            UninstallArguments = "/uninstall /quiet /norestart",
            RequiresVerification = true,
            Guidance = "Fallback profile for bundle installers. Verify with one local test."
        },
        new()
        {
            Name = "Alternative (Updater Silent)",
            Description = "Silent profile for updater-style installers.",
            InstallArguments = "--silent",
            UninstallArguments = "--uninstall --silent",
            RequiresVerification = true,
            Guidance = "Fallback profile for updater-style installers. Verify with one local test."
        },
        new()
        {
            Name = "Manual (Enter Yourself)",
            Description = "Leave template mode and enter your own install/uninstall arguments.",
            InstallArguments = PlaceholderSilentArgs,
            UninstallArguments = PlaceholderUninstallArgs,
            RequiresVerification = true,
            Guidance = "Manual mode. Use vendor documentation or local test results."
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
            Guidance = "Inno Setup framework detected from installer metadata/signatures.",
            BaseConfidenceScore = 84
        },
        new()
        {
            Framework = ExeInstallerFramework.Nsis,
            Name = "EXE - NSIS",
            InstallArguments = "/S",
            UninstallArguments = "/S",
            DetectionMarkers = ["nullsoft", "nsis error", "software\\nsis"],
            Guidance = "NSIS framework detected from installer metadata/signatures.",
            BaseConfidenceScore = 80
        },
        new()
        {
            Framework = ExeInstallerFramework.InstallShield,
            Name = "EXE - InstallShield",
            InstallArguments = "/s /v\"/qn REBOOT=ReallySuppress\"",
            UninstallArguments = "/s /x /v\"/qn REBOOT=ReallySuppress\"",
            DetectionMarkers = ["installshield", "isscript"],
            Guidance = "InstallShield framework detected from installer metadata/signatures.",
            BaseConfidenceScore = 75
        },
        new()
        {
            Framework = ExeInstallerFramework.WixBurn,
            Name = "EXE - WiX Burn",
            InstallArguments = "/quiet /norestart",
            UninstallArguments = "/uninstall /quiet /norestart",
            DetectionMarkers = ["bootstrapperapplicationdata", "wixstdba", "burnpipe", "wixburn"],
            Guidance = "WiX Burn bootstrapper detected from installer metadata/signatures.",
            BaseConfidenceScore = 78
        },
        new()
        {
            Framework = ExeInstallerFramework.Squirrel,
            Name = "EXE - Squirrel",
            InstallArguments = "--silent",
            UninstallArguments = "--uninstall --silent",
            DetectionMarkers = ["squirrel", "--squirrel-install", "--squirrel-uninstall", "update.exe --processstart"],
            Guidance = "Squirrel-style updater/installer markers detected. Verify vendor behavior carefully.",
            BaseConfidenceScore = 62
        },
        new()
        {
            Framework = ExeInstallerFramework.AdvancedInstaller,
            Name = "EXE - Advanced Installer",
            InstallArguments = "/quiet /norestart",
            UninstallArguments = "/uninstall /quiet /norestart",
            DetectionMarkers = ["advanced installer", "advinst", "aicustact.dll"],
            Guidance = "Advanced Installer markers detected from metadata/resources.",
            BaseConfidenceScore = 68
        }
    };

    public InstallerCommandService(string? knowledgeFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(knowledgeFilePath))
        {
            _knowledgeFilePath = knowledgeFilePath;
            return;
        }

        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IntuneWinPackager",
            "knowledge");
        _knowledgeFilePath = Path.Combine(defaultRoot, "installer-knowledge.v1.json");
    }

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

        var lookupContext = BuildKnowledgeLookupContext(setupFilePath, installerType);
        if (lookupContext is not null && TryGetKnowledgeEntry(lookupContext, out var cachedEntry))
        {
            var cacheGuidance = string.IsNullOrWhiteSpace(cachedEntry.TemplateGuidance)
                ? "Known-good commands reused from local knowledge cache for this installer hash + version."
                : cachedEntry.TemplateGuidance + " Known-good commands reused from local knowledge cache.";

            return new CommandSuggestion
            {
                InstallCommand = cachedEntry.InstallCommand,
                UninstallCommand = cachedEntry.UninstallCommand,
                SuggestedRules = cachedEntry.IntuneRules with
                {
                    TemplateGuidance = cacheGuidance
                },
                ConfidenceLevel = SuggestionConfidenceLevel.High,
                ConfidenceScore = 98,
                ConfidenceReason = "Matched verified knowledge cache entry.",
                FingerprintEngine = cachedEntry.FingerprintEngine,
                UsedKnowledgeCache = true,
                ParameterProbeDetected = true
            };
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
                ConfidenceLevel = SuggestionConfidenceLevel.Low,
                ConfidenceScore = 20,
                ConfidenceReason = "Installer type is unknown.",
                FingerprintEngine = "Unknown",
                ParameterProbeDetected = false,
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

    public void SaveVerifiedKnowledge(
        string setupFilePath,
        InstallerType installerType,
        string installCommand,
        string uninstallCommand,
        IntuneWin32AppRules intuneRules)
    {
        if (string.IsNullOrWhiteSpace(setupFilePath) ||
            !File.Exists(setupFilePath) ||
            installerType == InstallerType.Unknown)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(installCommand) ||
            string.IsNullOrWhiteSpace(uninstallCommand) ||
            ContainsPlaceholderArguments(installCommand) ||
            ContainsPlaceholderArguments(uninstallCommand))
        {
            return;
        }

        if (intuneRules.DetectionRule.RuleType == IntuneDetectionRuleType.None)
        {
            return;
        }

        if (installerType == InstallerType.Exe &&
            intuneRules.RequireSilentSwitchReview &&
            !intuneRules.SilentSwitchesVerified)
        {
            return;
        }

        var lookupContext = BuildKnowledgeLookupContext(setupFilePath, installerType);
        if (lookupContext is null)
        {
            return;
        }

        var fingerprint = installerType == InstallerType.Exe
            ? DetectExeFrameworkTemplate(setupFilePath)
            : null;

        var now = DateTimeOffset.UtcNow;
        lock (_knowledgeLock)
        {
            EnsureKnowledgeLoaded();

            var existing = _knowledgeStore.Entries.FirstOrDefault(entry =>
                entry.InstallerSha256.Equals(lookupContext.Sha256, StringComparison.OrdinalIgnoreCase) &&
                entry.ProductVersion.Equals(lookupContext.ProductVersion, StringComparison.OrdinalIgnoreCase) &&
                entry.InstallerType == installerType);

            if (existing is not null)
            {
                existing.InstallCommand = installCommand;
                existing.UninstallCommand = uninstallCommand;
                existing.IntuneRules = intuneRules;
                existing.LastVerifiedAtUtc = now;
                existing.UseCount++;
                existing.TemplateGuidance = intuneRules.TemplateGuidance;
                existing.FingerprintEngine = fingerprint?.Framework.ToString() ?? installerType.ToString();
            }
            else
            {
                _knowledgeStore.Entries.Add(new InstallerKnowledgeEntry
                {
                    InstallerSha256 = lookupContext.Sha256,
                    ProductVersion = lookupContext.ProductVersion,
                    ProductName = lookupContext.ProductName,
                    SetupFileName = Path.GetFileName(setupFilePath),
                    InstallerType = installerType,
                    InstallCommand = installCommand,
                    UninstallCommand = uninstallCommand,
                    IntuneRules = intuneRules,
                    LastVerifiedAtUtc = now,
                    UseCount = 1,
                    TemplateGuidance = intuneRules.TemplateGuidance,
                    FingerprintEngine = fingerprint?.Framework.ToString() ?? installerType.ToString()
                });
            }

            if (_knowledgeStore.Entries.Count > MaxKnowledgeEntries)
            {
                _knowledgeStore.Entries = _knowledgeStore.Entries
                    .OrderByDescending(entry => entry.LastVerifiedAtUtc)
                    .Take(MaxKnowledgeEntries)
                    .ToList();
            }

            PersistKnowledgeStoreUnsafe();
        }
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
            ConfidenceLevel = string.IsNullOrWhiteSpace(msiMetadata?.ProductCode)
                ? SuggestionConfidenceLevel.Medium
                : SuggestionConfidenceLevel.High,
            ConfidenceScore = string.IsNullOrWhiteSpace(msiMetadata?.ProductCode) ? 72 : 96,
            ConfidenceReason = string.IsNullOrWhiteSpace(msiMetadata?.ProductCode)
                ? "MSI detected but product code metadata was incomplete."
                : "MSI detected with product code metadata.",
            FingerprintEngine = "MSI",
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

    private CommandSuggestion CreateExeSuggestion(string setupFilePath, string setupFileName, SilentInstallPreset? preset)
    {
        var setupFileExists = File.Exists(setupFilePath);
        var template = preset is null
            ? (setupFileExists ? DetectExeFrameworkTemplate(setupFilePath) : new ExeFrameworkTemplate
            {
                Framework = ExeInstallerFramework.Unknown,
                Name = "EXE - Unknown Framework",
                InstallArguments = PlaceholderSilentArgs,
                UninstallArguments = PlaceholderUninstallArgs,
                Guidance = "Installer framework could not be determined. Provide vendor-specific silent install/uninstall switches.",
                BaseConfidenceScore = 32
            })
            : BuildTemplateFromPreset(preset);

        var quotedSetup = $"\"{setupFileName}\"";
        var installArguments = template.InstallArguments;
        var uninstallArguments = template.UninstallArguments;

        var probe = setupFileExists
            ? ProbeInstallerParameters(setupFilePath)
            : ParameterProbeResult.Empty;
        if (!string.IsNullOrWhiteSpace(probe.InstallArguments) &&
            (ContainsPlaceholderArguments(installArguments) || probe.ConfidenceScore >= template.BaseConfidenceScore))
        {
            installArguments = probe.InstallArguments;
        }

        if (!string.IsNullOrWhiteSpace(probe.UninstallArguments) &&
            (ContainsPlaceholderArguments(uninstallArguments) || probe.ConfidenceScore >= template.BaseConfidenceScore))
        {
            uninstallArguments = probe.UninstallArguments;
        }

        var installCommand = BuildCommand(quotedSetup, installArguments);
        var uninstallCommand = BuildCommand(quotedSetup, uninstallArguments);

        var installedEvidence = setupFileExists
            ? TryResolveInstalledAppEvidence(setupFilePath)
            : null;
        var guidanceParts = new List<string>
        {
            template.Guidance,
            "EXE switches and uninstall behavior must be verified for the exact vendor build."
        };

        if (probe.HasEvidence)
        {
            guidanceParts.Add($"Parameter probe detected switches from local help output ({string.Join(", ", probe.HelpSwitchesTried)}).");
        }

        var detectionRule = new IntuneDetectionRule
        {
            RuleType = IntuneDetectionRuleType.None
        };

        if (installedEvidence is not null)
        {
            detectionRule = installedEvidence.DetectionRule;
            uninstallCommand = installedEvidence.UninstallCommand;
            guidanceParts.Add(
                $"Deterministic local footprint matched '{installedEvidence.DisplayName}' " +
                $"(exact DisplayName/Publisher/DisplayVersion) and was used for registry detection + uninstall suggestion.");
        }
        else
        {
            guidanceParts.Add(
                "No deterministic installed footprint match was found. Configure an exact detection rule manually " +
                "(MSI Product Code, exact Registry value, or stable File path).");
        }

        var baseScore = Math.Max(template.BaseConfidenceScore, probe.ConfidenceScore);
        var adjustedScore = installedEvidence is not null
            ? Math.Min(99, baseScore + 8)
            : baseScore;
        var confidenceLevel = ToConfidenceLevel(adjustedScore);

        var confidenceReason = confidenceLevel switch
        {
            SuggestionConfidenceLevel.High => "Installer behavior matched deterministic local evidence and verified switch hints.",
            SuggestionConfidenceLevel.Medium => "Installer behavior was inferred. Verify switches and detection before production rollout.",
            _ => "Installer behavior remains uncertain. Manual production validation is required."
        };

        return new CommandSuggestion
        {
            InstallCommand = installCommand,
            UninstallCommand = uninstallCommand,
            ConfidenceLevel = confidenceLevel,
            ConfidenceScore = adjustedScore,
            ConfidenceReason = confidenceReason,
            FingerprintEngine = template.Framework.ToString(),
            ParameterProbeDetected = probe.HasEvidence,
            SuggestedRules = new IntuneWin32AppRules
            {
                InstallContext = IntuneInstallContext.System,
                RestartBehavior = IntuneRestartBehavior.DetermineBehaviorBasedOnReturnCodes,
                MaxRunTimeMinutes = 60,
                RequireSilentSwitchReview = true,
                SilentSwitchesVerified = false,
                AppliedTemplateName = template.Name,
                TemplateGuidance = string.Join(" ", guidanceParts) + $" Confidence: {confidenceLevel} ({adjustedScore}/100).",
                DetectionRule = detectionRule
            }
        };
    }

    private static CommandSuggestion CreateAppxMsixSuggestion(string setupFilePath, string setupFileName)
    {
        var packageIdentity = TryReadAppxIdentity(setupFilePath);
        var quotedSetup = $"\"{setupFileName}\"";

        var installCommand =
            $"powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Add-AppxPackage -Path {quotedSetup}\"";

        var uninstallCommand = packageIdentity is null || string.IsNullOrWhiteSpace(packageIdentity.Name)
            ? $"powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Get-AppxPackage -Name '{PlaceholderPackageName}' | Remove-AppxPackage\""
            : $"powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Get-AppxPackage -Name '{packageIdentity.Name}' | Remove-AppxPackage\"";

        var detectionRule = packageIdentity is null
            ? new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.None
            }
            : new IntuneDetectionRule
        {
            RuleType = IntuneDetectionRuleType.Script,
            Script = new ScriptDetectionRule
            {
                ScriptBody = BuildAppxDetectionScript(packageIdentity),
                RunAs32BitOn64System = false,
                EnforceSignatureCheck = false
            }
        };

        var guidance = packageIdentity is null
            ? "APPX/MSIX detected, but package identity metadata could not be extracted. Configure an exact detection rule manually."
            : "APPX/MSIX detected. Exact package identity metadata was extracted and deterministic script detection was prefilled.";

        return new CommandSuggestion
        {
            InstallCommand = installCommand,
            UninstallCommand = uninstallCommand,
            ConfidenceLevel = packageIdentity is null
                ? SuggestionConfidenceLevel.Medium
                : SuggestionConfidenceLevel.High,
            ConfidenceScore = packageIdentity is null ? 66 : 94,
            ConfidenceReason = packageIdentity is null
                ? "APPX/MSIX detected but exact package identity could not be extracted."
                : "APPX/MSIX detected with exact identity metadata (name/publisher/version).",
            FingerprintEngine = "APPX/MSIX",
            ParameterProbeDetected = false,
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
            ConfidenceLevel = SuggestionConfidenceLevel.Medium,
            ConfidenceScore = 65,
            ConfidenceReason = "Script installer type detected from extension.",
            FingerprintEngine = "Script",
            ParameterProbeDetected = false,
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
        var signerSubject = TryGetSignerSubject(setupFilePath).ToLowerInvariant();

        foreach (var template in ExeFrameworkTemplates)
        {
            if (template.DetectionMarkers.Any(marker =>
                    markersText.Contains(marker, StringComparison.Ordinal) ||
                    fileName.Contains(marker, StringComparison.Ordinal) ||
                    productName.Contains(marker, StringComparison.Ordinal) ||
                    fileDescription.Contains(marker, StringComparison.Ordinal) ||
                    signerSubject.Contains(marker, StringComparison.Ordinal)))
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
            Guidance = "Installer framework could not be determined. Provide vendor-specific silent install/uninstall switches.",
            BaseConfidenceScore = 32
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
            Guidance = preset.Guidance,
            BaseConfidenceScore = preset.RequiresVerification ? 64 : 80
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

    private static bool ContainsPlaceholderArguments(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Contains('<') && value.Contains('>');
    }

    private static SuggestionConfidenceLevel ToConfidenceLevel(int score)
    {
        if (score >= SuggestionConfidenceHighThreshold)
        {
            return SuggestionConfidenceLevel.High;
        }

        if (score >= SuggestionConfidenceMediumThreshold)
        {
            return SuggestionConfidenceLevel.Medium;
        }

        return SuggestionConfidenceLevel.Low;
    }

    private KnowledgeLookupContext? BuildKnowledgeLookupContext(string setupFilePath, InstallerType installerType)
    {
        if (string.IsNullOrWhiteSpace(setupFilePath) || !File.Exists(setupFilePath))
        {
            return null;
        }

        var sha = ComputeFileSha256Cached(setupFilePath);
        if (string.IsNullOrWhiteSpace(sha))
        {
            return null;
        }

        var versionInfo = TryGetVersionInfo(setupFilePath);
        var productVersion = NormalizeVersion(versionInfo?.ProductVersion ?? versionInfo?.FileVersion);
        if (string.IsNullOrWhiteSpace(productVersion))
        {
            productVersion = "unknown";
        }

        return new KnowledgeLookupContext
        {
            InstallerType = installerType,
            Sha256 = sha,
            ProductVersion = productVersion,
            ProductName = versionInfo?.ProductName ?? Path.GetFileNameWithoutExtension(setupFilePath) ?? string.Empty
        };
    }

    private bool TryGetKnowledgeEntry(KnowledgeLookupContext lookupContext, out InstallerKnowledgeEntry entry)
    {
        lock (_knowledgeLock)
        {
            EnsureKnowledgeLoaded();

            entry = _knowledgeStore.Entries.FirstOrDefault(candidate =>
                candidate.InstallerType == lookupContext.InstallerType &&
                candidate.InstallerSha256.Equals(lookupContext.Sha256, StringComparison.OrdinalIgnoreCase) &&
                candidate.ProductVersion.Equals(lookupContext.ProductVersion, StringComparison.OrdinalIgnoreCase))
                ?? _knowledgeStore.Entries.FirstOrDefault(candidate =>
                    candidate.InstallerType == lookupContext.InstallerType &&
                    candidate.InstallerSha256.Equals(lookupContext.Sha256, StringComparison.OrdinalIgnoreCase))
                ?? new InstallerKnowledgeEntry();

            if (string.IsNullOrWhiteSpace(entry.InstallerSha256))
            {
                return false;
            }

            entry.UseCount++;
            PersistKnowledgeStoreUnsafe();
            return true;
        }
    }

    private void EnsureKnowledgeLoaded()
    {
        if (_knowledgeLoaded)
        {
            return;
        }

        try
        {
            if (File.Exists(_knowledgeFilePath))
            {
                var json = File.ReadAllText(_knowledgeFilePath);
                var parsed = JsonSerializer.Deserialize<KnowledgeStore>(json, KnowledgeSerializerOptions);
                _knowledgeStore = parsed ?? new KnowledgeStore();
            }
            else
            {
                _knowledgeStore = new KnowledgeStore();
            }
        }
        catch
        {
            _knowledgeStore = new KnowledgeStore();
        }
        finally
        {
            _knowledgeLoaded = true;
        }
    }

    private void PersistKnowledgeStoreUnsafe()
    {
        try
        {
            var directory = Path.GetDirectoryName(_knowledgeFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _knowledgeFilePath + ".tmp";
            var json = JsonSerializer.Serialize(_knowledgeStore, KnowledgeSerializerOptions);
            File.WriteAllText(tempPath, json);
            File.Copy(tempPath, _knowledgeFilePath, overwrite: true);
            File.Delete(tempPath);
        }
        catch
        {
            // Best effort persistence.
        }
    }

    private string ComputeFileSha256Cached(string setupFilePath)
    {
        try
        {
            var info = new FileInfo(setupFilePath);
            if (!info.Exists)
            {
                return string.Empty;
            }

            var cacheKey = $"{setupFilePath.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            if (_shaCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            using var stream = File.OpenRead(setupFilePath);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            _shaCache[cacheKey] = hash;
            return hash;
        }
        catch
        {
            return string.Empty;
        }
    }

    private ParameterProbeResult ProbeInstallerParameters(string setupFilePath)
    {
        if (string.IsNullOrWhiteSpace(setupFilePath) || !File.Exists(setupFilePath))
        {
            return ParameterProbeResult.Empty;
        }

        var info = new FileInfo(setupFilePath);
        var cacheKey = $"{setupFilePath.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        if (_probeCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var probeSwitches = new[] { "/?", "-?", "/help", "--help" };
        var combinedOutput = new StringBuilder();
        var switchesWithOutput = new List<string>();
        var detectedSwitchTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var probeSwitch in probeSwitches)
        {
            var output = RunHelpProbe(setupFilePath, probeSwitch);
            if (string.IsNullOrWhiteSpace(output))
            {
                continue;
            }

            switchesWithOutput.Add(probeSwitch);
            combinedOutput.AppendLine(output);
            foreach (Match match in HelpSwitchTokenRegex.Matches(output))
            {
                var token = match.Value.Trim().TrimEnd('.', ',', ';', ':');
                if (!string.IsNullOrWhiteSpace(token))
                {
                    detectedSwitchTokens.Add(token.ToLowerInvariant());
                }
            }
        }

        if (switchesWithOutput.Count == 0)
        {
            _probeCache[cacheKey] = ParameterProbeResult.Empty;
            return ParameterProbeResult.Empty;
        }

        var outputText = combinedOutput.ToString().ToLowerInvariant();

        var hasVerySilent = outputText.Contains("/verysilent", StringComparison.Ordinal) || outputText.Contains("verysilent", StringComparison.Ordinal);
        var hasSilent = outputText.Contains("/silent", StringComparison.Ordinal) || outputText.Contains("--silent", StringComparison.Ordinal) || outputText.Contains(" /s ", StringComparison.Ordinal);
        var hasQuiet = outputText.Contains("/quiet", StringComparison.Ordinal) || outputText.Contains("--quiet", StringComparison.Ordinal);
        var hasQn = outputText.Contains("/qn", StringComparison.Ordinal);
        var hasNoRestart = outputText.Contains("/norestart", StringComparison.Ordinal) || outputText.Contains("norestart", StringComparison.Ordinal);
        var hasUninstall = outputText.Contains("/uninstall", StringComparison.Ordinal) || outputText.Contains("--uninstall", StringComparison.Ordinal);

        string installArguments = string.Empty;
        string uninstallArguments = string.Empty;
        var confidence = 35;

        if (hasVerySilent)
        {
            installArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";
            uninstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
            confidence = 88;
        }
        else if (hasQn)
        {
            installArguments = "/qn /norestart";
            uninstallArguments = "/x /qn /norestart";
            confidence = 82;
        }
        else if (hasQuiet)
        {
            installArguments = "/quiet /norestart";
            uninstallArguments = hasUninstall ? "/uninstall /quiet /norestart" : "/quiet /norestart";
            confidence = 78;
        }
        else if (hasSilent)
        {
            installArguments = hasNoRestart ? "/silent /norestart" : "/silent";
            uninstallArguments = hasUninstall
                ? (hasNoRestart ? "/uninstall /silent /norestart" : "/uninstall /silent")
                : "/silent";
            confidence = 70;
        }

        if (string.IsNullOrWhiteSpace(uninstallArguments) && hasUninstall)
        {
            uninstallArguments = hasQuiet
                ? "/uninstall /quiet /norestart"
                : "/uninstall";
            confidence = Math.Max(confidence, 66);
        }

        var result = new ParameterProbeResult
        {
            HasEvidence = true,
            InstallArguments = installArguments,
            UninstallArguments = uninstallArguments,
            ConfidenceScore = confidence,
            HelpSwitchesTried = switchesWithOutput,
            DetectedSwitches = detectedSwitchTokens.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(30).ToList()
        };

        _probeCache[cacheKey] = result;
        return result;
    }

    private static string RunHelpProbe(string setupFilePath, string helpSwitch)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = setupFilePath,
                Arguments = helpSwitch,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (!process.Start())
            {
                return string.Empty;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(ProbeTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore kill failures.
                }
            }

            Task.WaitAll([stdoutTask, stderrTask], ProbeTimeoutMs);
            var output = (stdoutTask.Result + Environment.NewLine + stderrTask.Result).Trim();
            if (output.Length > 16000)
            {
                output = output[..16000];
            }

            return output;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var normalized = version.Trim();
        var plusIndex = normalized.IndexOf('+');
        if (plusIndex > 0)
        {
            normalized = normalized[..plusIndex];
        }

        return normalized;
    }

    private static string TryGetSignerSubject(string setupFilePath)
    {
        try
        {
            var certificate = X509Certificate.CreateFromSignedFile(setupFilePath);
            if (certificate is null)
            {
                return string.Empty;
            }

            using var certificate2 = new X509Certificate2(certificate);
            return certificate2.Subject ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
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
        var expectedDisplayNames = new[]
        {
            Normalize(version?.ProductName),
            Normalize(version?.FileDescription)
        }
        .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 3)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        var expectedPublisher = Normalize(version?.CompanyName);
        var expectedVersion = NormalizeVersion(version?.ProductVersion);

        if (expectedDisplayNames.Count == 0 ||
            string.IsNullOrWhiteSpace(expectedPublisher) ||
            string.IsNullOrWhiteSpace(expectedVersion))
        {
            return null;
        }

        var matches = EnumerateUninstallEntries()
            .Where(entry => expectedDisplayNames.Contains(Normalize(entry.DisplayName), StringComparer.OrdinalIgnoreCase))
            .Where(entry => Normalize(entry.Publisher).Equals(expectedPublisher, StringComparison.OrdinalIgnoreCase))
            .Where(entry => NormalizeVersion(entry.DisplayVersion).Equals(expectedVersion, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            return null;
        }

        var match = matches
            .OrderBy(entry => entry.HiveName.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(entry => entry.Check32BitOn64System ? 1 : 0)
            .ThenBy(entry => entry.KeyPath, StringComparer.OrdinalIgnoreCase)
            .First();

        return new InstalledAppEvidence
        {
            DisplayName = match.DisplayName,
            DetectionRule = new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.Registry,
                Registry = new RegistryDetectionRule
                {
                    Hive = match.HiveName,
                    KeyPath = match.KeyPath,
                    ValueName = "DisplayVersion",
                    Check32BitOn64System = match.Check32BitOn64System,
                    Operator = IntuneDetectionOperator.Equals,
                    Value = match.DisplayVersion
                }
            },
            UninstallCommand = NormalizeUninstallCommand(match.UninstallString)
        };
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<RegistryUninstallEntry> EnumerateUninstallEntries()
    {
        var roots = new[]
        {
            (Hive: RegistryHive.LocalMachine, View: RegistryView.Registry64, Path: @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Hive: RegistryHive.LocalMachine, View: RegistryView.Registry32, Path: @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Hive: RegistryHive.CurrentUser, View: RegistryView.Default, Path: @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
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
                            DisplayVersion = appKey.GetValue("DisplayVersion")?.ToString() ?? string.Empty,
                            Check32BitOn64System = root.Hive == RegistryHive.LocalMachine && root.View == RegistryView.Registry32,
                            UninstallString = appKey.GetValue("QuietUninstallString")?.ToString()
                                ?? appKey.GetValue("UninstallString")?.ToString()
                                ?? string.Empty
                        };
                    }
                }
            }
        }
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

    private static AppxIdentity? TryReadAppxIdentity(string setupFilePath)
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
            var name = identity?.Attribute("Name")?.Value;
            var publisher = identity?.Attribute("Publisher")?.Value;
            var version = identity?.Attribute("Version")?.Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            return new AppxIdentity
            {
                Name = name,
                Publisher = publisher ?? string.Empty,
                Version = version
            };
        }
        catch
        {
            return null;
        }
    }

    private static string BuildAppxDetectionScript(AppxIdentity identity)
    {
        var escapedName = identity.Name.Replace("\"", "`\"", StringComparison.Ordinal);
        var escapedVersion = identity.Version.Replace("\"", "`\"", StringComparison.Ordinal);
        var escapedPublisher = identity.Publisher.Replace("\"", "`\"", StringComparison.Ordinal);

        var publisherFilter = string.IsNullOrWhiteSpace(escapedPublisher)
            ? string.Empty
            : $" -and $_.Publisher -eq \"{escapedPublisher}\"";

        return string.Join(Environment.NewLine,
        [
            "$package = Get-AppxPackage -Name \"" + escapedName + "\" -ErrorAction SilentlyContinue | Where-Object {",
            "    $_.Version.ToString() -eq \"" + escapedVersion + "\"" + publisherFilter,
            "} | Select-Object -First 1",
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

    private sealed record AppxIdentity
    {
        public string Name { get; init; } = string.Empty;

        public string Publisher { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;
    }

    private sealed record ExeFrameworkTemplate
    {
        public ExeInstallerFramework Framework { get; init; } = ExeInstallerFramework.Unknown;

        public string Name { get; init; } = string.Empty;

        public string InstallArguments { get; init; } = string.Empty;

        public string UninstallArguments { get; init; } = string.Empty;

        public IReadOnlyList<string> DetectionMarkers { get; init; } = [];

        public string Guidance { get; init; } = string.Empty;

        public int BaseConfidenceScore { get; init; } = 40;
    }

    private sealed class KnowledgeStore
    {
        public List<InstallerKnowledgeEntry> Entries { get; set; } = [];
    }

    private sealed class InstallerKnowledgeEntry
    {
        public string InstallerSha256 { get; set; } = string.Empty;

        public string ProductVersion { get; set; } = "unknown";

        public string ProductName { get; set; } = string.Empty;

        public string SetupFileName { get; set; } = string.Empty;

        public InstallerType InstallerType { get; set; } = InstallerType.Unknown;

        public string InstallCommand { get; set; } = string.Empty;

        public string UninstallCommand { get; set; } = string.Empty;

        public IntuneWin32AppRules IntuneRules { get; set; } = new();

        public DateTimeOffset LastVerifiedAtUtc { get; set; }

        public int UseCount { get; set; }

        public string TemplateGuidance { get; set; } = string.Empty;

        public string FingerprintEngine { get; set; } = string.Empty;
    }

    private sealed record KnowledgeLookupContext
    {
        public InstallerType InstallerType { get; init; } = InstallerType.Unknown;

        public string Sha256 { get; init; } = string.Empty;

        public string ProductVersion { get; init; } = "unknown";

        public string ProductName { get; init; } = string.Empty;
    }

    private sealed record ParameterProbeResult
    {
        public static ParameterProbeResult Empty { get; } = new();

        public bool HasEvidence { get; init; }

        public string InstallArguments { get; init; } = string.Empty;

        public string UninstallArguments { get; init; } = string.Empty;

        public int ConfidenceScore { get; init; }

        public IReadOnlyList<string> HelpSwitchesTried { get; init; } = [];

        public IReadOnlyList<string> DetectedSwitches { get; init; } = [];
    }

    private sealed record RegistryUninstallEntry
    {
        public string HiveName { get; init; } = string.Empty;

        public string KeyPath { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string Publisher { get; init; } = string.Empty;

        public string DisplayVersion { get; init; } = string.Empty;

        public bool Check32BitOn64System { get; init; }

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
        Squirrel = 5,
        AdvancedInstaller = 6,
        Manual = 7
    }
}
