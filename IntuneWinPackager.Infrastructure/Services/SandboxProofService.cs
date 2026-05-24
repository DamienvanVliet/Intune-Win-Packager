using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Infrastructure.Support;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class SandboxProofService : ISandboxProofService
{
    private const string SandboxProofRoot = @"C:\IwpSandboxProof";
    private const string SandboxSourceRoot = @"C:\IwpSandboxSource";
    private static readonly Regex UnsafeFileNameCharacters = new(@"[^a-zA-Z0-9._-]+", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<SandboxProofSession> StartAsync(
        SandboxProofRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return Failed("Sandbox proof requires a request.");
        }

        if (string.IsNullOrWhiteSpace(request.SetupFilePath) || !File.Exists(request.SetupFilePath))
        {
            return Failed("Sandbox proof requires an existing setup file.");
        }

        DataPathProvider.EnsureBaseDirectory();
        Directory.CreateDirectory(DataPathProvider.SandboxProofRunsDirectory);

        var setupPath = Path.GetFullPath(request.SetupFilePath);
        var sourceFolder = ResolveSourceFolder(request.SourceFolder, setupPath);
        var runDirectory = BuildRunDirectory(setupPath);
        var inputDirectory = Path.Combine(runDirectory, "input");
        var logsDirectory = Path.Combine(runDirectory, "logs");

        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(logsDirectory);

        var setupSandboxPath = TryMapSetupIntoSource(sourceFolder, setupPath, out var relativeSetupPath)
            ? CombineSandboxPath(SandboxSourceRoot, relativeSetupPath)
            : CopySetupIntoRunInput(setupPath, inputDirectory);

        var sandboxWorkingDirectory = Path.GetDirectoryName(setupSandboxPath) ?? SandboxSourceRoot;
        var sandboxInstallCommand = RewriteCommandForSandbox(
            string.IsNullOrWhiteSpace(request.InstallCommand) ? BuildDefaultInstallCommand(request.InstallerType, setupSandboxPath) : request.InstallCommand,
            setupPath,
            setupSandboxPath,
            sourceFolder,
            SandboxSourceRoot);
        var sandboxUninstallCommand = RewriteCommandForSandbox(
            request.UninstallCommand,
            setupPath,
            setupSandboxPath,
            sourceFolder,
            SandboxSourceRoot);

        var proofInput = new SandboxProofInput
        {
            InstallerType = request.InstallerType,
            HostSetupFilePath = setupPath,
            HostSourceFolder = sourceFolder,
            SandboxSetupFilePath = setupSandboxPath,
            SandboxWorkingDirectory = sandboxWorkingDirectory,
            InstallCommand = sandboxInstallCommand,
            UninstallCommand = sandboxUninstallCommand,
            DetectionRule = request.DetectionRule,
            PrecheckSummary = request.PrecheckSummary,
            PrecheckDetectionRuleAvailable = request.PrecheckDetectionRuleAvailable,
            PrecheckAdditionalDetectionRuleCount = request.PrecheckAdditionalDetectionRuleCount,
            TimeoutMinutes = Math.Clamp(request.TimeoutMinutes, 5, 240),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var inputPath = Path.Combine(runDirectory, "proof-input.json");
        var scriptPath = Path.Combine(runDirectory, "run-proof.ps1");
        var wsbPath = Path.Combine(runDirectory, "SandboxProof.wsb");
        var reportPath = Path.Combine(runDirectory, "report.txt");
        var resultPath = Path.Combine(runDirectory, "result.json");

        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(proofInput, JsonOptions), Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(scriptPath, BuildRunnerScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
        await File.WriteAllTextAsync(wsbPath, BuildWsbConfiguration(runDirectory, sourceFolder), Encoding.UTF8, cancellationToken);

        if (!request.LaunchSandbox)
        {
            return new SandboxProofSession
            {
                Success = true,
                Launched = false,
                Message = "Sandbox proof workspace created.",
                RunDirectory = runDirectory,
                WsbPath = wsbPath,
                InputPath = inputPath,
                RunnerScriptPath = scriptPath,
                ReportPath = reportPath,
                ResultPath = resultPath
            };
        }

        if (!OperatingSystem.IsWindows())
        {
            return SessionFailure("Windows Sandbox proof is only supported on Windows.", runDirectory, wsbPath, inputPath, scriptPath, reportPath, resultPath);
        }

        if (!IsWindowsSandboxAvailable())
        {
            return SessionFailure(
                "Windows Sandbox does not appear to be enabled. Enable the Windows Sandbox optional feature and try again.",
                runDirectory,
                wsbPath,
                inputPath,
                scriptPath,
                reportPath,
                resultPath);
        }

        var activeSandboxProcesses = GetActiveWindowsSandboxProcesses();
        if (activeSandboxProcesses.Count > 0)
        {
            return SessionFailure(
                $"Windows Sandbox is already running ({string.Join(", ", activeSandboxProcesses)}). Close existing Sandbox windows and wait until WindowsSandbox/vmmemWindowsSandbox stops before starting a new Sandbox Proof run.",
                runDirectory,
                wsbPath,
                inputPath,
                scriptPath,
                reportPath,
                resultPath);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = wsbPath,
                UseShellExecute = true,
                WorkingDirectory = runDirectory
            });

            return new SandboxProofSession
            {
                Success = true,
                Launched = true,
                Message = "Windows Sandbox proof launched. Wait for the sandbox run to finish, then open the report from the run folder.",
                RunDirectory = runDirectory,
                WsbPath = wsbPath,
                InputPath = inputPath,
                RunnerScriptPath = scriptPath,
                ReportPath = reportPath,
                ResultPath = resultPath
            };
        }
        catch (Exception ex)
        {
            return SessionFailure($"Windows Sandbox could not be launched: {ex.Message}", runDirectory, wsbPath, inputPath, scriptPath, reportPath, resultPath);
        }
    }

    private static IReadOnlyList<string> GetActiveWindowsSandboxProcesses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WindowsSandbox",
            "WindowsSandboxClient",
            "WindowsSandboxRemoteSession",
            "WindowsSandboxServer",
            "vmmemWindowsSandbox"
        };

        try
        {
            return Process.GetProcesses()
                .Where(process => processNames.Contains(process.ProcessName))
                .Select(process => $"{process.ProcessName} ({process.Id})")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task<SandboxProofDetectionResult> ReadResultAsync(
        string resultPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return new SandboxProofDetectionResult
            {
                Completed = false,
                Message = "Sandbox proof result path is empty."
            };
        }

        if (!File.Exists(resultPath))
        {
            var runnerFailure = TryBuildMissingResultFailure(resultPath);
            if (runnerFailure is not null)
            {
                return runnerFailure;
            }

            return new SandboxProofDetectionResult
            {
                Completed = false,
                ResultPath = resultPath,
                Message = "Sandbox proof result is not ready yet."
            };
        }

        try
        {
            await using var stream = File.OpenRead(resultPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            var hasInstallOutcome = TryGetObject(root, "install", out var installElement);
            var installTimedOut = hasInstallOutcome && GetJsonBoolean(installElement, "timedOut");
            var installExitCode = hasInstallOutcome ? GetJsonInt(installElement, "exitCode") : 0;
            var installCommand = hasInstallOutcome ? GetJsonString(installElement, "command") : string.Empty;
            var hasUninstallOutcome = TryGetObject(root, "uninstall", out var uninstallElement);
            var uninstallTimedOut = hasUninstallOutcome && GetJsonBoolean(uninstallElement, "timedOut");
            var uninstallExitCode = hasUninstallOutcome ? GetJsonInt(uninstallElement, "exitCode") : 0;
            var uninstallCommand = hasUninstallOutcome ? GetJsonString(uninstallElement, "command") : string.Empty;
            var hasUninstallValidation = TryGetObject(root, "uninstallValidation", out var uninstallValidationElement);
            var uninstallCommandSource = TryGetObject(root, "uninstallResolution", out var uninstallResolutionElement)
                ? GetJsonString(uninstallResolutionElement, "source")
                : string.Empty;
            var failureKind = GetJsonString(root, "failureKind");
            var installProven = hasInstallOutcome && IsSuccessfulInstallExitCode(installExitCode, installTimedOut);
            var uninstallProven = hasUninstallValidation
                ? GetJsonBoolean(uninstallValidationElement, "success")
                : !hasUninstallOutcome || IsSuccessfulInstallExitCode(uninstallExitCode, uninstallTimedOut);
            var launchValidationProven = !TryGetObject(root, "launchValidation", out var launchValidationElement) ||
                                         GetJsonBoolean(launchValidationElement, "success");
            var candidates = new List<SandboxProofDetectionCandidate>();
            if (root.TryGetProperty("candidates", out var candidatesElement) &&
                candidatesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var candidateElement in candidatesElement.EnumerateArray())
                {
                    var candidate = ParseDetectionCandidate(candidateElement);
                    if (candidate is not null && candidate.Rule.RuleType != IntuneDetectionRuleType.None)
                    {
                        candidates.Add(candidate);
                    }
                }
            }

            var provenCandidateCount = candidates.Count(candidate => candidate.IsProven);
            var proofAvailable = candidates.Any(candidate => candidate.ProofAvailable);
            var selectableCandidates = proofAvailable
                ? candidates.Where(candidate => candidate.IsProven)
                : candidates;
            var bestCandidate = selectableCandidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => ConfidenceScore(candidate.Confidence))
                .ThenBy(candidate => DetectionTypePriority(candidate.Rule.RuleType))
                .FirstOrDefault();

            if (GetJsonBoolean(root, "failed") &&
                !string.IsNullOrWhiteSpace(failureKind) &&
                !failureKind.Equals("LaunchValidation", StringComparison.OrdinalIgnoreCase))
            {
                var error = GetJsonString(root, "error");
                return new SandboxProofDetectionResult
                {
                    Completed = true,
                    Failed = true,
                    ResultPath = resultPath,
                    FailureKind = failureKind,
                    InstallCommand = installCommand,
                    UninstallCommand = uninstallCommand,
                    UninstallCommandSource = uninstallCommandSource,
                    InstallProven = installProven,
                    DetectionProven = provenCandidateCount > 0,
                    UninstallProven = uninstallProven,
                    LaunchValidationProven = launchValidationProven,
                    Message = string.IsNullOrWhiteSpace(error)
                        ? BuildBlockingFailureMessage(failureKind)
                        : $"Sandbox proof failed: {error}",
                    CandidateCount = candidates.Count,
                    ProvenCandidateCount = provenCandidateCount,
                    BestCandidate = bestCandidate,
                    Candidates = candidates
                };
            }

            if (GetJsonBoolean(root, "failed") && bestCandidate is null)
            {
                var error = GetJsonString(root, "error");
                return new SandboxProofDetectionResult
                {
                    Completed = true,
                    Failed = true,
                    ResultPath = resultPath,
                    FailureKind = failureKind,
                    InstallCommand = installCommand,
                    UninstallCommand = uninstallCommand,
                    UninstallCommandSource = uninstallCommandSource,
                    InstallProven = installProven,
                    DetectionProven = provenCandidateCount > 0,
                    UninstallProven = uninstallProven,
                    LaunchValidationProven = launchValidationProven,
                    Message = string.IsNullOrWhiteSpace(error)
                        ? "Sandbox proof failed."
                        : $"Sandbox proof failed: {error}",
                    CandidateCount = candidates.Count,
                    ProvenCandidateCount = provenCandidateCount,
                    Candidates = candidates
                };
            }

            if (hasInstallOutcome &&
                !IsSuccessfulInstallExitCode(installExitCode, installTimedOut))
            {
                return new SandboxProofDetectionResult
                {
                    Completed = true,
                    Failed = true,
                    ResultPath = resultPath,
                    FailureKind = "Install",
                    InstallCommand = installCommand,
                    UninstallCommand = uninstallCommand,
                    UninstallCommandSource = uninstallCommandSource,
                    InstallProven = false,
                    DetectionProven = provenCandidateCount > 0,
                    UninstallProven = uninstallProven,
                    LaunchValidationProven = launchValidationProven,
                    Message = BuildInstallFailureMessage(installExitCode, installTimedOut, candidates.Count),
                    CandidateCount = candidates.Count,
                    ProvenCandidateCount = provenCandidateCount,
                    BestCandidate = bestCandidate,
                    Candidates = candidates
                };
            }

            var message = BuildSandboxProofResultMessage(candidates.Count, provenCandidateCount, proofAvailable, bestCandidate);
            if (GetJsonBoolean(root, "failed") ||
                string.Equals(failureKind, "LaunchValidation", StringComparison.OrdinalIgnoreCase))
            {
                var error = GetJsonString(root, "error");
                message = string.IsNullOrWhiteSpace(error)
                    ? $"{message} Sandbox launch validation warning: launch check did not prove an interactive app window, but install/detection/uninstall proof is still valid."
                    : $"{message} Sandbox launch validation warning: {error}. Install/detection/uninstall proof is still valid.";
            }

            return new SandboxProofDetectionResult
            {
                Completed = true,
                ResultPath = resultPath,
                FailureKind = failureKind,
                InstallCommand = installCommand,
                UninstallCommand = uninstallCommand,
                UninstallCommandSource = uninstallCommandSource,
                InstallProven = installProven,
                DetectionProven = provenCandidateCount > 0,
                UninstallProven = uninstallProven,
                LaunchValidationProven = launchValidationProven,
                Message = message,
                CandidateCount = candidates.Count,
                ProvenCandidateCount = provenCandidateCount,
                BestCandidate = bestCandidate,
                Candidates = candidates
            };
        }
        catch (JsonException)
        {
            return new SandboxProofDetectionResult
            {
                Completed = false,
                ResultPath = resultPath,
                Message = "Sandbox proof result is still being written."
            };
        }
        catch (IOException)
        {
            return new SandboxProofDetectionResult
            {
                Completed = false,
                ResultPath = resultPath,
                Message = "Sandbox proof result is still being written."
            };
        }
        catch (Exception ex)
        {
            return new SandboxProofDetectionResult
            {
                Completed = true,
                Failed = true,
                ResultPath = resultPath,
                Message = $"Sandbox proof result could not be read: {ex.Message}"
            };
        }
    }

    private static SandboxProofSession Failed(string message)
    {
        return new SandboxProofSession
        {
            Success = false,
            Message = message
        };
    }

    private static SandboxProofDetectionResult? TryBuildMissingResultFailure(string resultPath)
    {
        var runDirectory = Path.GetDirectoryName(resultPath);
        if (string.IsNullOrWhiteSpace(runDirectory) || !Directory.Exists(runDirectory))
        {
            return null;
        }

        var proofLogPath = Path.Combine(runDirectory, "logs", "proof.log");
        var inputPath = Path.Combine(runDirectory, "proof-input.json");
        var referencePath = File.Exists(inputPath) ? inputPath : runDirectory;
        DateTimeOffset lastWriteUtc;
        try
        {
            lastWriteUtc = File.Exists(referencePath)
                ? File.GetLastWriteTimeUtc(referencePath)
                : Directory.GetLastWriteTimeUtc(referencePath);
        }
        catch
        {
            return null;
        }

        if (!File.Exists(proofLogPath))
        {
            if (DateTimeOffset.UtcNow - lastWriteUtc < TimeSpan.FromMinutes(4))
            {
                return null;
            }

            return new SandboxProofDetectionResult
            {
                Completed = true,
                Failed = true,
                ResultPath = resultPath,
                FailureKind = "RunnerStart",
                Message =
                    "Sandbox proof failed before the runner started. Windows Sandbox launched, but run-proof.ps1 did not write logs within 4 minutes. " +
                    "Close any existing Windows Sandbox windows, wait for vmmemWindowsSandbox to stop, then run Sandbox Proof again."
            };
        }

        var timeoutMinutes = TryReadSandboxProofTimeoutMinutes(inputPath);
        var staleAfter = TimeSpan.FromMinutes(Math.Clamp(timeoutMinutes + 3, 8, 245));
        var proofLogLastWriteUtc = File.GetLastWriteTimeUtc(proofLogPath);
        if (DateTimeOffset.UtcNow - proofLogLastWriteUtc < staleAfter)
        {
            return null;
        }

        return new SandboxProofDetectionResult
        {
            Completed = true,
            Failed = true,
            ResultPath = resultPath,
            FailureKind = "RunnerIncomplete",
            Message =
                "Sandbox proof started but did not write result.json after the expected timeout window. " +
                "The install command may have hung, Windows Sandbox may have been closed, or the runner stopped before writing final evidence. " +
                "Open the run folder and review logs/proof.log and the install stdout/stderr files."
        };
    }

    private static int TryReadSandboxProofTimeoutMinutes(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            return 20;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(inputPath));
            return Math.Clamp(GetJsonInt(document.RootElement, "timeoutMinutes"), 5, 240);
        }
        catch
        {
            return 20;
        }
    }

    private static bool IsSuccessfulInstallExitCode(int exitCode, bool timedOut)
    {
        return !timedOut && exitCode is 0 or 3010 or 1641;
    }

    private static string BuildBlockingFailureMessage(string failureKind)
    {
        return failureKind switch
        {
            "Install" => "Sandbox proof failed: install command did not complete successfully.",
            "Uninstall" => "Sandbox proof failed: uninstall command did not complete successfully or did not remove the detection evidence.",
            "Detection" => "Sandbox proof failed: no detection rule passed install and uninstall validation.",
            _ => "Sandbox proof failed."
        };
    }

    private static string BuildInstallFailureMessage(int exitCode, bool timedOut, int candidateCount)
    {
        if (timedOut)
        {
            return "Sandbox proof failed: install command timed out. The installer did not finish unattended, so this package would likely hang in Intune too.";
        }

        var explanation = exitCode switch
        {
            1602 => "cancelled by the installer or blocked by a required UI prompt, often caused by incorrect silent switches",
            1618 => "another installation is already in progress",
            1623 or 1633 or 1654 => "installer does not support this system",
            1625 or 1640 or 1643 or 1644 or 1649 => "blocked by policy",
            1628 or 1639 or 1650 => "invalid command-line parameters",
            1638 => "another version is already installed",
            _ => "installer returned a failure code"
        };
        var evidence = candidateCount == 0
            ? "No install evidence or detection candidates were created."
            : $"{candidateCount} detection candidate(s) were created, but none passed proof validation.";

        return $"Sandbox proof failed: install command exited with code {exitCode} ({explanation}). {evidence} Review the install arguments before packaging.";
    }

    private static SandboxProofDetectionCandidate? ParseDetectionCandidate(JsonElement candidateElement)
    {
        if (!candidateElement.TryGetProperty("rule", out var ruleElement) ||
            ruleElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rule = ParseDetectionRule(ruleElement);
        if (rule.RuleType == IntuneDetectionRuleType.None)
        {
            return null;
        }

        var proofAvailable = TryGetObject(candidateElement, "proof", out var proofElement);
        var negativeProofSummary = proofAvailable && TryGetObject(proofElement, "negativePhase", out var negativePhase)
            ? GetJsonString(negativePhase, "summary")
            : string.Empty;
        var positiveProofSummary = proofAvailable && TryGetObject(proofElement, "positivePhase", out var positivePhase)
            ? GetJsonString(positivePhase, "summary")
            : string.Empty;
        var uninstallProofSummary = proofAvailable && TryGetObject(proofElement, "uninstallPhase", out var uninstallPhase)
            ? GetJsonString(uninstallPhase, "summary")
            : string.Empty;

        var additionalRules = new List<IntuneDetectionRule>();
        if (candidateElement.TryGetProperty("additionalRules", out var additionalRulesElement) &&
            additionalRulesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var additionalRuleElement in additionalRulesElement.EnumerateArray())
            {
                if (additionalRuleElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var additionalRule = ParseDetectionRule(additionalRuleElement);
                if (additionalRule.RuleType != IntuneDetectionRuleType.None)
                {
                    additionalRules.Add(additionalRule);
                }
            }
        }

        return new SandboxProofDetectionCandidate
        {
            Type = GetJsonString(candidateElement, "type"),
            Confidence = GetJsonString(candidateElement, "confidence"),
            Score = GetJsonInt(candidateElement, "score"),
            Reason = GetJsonString(candidateElement, "reason"),
            ProofAvailable = proofAvailable,
            IsProven = proofAvailable && GetJsonBoolean(proofElement, "success"),
            ProofSummary = proofAvailable ? GetJsonString(proofElement, "summary") : string.Empty,
            NegativeProofSummary = negativeProofSummary,
            PositiveProofSummary = positiveProofSummary,
            UninstallProofSummary = uninstallProofSummary,
            Rule = rule,
            AdditionalRules = additionalRules
        };
    }

    private static string BuildSandboxProofResultMessage(
        int candidateCount,
        int provenCandidateCount,
        bool proofAvailable,
        SandboxProofDetectionCandidate? bestCandidate)
    {
        if (candidateCount == 0)
        {
            return "Sandbox proof finished, but no detection candidates were found.";
        }

        if (proofAvailable && bestCandidate is null)
        {
            return $"Sandbox proof found {candidateCount} detection candidate(s), but none passed sandbox validation.";
        }

        if (bestCandidate is null)
        {
            return "Sandbox proof finished, but no importable detection candidate was found.";
        }

        return proofAvailable
            ? $"Sandbox proof found {candidateCount} detection candidate(s), {provenCandidateCount} proven. Best: {bestCandidate.Rule.RuleType} ({bestCandidate.Confidence})."
            : $"Sandbox proof found {candidateCount} detection candidate(s). Best: {bestCandidate.Rule.RuleType} ({bestCandidate.Confidence}).";
    }

    private static IntuneDetectionRule ParseDetectionRule(JsonElement ruleElement)
    {
        var ruleType = ParseEnum(GetJsonString(ruleElement, "ruleType"), IntuneDetectionRuleType.None);
        return ruleType switch
        {
            IntuneDetectionRuleType.MsiProductCode => new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.MsiProductCode,
                Msi = ParseMsiDetectionRule(SelectNestedRuleElement(ruleElement, "msi"))
            },
            IntuneDetectionRuleType.File => new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.File,
                File = ParseFileDetectionRule(SelectNestedRuleElement(ruleElement, "file"))
            },
            IntuneDetectionRuleType.Registry => new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.Registry,
                Registry = ParseRegistryDetectionRule(SelectNestedRuleElement(ruleElement, "registry"))
            },
            IntuneDetectionRuleType.Script => new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.Script,
                Script = ParseScriptDetectionRule(SelectNestedRuleElement(ruleElement, "script"))
            },
            _ => new IntuneDetectionRule()
        };
    }

    private static JsonElement SelectNestedRuleElement(JsonElement ruleElement, string propertyName)
    {
        return ruleElement.TryGetProperty(propertyName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : ruleElement;
    }

    private static MsiDetectionRule ParseMsiDetectionRule(JsonElement element)
    {
        return new MsiDetectionRule
        {
            ProductCode = GetJsonString(element, "productCode"),
            ProductVersion = GetJsonString(element, "productVersion"),
            ProductVersionOperator = ParseEnum(GetJsonString(element, "productVersionOperator"), IntuneDetectionOperator.Equals)
        };
    }

    private static FileDetectionRule ParseFileDetectionRule(JsonElement element)
    {
        return new FileDetectionRule
        {
            Path = GetJsonString(element, "path"),
            FileOrFolderName = GetJsonString(element, "fileOrFolderName"),
            Check32BitOn64System = GetJsonBoolean(element, "check32BitOn64System"),
            Operator = ParseEnum(GetJsonString(element, "operator"), IntuneDetectionOperator.Exists),
            Value = GetJsonString(element, "value")
        };
    }

    private static RegistryDetectionRule ParseRegistryDetectionRule(JsonElement element)
    {
        return new RegistryDetectionRule
        {
            Hive = string.IsNullOrWhiteSpace(GetJsonString(element, "hive"))
                ? "HKEY_LOCAL_MACHINE"
                : GetJsonString(element, "hive"),
            KeyPath = GetJsonString(element, "keyPath"),
            ValueName = GetJsonString(element, "valueName"),
            Check32BitOn64System = GetJsonBoolean(element, "check32BitOn64System"),
            Operator = ParseEnum(GetJsonString(element, "operator"), IntuneDetectionOperator.Exists),
            Value = GetJsonString(element, "value")
        };
    }

    private static ScriptDetectionRule ParseScriptDetectionRule(JsonElement element)
    {
        return new ScriptDetectionRule
        {
            ScriptBody = GetJsonString(element, "scriptBody"),
            RunAs32BitOn64System = GetJsonBoolean(element, "runAs32BitOn64System"),
            EnforceSignatureCheck = GetJsonBoolean(element, "enforceSignatureCheck")
        };
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => string.Empty
        };
    }

    private static bool GetJsonBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static int GetJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var numericValue))
        {
            return numericValue;
        }

        if (property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), out var stringValue))
        {
            return stringValue;
        }

        return 0;
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement property)
    {
        return element.TryGetProperty(propertyName, out property) && property.ValueKind == JsonValueKind.Object;
    }

    private static int ConfidenceScore(string confidence)
    {
        return confidence switch
        {
            _ when string.Equals(confidence, "High", StringComparison.OrdinalIgnoreCase) => 3,
            _ when string.Equals(confidence, "Medium", StringComparison.OrdinalIgnoreCase) => 2,
            _ when string.Equals(confidence, "Low", StringComparison.OrdinalIgnoreCase) => 1,
            _ => 0
        };
    }

    private static int DetectionTypePriority(IntuneDetectionRuleType ruleType)
    {
        return ruleType switch
        {
            IntuneDetectionRuleType.Registry => 0,
            IntuneDetectionRuleType.MsiProductCode => 1,
            IntuneDetectionRuleType.File => 2,
            IntuneDetectionRuleType.Script => 3,
            _ => 4
        };
    }

    private static SandboxProofSession SessionFailure(
        string message,
        string runDirectory,
        string wsbPath,
        string inputPath,
        string scriptPath,
        string reportPath,
        string resultPath)
    {
        return new SandboxProofSession
        {
            Success = false,
            Launched = false,
            Message = message,
            RunDirectory = runDirectory,
            WsbPath = wsbPath,
            InputPath = inputPath,
            RunnerScriptPath = scriptPath,
            ReportPath = reportPath,
            ResultPath = resultPath
        };
    }

    private static string ResolveSourceFolder(string sourceFolder, string setupPath)
    {
        if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder))
        {
            return Path.GetFullPath(sourceFolder);
        }

        return Path.GetDirectoryName(setupPath) ?? Environment.CurrentDirectory;
    }

    private static string BuildRunDirectory(string setupPath)
    {
        var packageName = Path.GetFileNameWithoutExtension(setupPath);
        if (string.IsNullOrWhiteSpace(packageName))
        {
            packageName = "package";
        }

        packageName = UnsafeFileNameCharacters.Replace(packageName, "-").Trim('-', '.');
        if (string.IsNullOrWhiteSpace(packageName))
        {
            packageName = "package";
        }

        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(DataPathProvider.SandboxProofRunsDirectory, $"{timestamp}-{packageName}");
    }

    private static bool TryMapSetupIntoSource(string sourceFolder, string setupPath, out string relativeSetupPath)
    {
        relativeSetupPath = string.Empty;

        try
        {
            var relative = Path.GetRelativePath(sourceFolder, setupPath);
            if (string.IsNullOrWhiteSpace(relative) ||
                relative.StartsWith("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                return false;
            }

            relativeSetupPath = relative;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CopySetupIntoRunInput(string setupPath, string inputDirectory)
    {
        var target = Path.Combine(inputDirectory, Path.GetFileName(setupPath));
        File.Copy(setupPath, target, overwrite: true);
        return CombineSandboxPath(SandboxProofRoot, "input", Path.GetFileName(setupPath));
    }

    private static string CombineSandboxPath(params string[] parts)
    {
        return string.Join("\\", parts.Select(part => part.Trim('\\')));
    }

    private static string BuildDefaultInstallCommand(InstallerType installerType, string setupSandboxPath)
    {
        return installerType == InstallerType.Msi
            ? $"msiexec /i {QuoteCommandValue(setupSandboxPath)} /qn /norestart"
            : QuoteCommandValue(setupSandboxPath);
    }

    private static string RewriteCommandForSandbox(
        string command,
        string setupHostPath,
        string setupSandboxPath,
        string sourceHostFolder,
        string sourceSandboxFolder)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var rewritten = command;
        rewritten = ReplacePath(rewritten, setupHostPath, setupSandboxPath);
        rewritten = ReplacePath(rewritten, sourceHostFolder, sourceSandboxFolder);
        return rewritten;
    }

    private static string ReplacePath(string input, string hostPath, string sandboxPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return input;
        }

        var normalizedHost = Path.GetFullPath(hostPath).TrimEnd('\\');
        return input
            .Replace($"\"{normalizedHost}\"", QuoteCommandValue(sandboxPath), StringComparison.OrdinalIgnoreCase)
            .Replace(normalizedHost, sandboxPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteCommandValue(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static bool IsWindowsSandboxAvailable()
    {
        var sandboxExe = Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe");
        return File.Exists(sandboxExe);
    }

    private static string BuildWsbConfiguration(string runDirectory, string sourceFolder)
    {
        var command = @"powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ""C:\IwpSandboxProof\run-proof.ps1""";

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            Encoding = Encoding.UTF8
        };

        var builder = new StringBuilder();
        using var writer = XmlWriter.Create(builder, settings);
        writer.WriteStartElement("Configuration");
        writer.WriteElementString("Networking", "Enable");
        writer.WriteStartElement("MappedFolders");
        WriteMappedFolder(writer, runDirectory, SandboxProofRoot, readOnly: false);
        WriteMappedFolder(writer, sourceFolder, SandboxSourceRoot, readOnly: true);
        writer.WriteEndElement();
        writer.WriteStartElement("LogonCommand");
        writer.WriteElementString("Command", command);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.Flush();

        return builder.ToString();
    }

    private static void WriteMappedFolder(XmlWriter writer, string hostFolder, string sandboxFolder, bool readOnly)
    {
        writer.WriteStartElement("MappedFolder");
        writer.WriteElementString("HostFolder", hostFolder);
        writer.WriteElementString("SandboxFolder", sandboxFolder);
        writer.WriteElementString("ReadOnly", readOnly ? "true" : "false");
        writer.WriteEndElement();
    }

    private static string BuildRunnerScript()
    {
        return """
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$ProofRoot = 'C:\IwpSandboxProof'
$InputPath = Join-Path $ProofRoot 'proof-input.json'
$ReportPath = Join-Path $ProofRoot 'report.txt'
$ResultPath = Join-Path $ProofRoot 'result.json'
$LogsPath = Join-Path $ProofRoot 'logs'
$TranscriptPath = Join-Path $LogsPath 'transcript.txt'
$DetectionScriptPath = Join-Path $ProofRoot 'detection-script.ps1'
$CompletedMarkerPath = Join-Path $ProofRoot 'completed.marker'

New-Item -ItemType Directory -Path $LogsPath -Force | Out-Null

function Write-ProofLog {
    param([string]$Message)
    $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -LiteralPath (Join-Path $LogsPath 'proof.log') -Value $line
}

function ConvertTo-PlainObject {
    param($Value)
    if ($null -eq $Value) { return $null }
    return $Value
}

function Resolve-MsiProductCodeFromKeyName {
    param([string]$KeyName)
    if ([string]::IsNullOrWhiteSpace($KeyName)) { return '' }

    $guid = [guid]::Empty
    $trimmed = $KeyName.Trim().Trim('{', '}')
    if ([guid]::TryParse($trimmed, [ref]$guid)) {
        return '{{{0}}}' -f $guid.ToString('D').ToUpperInvariant()
    }

    return ''
}

function Get-UninstallSnapshot {
    $roots = @(
        @{ Hive = 'HKLM'; Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'; KeyPrefix = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' },
        @{ Hive = 'HKLM32'; Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'; KeyPrefix = 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' },
        @{ Hive = 'HKCU'; Path = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'; KeyPrefix = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' }
    )

    $items = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root.Path)) { continue }
        foreach ($key in Get-ChildItem -LiteralPath $root.Path -ErrorAction SilentlyContinue) {
            try {
                $props = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction Stop
                $keyPath = '{0}\{1}' -f $root.KeyPrefix, $key.PSChildName
                $items += [pscustomobject]@{
                    id = '{0}|{1}' -f $root.Hive, $key.PSChildName
                    hive = $root.Hive
                    keyPath = $keyPath
                    keyName = $key.PSChildName
                    productCode = Resolve-MsiProductCodeFromKeyName -KeyName $key.PSChildName
                    displayName = [string]$props.DisplayName
                    displayVersion = [string]$props.DisplayVersion
                    publisher = [string]$props.Publisher
                    installLocation = [string]$props.InstallLocation
                    displayIcon = [string]$props.DisplayIcon
                    uninstallString = [string]$props.UninstallString
                    quietUninstallString = [string]$props.QuietUninstallString
                    systemComponent = [string]$props.SystemComponent
                }
            }
            catch {
                Write-ProofLog "Failed to read uninstall key $($key.Name): $($_.Exception.Message)"
            }
        }
    }

    return @($items)
}

function Get-ProgramDirectorySnapshot {
    $localPrograms = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { '' } else { Join-Path $env:LOCALAPPDATA 'Programs' }
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $localPrograms, $env:LOCALAPPDATA, $env:ProgramData) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    $items = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            $items += [pscustomobject]@{
                id = $directory.FullName.ToLowerInvariant()
                root = $root
                name = $directory.Name
                fullName = $directory.FullName
                lastWriteTimeUtc = $directory.LastWriteTimeUtc.ToString('o')
            }
        }
    }

    return @($items)
}

function Get-ExecutableSnapshot {
    $localPrograms = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { '' } else { Join-Path $env:LOCALAPPDATA 'Programs' }
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $localPrograms, $env:ProgramData) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    $items = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        try {
            foreach ($file in @(Get-ChildItem -LiteralPath $root -Filter '*.exe' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 5000)) {
                $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName)
                $version = [string]$versionInfo.ProductVersion
                if ([string]::IsNullOrWhiteSpace($version)) { $version = [string]$versionInfo.FileVersion }
                $items += [pscustomobject]@{
                    id = $file.FullName.ToLowerInvariant()
                    root = $root
                    name = $file.Name
                    fullName = $file.FullName
                    directoryName = $file.DirectoryName
                    length = $file.Length
                    lastWriteTimeUtc = $file.LastWriteTimeUtc.ToString('o')
                    version = $version
                    productName = [string]$versionInfo.ProductName
                    fileDescription = [string]$versionInfo.FileDescription
                    companyName = [string]$versionInfo.CompanyName
                }
            }
        }
        catch {
            Write-ProofLog "Executable snapshot failed for ${root}: $($_.Exception.Message)"
        }
    }

    return @($items)
}

function Get-ServiceSnapshot {
    try {
        return @(Get-CimInstance -ClassName Win32_Service -ErrorAction Stop | ForEach-Object {
            [pscustomobject]@{
                id = $_.Name
                name = $_.Name
                displayName = $_.DisplayName
                status = [string]$_.Status
                pathName = [string]$_.PathName
                startName = [string]$_.StartName
            }
        })
    }
    catch {
        Write-ProofLog "Service snapshot failed: $($_.Exception.Message)"
        return @()
    }
}

function Get-TaskSnapshot {
    try {
        return @(Get-ScheduledTask -ErrorAction SilentlyContinue | ForEach-Object {
            $actions = @($_.Actions | ForEach-Object {
                [pscustomobject]@{
                    execute = [string]$_.Execute
                    arguments = [string]$_.Arguments
                    workingDirectory = [string]$_.WorkingDirectory
                }
            })

            [pscustomobject]@{
                id = '{0}{1}' -f $_.TaskPath, $_.TaskName
                taskPath = $_.TaskPath
                taskName = $_.TaskName
                state = [string]$_.State
                actions = $actions
            }
        })
    }
    catch {
        Write-ProofLog "Scheduled task snapshot failed: $($_.Exception.Message)"
        return @()
    }
}

function Get-ShortcutSnapshot {
    $roots = @(
        "$env:ProgramData\Microsoft\Windows\Start Menu\Programs",
        "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
    )

    $items = @()
    $shell = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
    }
    catch {
        Write-ProofLog "Shortcut target resolution unavailable: $($_.Exception.Message)"
    }

    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($shortcut in Get-ChildItem -LiteralPath $root -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue) {
            $targetPath = ''
            $arguments = ''
            $workingDirectory = ''
            if ($null -ne $shell) {
                try {
                    $link = $shell.CreateShortcut($shortcut.FullName)
                    $targetPath = [string]$link.TargetPath
                    $arguments = [string]$link.Arguments
                    $workingDirectory = [string]$link.WorkingDirectory
                }
                catch {
                    Write-ProofLog "Failed to resolve shortcut $($shortcut.FullName): $($_.Exception.Message)"
                }
            }

            $items += [pscustomobject]@{
                id = $shortcut.FullName.ToLowerInvariant()
                name = $shortcut.Name
                fullName = $shortcut.FullName
                targetPath = $targetPath
                arguments = $arguments
                workingDirectory = $workingDirectory
                lastWriteTimeUtc = $shortcut.LastWriteTimeUtc.ToString('o')
            }
        }
    }

    return @($items)
}

function Get-Snapshot {
    param([string]$Name)
    Write-ProofLog "Capturing $Name snapshot"
    return [pscustomobject]@{
        name = $Name
        capturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        uninstallEntries = @(Get-UninstallSnapshot)
        programDirectories = @(Get-ProgramDirectorySnapshot)
        executables = @(Get-ExecutableSnapshot)
        services = @(Get-ServiceSnapshot)
        scheduledTasks = @(Get-TaskSnapshot)
        shortcuts = @(Get-ShortcutSnapshot)
    }
}

function Compare-ById {
    param($Before, $After)
    $beforeIds = @{}
    foreach ($item in @($Before)) {
        if ($null -ne $item.id) { $beforeIds[[string]$item.id] = $true }
    }

    $newItems = @()
    foreach ($item in @($After)) {
        if ($null -eq $item.id -or -not $beforeIds.ContainsKey([string]$item.id)) {
            $newItems += $item
        }
    }

    return @($newItems)
}

function Compare-ProofValue {
    param([string]$Actual, [string]$Expected, [string]$Operator)
    $Actual = ([string]$Actual).Trim()
    $Expected = ([string]$Expected).Trim()
    if ($Operator -eq 'Exists') { return -not [string]::IsNullOrWhiteSpace($Actual) }
    if ($Operator -eq 'Equals') { return $Actual -ieq $Expected }
    if ($Operator -eq 'NotEquals') { return $Actual -ine $Expected }

    $actualVersion = $null
    $expectedVersion = $null
    if ([version]::TryParse($Actual, [ref]$actualVersion) -and [version]::TryParse($Expected, [ref]$expectedVersion)) {
        $compare = $actualVersion.CompareTo($expectedVersion)
    }
    else {
        $actualNumber = 0.0
        $expectedNumber = 0.0
        if ([double]::TryParse($Actual, [System.Globalization.NumberStyles]::Any, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$actualNumber) -and
            [double]::TryParse($Expected, [System.Globalization.NumberStyles]::Any, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$expectedNumber)) {
            $compare = $actualNumber.CompareTo($expectedNumber)
        }
        else {
            $compare = [string]::Compare($Actual, $Expected, $true, [System.Globalization.CultureInfo]::InvariantCulture)
        }
    }

    switch ($Operator) {
        'GreaterThan' { return $compare -gt 0 }
        'GreaterThanOrEqual' { return $compare -ge 0 }
        'LessThan' { return $compare -lt 0 }
        'LessThanOrEqual' { return $compare -le 0 }
        default { return $false }
    }
}

function Test-ProofRegistryDetection {
    param($Rule)
    if ($null -eq $Rule -or [string]::IsNullOrWhiteSpace($Rule.keyPath)) {
        return [pscustomobject]@{ success = $false; summary = 'Registry detection missing key path.'; details = '' }
    }

    $hive = [string]$Rule.hive
    $providerRoot = switch -Regex ($hive) {
        'HKCU|HKEY_CURRENT_USER' { 'HKCU:'; break }
        default { 'HKLM:' }
    }

    $keyPath = [string]$Rule.keyPath
    if ($Rule.check32BitOn64System -and $providerRoot -eq 'HKLM:' -and $keyPath -like 'SOFTWARE\*' -and $keyPath -notlike 'SOFTWARE\WOW6432Node\*') {
        $keyPath = $keyPath -replace '^SOFTWARE\\', 'SOFTWARE\WOW6432Node\'
    }

    $fullPath = Join-Path $providerRoot $keyPath
    $exists = Test-Path -LiteralPath $fullPath
    if ($Rule.operator -eq 'Exists') {
        if (-not $exists) { return [pscustomobject]@{ success = $false; summary = 'Registry key not found.'; details = $fullPath } }
        if ([string]::IsNullOrWhiteSpace($Rule.valueName)) {
            return [pscustomobject]@{ success = $true; summary = 'Registry key exists.'; details = $fullPath }
        }

        $props = Get-ItemProperty -LiteralPath $fullPath -ErrorAction SilentlyContinue
        $valueProperty = $props.PSObject.Properties[[string]$Rule.valueName]
        $value = if ($null -ne $valueProperty) { $valueProperty.Value } else { $null }
        return [pscustomobject]@{
            success = $null -ne $value
            summary = if ($null -ne $value) { 'Registry value exists.' } else { 'Registry value not found.' }
            details = "$fullPath :: $($Rule.valueName)"
        }
    }

    if (-not $exists -or [string]::IsNullOrWhiteSpace($Rule.valueName)) {
        return [pscustomobject]@{ success = $false; summary = 'Registry comparison target not found.'; details = $fullPath }
    }

    $props = Get-ItemProperty -LiteralPath $fullPath -ErrorAction SilentlyContinue
    $valueProperty = $props.PSObject.Properties[[string]$Rule.valueName]
    $actual = if ($null -ne $valueProperty) { [string]$valueProperty.Value } else { '' }
    $ok = Compare-ProofValue -Actual $actual -Expected ([string]$Rule.value) -Operator ([string]$Rule.operator)
    return [pscustomobject]@{
        success = $ok
        summary = if ($ok) { 'Registry comparison passed.' } else { 'Registry comparison failed.' }
        details = "Actual='$actual', Expected='$($Rule.value)', Operator='$($Rule.operator)'"
    }
}

function Test-ProofFileDetection {
    param($Rule)
    if ($null -eq $Rule -or [string]::IsNullOrWhiteSpace($Rule.path) -or [string]::IsNullOrWhiteSpace($Rule.fileOrFolderName)) {
        return [pscustomobject]@{ success = $false; summary = 'File detection missing path or name.'; details = '' }
    }

    $target = Join-Path ([Environment]::ExpandEnvironmentVariables([string]$Rule.path)) ([string]$Rule.fileOrFolderName)
    $fileExists = Test-Path -LiteralPath $target -PathType Leaf
    $folderExists = Test-Path -LiteralPath $target -PathType Container
    if ($Rule.operator -eq 'Exists') {
        return [pscustomobject]@{
            success = ($fileExists -or $folderExists)
            summary = if ($fileExists -or $folderExists) { 'File detection exists check passed.' } else { 'File detection target missing.' }
            details = $target
        }
    }

    if (-not ($fileExists -or $folderExists)) {
        return [pscustomobject]@{ success = $false; summary = 'File comparison target missing.'; details = $target }
    }

    $actual = ''
    if ($fileExists) {
        $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($target)
        $actual = [string]$versionInfo.ProductVersion
        if ([string]::IsNullOrWhiteSpace($actual)) {
            $actual = [string]$versionInfo.FileVersion
        }
    }
    if ([string]::IsNullOrWhiteSpace($actual)) {
        $actual = if ($fileExists) { [string](Get-Item -LiteralPath $target).Length } else { [string](Get-Item -LiteralPath $target).LastWriteTimeUtc.Ticks }
    }

    $ok = Compare-ProofValue -Actual $actual -Expected ([string]$Rule.value) -Operator ([string]$Rule.operator)
    return [pscustomobject]@{
        success = $ok
        summary = if ($ok) { 'File comparison passed.' } else { 'File comparison failed.' }
        details = "Target='$target', Actual='$actual', Expected='$($Rule.value)', Operator='$($Rule.operator)'"
    }
}

function Test-ProofMsiDetection {
    param($Rule)
    $productCode = ([string]$Rule.productCode).Trim()
    if ([string]::IsNullOrWhiteSpace($productCode)) {
        return [pscustomobject]@{ success = $false; summary = 'MSI detection missing ProductCode.'; details = '' }
    }

    $roots = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$productCode",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$productCode",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$productCode"
    )

    foreach ($path in $roots) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        if ([string]::IsNullOrWhiteSpace($Rule.productVersion)) {
            return [pscustomobject]@{ success = $true; summary = 'MSI ProductCode exists.'; details = $path }
        }

        $props = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
        $actual = [string]$props.DisplayVersion
        $ok = Compare-ProofValue -Actual $actual -Expected ([string]$Rule.productVersion) -Operator ([string]$Rule.productVersionOperator)
        return [pscustomobject]@{
            success = $ok
            summary = if ($ok) { 'MSI version comparison passed.' } else { 'MSI version comparison failed.' }
            details = "Path='$path', Actual='$actual', Expected='$($Rule.productVersion)'"
        }
    }

    return [pscustomobject]@{ success = $false; summary = 'MSI ProductCode not found.'; details = $productCode }
}

function Test-ProofScriptDetection {
    param($Rule)
    if ($null -eq $Rule -or [string]::IsNullOrWhiteSpace($Rule.scriptBody)) {
        return [pscustomobject]@{ success = $false; summary = 'Script detection body is empty.'; details = '' }
    }

    Set-Content -LiteralPath $DetectionScriptPath -Value ([string]$Rule.scriptBody) -Encoding UTF8
    $stdout = Join-Path $LogsPath 'detection-stdout.txt'
    $stderr = Join-Path $LogsPath 'detection-stderr.txt'
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File', $DetectionScriptPath) -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $stdOutText = if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -Raw } else { '' }
    $stdErrText = if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -Raw } else { '' }
    $ok = $process.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($stdOutText) -and [string]::IsNullOrWhiteSpace($stdErrText)
    return [pscustomobject]@{
        success = $ok
        summary = if ($ok) { 'Script detection passed Intune signal checks.' } else { 'Script detection failed Intune signal checks.' }
        details = "ExitCode=$($process.ExitCode), HasStdOut=$(-not [string]::IsNullOrWhiteSpace($stdOutText)), HasStdErr=$(-not [string]::IsNullOrWhiteSpace($stdErrText))"
    }
}

function Test-ProofDetection {
    param($Rule)
    if ($null -eq $Rule -or $Rule.ruleType -eq 'None') {
        return [pscustomobject]@{ success = $false; summary = 'No detection rule configured.'; details = '' }
    }

    switch ([string]$Rule.ruleType) {
        'MsiProductCode' { return Test-ProofMsiDetection -Rule $Rule.msi }
        'File' { return Test-ProofFileDetection -Rule $Rule.file }
        'Registry' { return Test-ProofRegistryDetection -Rule $Rule.registry }
        'Script' { return Test-ProofScriptDetection -Rule $Rule.script }
        default { return [pscustomobject]@{ success = $false; summary = "Unsupported detection rule type '$($Rule.ruleType)'."; details = '' } }
    }
}

function Invoke-ProofCommand {
    param([string]$Command, [string]$WorkingDirectory, [int]$TimeoutMinutes, [string]$Phase = 'command')
    if ([string]::IsNullOrWhiteSpace($Command)) {
        return [pscustomobject]@{ exitCode = -1; timedOut = $false; stdout = ''; stderr = 'No command was provided.' }
    }

    if ([string]::IsNullOrWhiteSpace($WorkingDirectory) -or -not (Test-Path -LiteralPath $WorkingDirectory)) {
        $WorkingDirectory = $ProofRoot
    }

    $safePhase = if ([string]::IsNullOrWhiteSpace($Phase)) { 'command' } else { ($Phase -replace '[^a-zA-Z0-9._-]+', '-') }
    $stdout = Join-Path $LogsPath "$safePhase-stdout.txt"
    $stderr = Join-Path $LogsPath "$safePhase-stderr.txt"
    Write-ProofLog "Running ${Phase} command: $Command"
    Write-ProofLog "Working directory: $WorkingDirectory"

    try {
        return Invoke-ProofCommandAsSystem -Command $Command -WorkingDirectory $WorkingDirectory -TimeoutMinutes $TimeoutMinutes -Phase $safePhase -StdoutPath $stdout -StderrPath $stderr
    }
    catch {
        Write-ProofLog "SYSTEM scheduled task execution failed for ${Phase}; falling back to direct command: $($_.Exception.Message)"
        return Invoke-ProofCommandDirect -Command $Command -WorkingDirectory $WorkingDirectory -TimeoutMinutes $TimeoutMinutes -Phase $Phase -StdoutPath $stdout -StderrPath $stderr
    }
}

function Invoke-ProofCommandAsSystem {
    param(
        [string]$Command,
        [string]$WorkingDirectory,
        [int]$TimeoutMinutes,
        [string]$Phase,
        [string]$StdoutPath,
        [string]$StderrPath
    )

    $exitCodePath = Join-Path $LogsPath "$Phase-exitcode.txt"
    $cmdPath = Join-Path $LogsPath "$Phase-command.cmd"
    $taskName = "IwpSandboxProof-$Phase-$([guid]::NewGuid().ToString('N'))"
    $escapedWorkingDirectory = $WorkingDirectory.Replace('"', '""')
    $escapedStdout = $StdoutPath.Replace('"', '""')
    $escapedStderr = $StderrPath.Replace('"', '""')
    $escapedExitCode = $exitCodePath.Replace('"', '""')

    $cmdLines = @(
        '@echo off',
        "cd /d `"$escapedWorkingDirectory`"",
        "call $Command > `"$escapedStdout`" 2> `"$escapedStderr`"",
        "echo %ERRORLEVEL%> `"$escapedExitCode`"",
        "exit /b %ERRORLEVEL%"
    )
    Set-Content -LiteralPath $cmdPath -Value $cmdLines -Encoding ASCII

    Write-ProofLog "Executing ${Phase} command as SYSTEM via scheduled task $taskName."
    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/d /c `"$cmdPath`""
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null

    $timedOut = $false
    $taskLastResult = $null
    try {
        $startedAt = Get-Date
        Start-ScheduledTask -TaskName $taskName
        $deadline = (Get-Date).AddMilliseconds([Math]::Max(5, $TimeoutMinutes) * 60 * 1000)
        do {
            Start-Sleep -Milliseconds 500
            if (Test-Path -LiteralPath $exitCodePath) { break }

            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            $state = if ($null -eq $task) { '' } else { [string]$task.State }
            $taskInfo = $null
            try { $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction SilentlyContinue } catch {}
            if ($null -ne $taskInfo) { $taskLastResult = $taskInfo.LastTaskResult }

            if ($state -eq 'Running') {
                continue
            }

            if ($state -eq 'Ready') {
                if ($null -ne $taskInfo -and $taskInfo.LastRunTime -gt $startedAt.AddSeconds(-2)) {
                    Start-Sleep -Milliseconds 500
                    if (Test-Path -LiteralPath $exitCodePath) { break }
                    Write-ProofLog "$Phase scheduled task finished without writing exit code file. State: $state; LastTaskResult: $taskLastResult."
                    break
                }

                continue
            }

            if (-not [string]::IsNullOrWhiteSpace($state)) {
                Start-Sleep -Milliseconds 500
                if (Test-Path -LiteralPath $exitCodePath) { break }
                Write-ProofLog "$Phase scheduled task stopped before writing exit code file. State: $state; LastTaskResult: $taskLastResult."
                break
            }
        } while ((Get-Date) -lt $deadline)

        if (-not (Test-Path -LiteralPath $exitCodePath)) {
            $timedOut = $true
            Write-ProofLog "$Phase command timed out after $TimeoutMinutes minute(s); stopping scheduled task."
            try { Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue } catch {}
        }
    }
    finally {
        try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue } catch {}
    }

    $stdoutText = if (Test-Path -LiteralPath $StdoutPath) { Get-Content -LiteralPath $StdoutPath -Raw } else { '' }
    $stderrText = if (Test-Path -LiteralPath $StderrPath) { Get-Content -LiteralPath $StderrPath -Raw } else { '' }
    $exitCodeText = if (Test-Path -LiteralPath $exitCodePath) { (Get-Content -LiteralPath $exitCodePath -Raw).Trim() } else { '' }
    $exitCode = 0
    if (-not [int]::TryParse($exitCodeText, [ref]$exitCode)) {
        $taskLastResultText = if ($null -eq $taskLastResult) { '' } else { [string]$taskLastResult }
        if ($timedOut) {
            $exitCode = -1
        }
        elseif (-not [int]::TryParse($taskLastResultText, [ref]$exitCode)) {
            $exitCode = 1
        }
    }

    return [pscustomobject]@{
        command = $Command
        executionMode = 'ScheduledTaskSystem'
        exitCode = if ($timedOut) { -1 } else { $exitCode }
        timedOut = $timedOut
        stdout = $stdoutText
        stderr = $stderrText
    }
}

function Invoke-ProofCommandDirect {
    param(
        [string]$Command,
        [string]$WorkingDirectory,
        [int]$TimeoutMinutes,
        [string]$Phase,
        [string]$StdoutPath,
        [string]$StderrPath
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'cmd.exe'
    $psi.Arguments = "/d /s /c call $Command"
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    [void]$process.Start()

    $stdOutTask = $process.StandardOutput.ReadToEndAsync()
    $stdErrTask = $process.StandardError.ReadToEndAsync()
    $timeoutMs = [Math]::Max(5, $TimeoutMinutes) * 60 * 1000
    $exited = $process.WaitForExit($timeoutMs)
    if (-not $exited) {
        Write-ProofLog "$Phase command timed out after $TimeoutMinutes minute(s); terminating process tree."
        try { $process.Kill($true) } catch { try { $process.Kill() } catch {} }
    }

    $stdoutText = $stdOutTask.GetAwaiter().GetResult()
    $stderrText = $stdErrTask.GetAwaiter().GetResult()
    Set-Content -LiteralPath $StdoutPath -Value $stdoutText -Encoding UTF8
    Set-Content -LiteralPath $StderrPath -Value $stderrText -Encoding UTF8

    return [pscustomobject]@{
        command = $Command
        executionMode = 'DirectUser'
        exitCode = if ($exited) { $process.ExitCode } else { -1 }
        timedOut = -not $exited
        stdout = $stdoutText
        stderr = $stderrText
    }
}

function Test-ProofCommandSucceeded {
    param($Result)
    if ($null -eq $Result) { return $false }
    return -not [bool]$Result.timedOut -and ([int]$Result.exitCode -in @(0, 3010, 1641))
}

function Resolve-PathCandidate {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    $trimmed = [Environment]::ExpandEnvironmentVariables($Value.Trim())
    if ($trimmed -match '^\s*"([^"]+)"') { return $matches[1] }
    if ($trimmed -match '([A-Za-z]:\\[^,]+?\.exe)') { return $matches[1].Trim() }
    if ($trimmed -match '([A-Za-z]:\\[^,]+?\.msi)') { return $matches[1].Trim() }
    $literal = $trimmed.Trim('"')
    if (Test-Path -LiteralPath $literal -ErrorAction SilentlyContinue) { return $literal }
    return ''
}

function Split-FileDetectionTarget {
    param([string]$Target)
    $expanded = [Environment]::ExpandEnvironmentVariables(([string]$Target).Trim().Trim('"'))
    if ([string]::IsNullOrWhiteSpace($expanded) -or -not (Test-Path -LiteralPath $expanded)) { return $null }
    $item = Get-Item -LiteralPath $expanded -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    $version = ''
    $productName = ''
    $fileDescription = ''
    $companyName = ''
    if (-not $item.PSIsContainer) {
        $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($item.FullName)
        $version = [string]$versionInfo.ProductVersion
        if ([string]::IsNullOrWhiteSpace($version)) { $version = [string]$versionInfo.FileVersion }
        $productName = [string]$versionInfo.ProductName
        $fileDescription = [string]$versionInfo.FileDescription
        $companyName = [string]$versionInfo.CompanyName
    }

    return [pscustomobject]@{
        fullName = $item.FullName
        path = if ($item.PSIsContainer) { $item.Parent.FullName } else { $item.DirectoryName }
        name = $item.Name
        isFile = -not $item.PSIsContainer
        version = $version
        productName = $productName
        fileDescription = $fileDescription
        companyName = $companyName
    }
}

function New-Candidate {
    param([string]$Type, [string]$Confidence, [int]$Score, [string]$Reason, $Rule, $AdditionalRules, $Evidence, $NegativePhase)
    if ($null -eq $NegativePhase) {
        $NegativePhase = [pscustomobject]@{
            success = $true
            summary = 'Candidate source was absent before install and present after install.'
            details = ''
        }
    }

    return [pscustomobject]@{
        type = $Type
        confidence = $Confidence
        score = $Score
        reason = $Reason
        rule = $Rule
        additionalRules = @($AdditionalRules)
        evidence = $Evidence
        proof = [pscustomobject]@{
            success = $false
            summary = 'Candidate has not been validated yet.'
            negativePhase = $NegativePhase
            positivePhase = [pscustomobject]@{
                success = $false
                summary = 'Post-install rule validation has not run yet.'
                details = ''
            }
        }
    }
}

function Test-ProofDetectionRuleSet {
    param($Candidate)
    $rules = @($Candidate.rule)
    if ($null -ne $Candidate.additionalRules) {
        $rules += @($Candidate.additionalRules)
    }

    $results = @()
    $index = 0
    foreach ($rule in @($rules)) {
        $index += 1
        if ($null -eq $rule -or [string]$rule.ruleType -eq 'None') { continue }
        $result = Test-ProofDetection -Rule $rule
        $results += [pscustomobject]@{
            index = $index
            ruleType = [string]$rule.ruleType
            success = [bool]$result.success
            summary = [string]$result.summary
            details = [string]$result.details
        }
    }

    $failed = @($results | Where-Object { -not $_.success })
    return [pscustomobject]@{
        success = $results.Count -gt 0 -and $failed.Count -eq 0
        summary = if ($failed.Count -eq 0 -and $results.Count -gt 0) { "Rule set validation passed for $($results.Count) rule(s)." } else { "Rule set validation failed for $($failed.Count) of $($results.Count) rule(s)." }
        details = ($results | ForEach-Object { "Rule $($_.index) [$($_.ruleType)]: $($_.summary) $($_.details)" }) -join ' | '
    }
}

function Complete-CandidateProof {
    param($Candidate)
    if ($null -eq $Candidate -or $null -eq $Candidate.rule) { return $Candidate }

    $negativePhase = if ($null -ne $Candidate.proof -and $null -ne $Candidate.proof.negativePhase) {
        $Candidate.proof.negativePhase
    } else {
        [pscustomobject]@{
            success = $true
            summary = 'Candidate source was absent before install and present after install.'
            details = ''
        }
    }

    $positive = Test-ProofDetectionRuleSet -Candidate $Candidate
    $positivePhase = [pscustomobject]@{
        success = [bool]$positive.success
        summary = [string]$positive.summary
        details = [string]$positive.details
    }
    $success = [bool]$negativePhase.success -and [bool]$positivePhase.success
    $Candidate.proof = [pscustomobject]@{
        success = $success
        summary = if ($success) { 'Candidate passed sandbox two-phase validation.' } else { 'Candidate failed sandbox validation.' }
        negativePhase = $negativePhase
        positivePhase = $positivePhase
    }
    return $Candidate
}

function Test-ProofDetectionRuleSetAbsent {
    param($Candidate)
    $rules = @($Candidate.rule)
    if ($null -ne $Candidate.additionalRules) {
        $rules += @($Candidate.additionalRules)
    }

    $results = @()
    $index = 0
    foreach ($rule in @($rules)) {
        $index += 1
        if ($null -eq $rule -or [string]$rule.ruleType -eq 'None') { continue }
        $result = Test-ProofDetection -Rule $rule
        $results += [pscustomobject]@{
            index = $index
            ruleType = [string]$rule.ruleType
            success = -not [bool]$result.success
            summary = if ([bool]$result.success) { 'Rule still matched after uninstall.' } else { 'Rule no longer matched after uninstall.' }
            details = [string]$result.details
        }
    }

    $failed = @($results | Where-Object { -not $_.success })
    return [pscustomobject]@{
        success = $results.Count -gt 0 -and $failed.Count -eq 0
        summary = if ($failed.Count -eq 0 -and $results.Count -gt 0) { "Uninstall validation cleared $($results.Count) detection rule(s)." } else { "Uninstall validation failed for $($failed.Count) of $($results.Count) detection rule(s)." }
        details = ($results | ForEach-Object { "Rule $($_.index) [$($_.ruleType)]: $($_.summary) $($_.details)" }) -join ' | '
    }
}

function Complete-CandidateUninstallProof {
    param($Candidate)
    if ($null -eq $Candidate -or $null -eq $Candidate.rule) { return $Candidate }

    $uninstall = Test-ProofDetectionRuleSetAbsent -Candidate $Candidate
    $uninstallPhase = [pscustomobject]@{
        success = [bool]$uninstall.success
        summary = [string]$uninstall.summary
        details = [string]$uninstall.details
    }

    if ($null -eq $Candidate.proof) {
        $Candidate | Add-Member -NotePropertyName proof -NotePropertyValue ([pscustomobject]@{}) -Force
    }

    $installAndDetectionSuccess = [bool]$Candidate.proof.success
    $Candidate.proof | Add-Member -NotePropertyName uninstallPhase -NotePropertyValue $uninstallPhase -Force
    $Candidate.proof.success = $installAndDetectionSuccess -and [bool]$uninstallPhase.success
    $Candidate.proof.summary = if ([bool]$Candidate.proof.success) {
        'Candidate passed sandbox install, detection, and uninstall validation.'
    } elseif (-not $installAndDetectionSuccess) {
        'Candidate failed sandbox install/detection validation.'
    } else {
        'Candidate failed sandbox uninstall validation.'
    }

    return $Candidate
}

function New-ConfiguredDetectionCandidate {
    param($Rule, $PreDetection, $ExistingCandidates)
    if ($null -eq $Rule -or [string]$Rule.ruleType -eq 'None') { return $null }
    if (-not (Test-DetectionRuleCandidateAllowed -Rule $Rule)) { return $null }

    $identity = Get-RuleIdentity -Rule $Rule
    foreach ($candidate in @($ExistingCandidates)) {
        if ((Get-RuleIdentity -Rule $candidate.rule) -eq $identity) {
            return $null
        }
    }

    $negative = [pscustomobject]@{
        success = -not [bool]$PreDetection.success
        summary = if ([bool]$PreDetection.success) { 'Configured detection already matched before install.' } else { 'Configured detection did not match before install.' }
        details = [string]$PreDetection.details
    }

    return New-Candidate `
        -Type 'ConfiguredDetection' `
        -Confidence 'High' `
        -Score 93 `
        -Reason 'Detection rule was already configured before Sandbox Proof and was validated against clean install evidence.' `
        -Evidence ([pscustomobject]@{ source = 'precheck'; summary = [string]$PreDetection.summary }) `
        -Rule $Rule `
        -AdditionalRules @() `
        -NegativePhase $negative
}

function Normalize-DetectionText {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return (($Value -replace '[^a-zA-Z0-9]+', ' ').Trim()).ToLowerInvariant()
}

function Test-DetectionTextMatch {
    param([string]$First, [string]$Second)
    $a = Normalize-DetectionText -Value $First
    $b = Normalize-DetectionText -Value $Second
    if ($a.Length -lt 3 -or $b.Length -lt 3) { return $false }
    return $a.Contains($b) -or $b.Contains($a)
}

function New-FileDetectionRule {
    param($FileTarget)
    return [pscustomobject]@{
        ruleType = 'File'
        file = [pscustomobject]@{
            path = $FileTarget.path
            fileOrFolderName = $FileTarget.name
            check32BitOn64System = $false
            operator = if (-not [string]::IsNullOrWhiteSpace($FileTarget.version)) { 'GreaterThanOrEqual' } else { 'Exists' }
            value = if (-not [string]::IsNullOrWhiteSpace($FileTarget.version)) { $FileTarget.version } else { '' }
        }
    }
}

function New-RegistryDetectionRule {
    param($Entry, [string]$ValueName, [string]$Operator, [string]$Value)
    return [pscustomobject]@{
        ruleType = 'Registry'
        registry = [pscustomobject]@{
            hive = if ($Entry.hive -eq 'HKCU') { 'HKEY_CURRENT_USER' } else { 'HKEY_LOCAL_MACHINE' }
            keyPath = $Entry.keyPath
            valueName = $ValueName
            check32BitOn64System = $false
            operator = $Operator
            value = $Value
        }
    }
}

function New-RegistryIdentityAdditionalRules {
    param($Entry, [string]$PrimaryValueName)
    $rules = @()

    if (-not [string]::IsNullOrWhiteSpace($entry.displayName) -and $PrimaryValueName -ne 'DisplayName') {
        $rules += New-RegistryDetectionRule -Entry $Entry -ValueName 'DisplayName' -Operator 'Equals' -Value ([string]$entry.displayName)
    }

    if (-not [string]::IsNullOrWhiteSpace($entry.publisher) -and $PrimaryValueName -ne 'Publisher') {
        $rules += New-RegistryDetectionRule -Entry $Entry -ValueName 'Publisher' -Operator 'Equals' -Value ([string]$entry.publisher)
    }

    if (-not [string]::IsNullOrWhiteSpace($entry.displayVersion) -and $PrimaryValueName -ne 'DisplayVersion') {
        $rules += New-RegistryDetectionRule -Entry $Entry -ValueName 'DisplayVersion' -Operator 'GreaterThanOrEqual' -Value ([string]$entry.displayVersion)
    }

    return @($rules)
}

function New-MsiDetectionRule {
    param($Entry)
    return [pscustomobject]@{
        ruleType = 'MsiProductCode'
        msi = [pscustomobject]@{
            productCode = $Entry.productCode
            productVersion = [string]$Entry.displayVersion
            productVersionOperator = 'GreaterThanOrEqual'
        }
    }
}

function Get-RuleIdentity {
    param($Rule)
    if ($null -eq $Rule) { return '' }

    switch ([string]$Rule.ruleType) {
        'MsiProductCode' {
            return "msi|$($Rule.msi.productCode)|$($Rule.msi.productVersion)|$($Rule.msi.productVersionOperator)".ToLowerInvariant()
        }
        'File' {
            return "file|$($Rule.file.path)|$($Rule.file.fileOrFolderName)|$($Rule.file.operator)|$($Rule.file.value)".ToLowerInvariant()
        }
        'Registry' {
            return "registry|$($Rule.registry.hive)|$($Rule.registry.keyPath)|$($Rule.registry.valueName)|$($Rule.registry.operator)|$($Rule.registry.value)".ToLowerInvariant()
        }
        'Script' {
            return "script|$($Rule.script.scriptBody)".ToLowerInvariant()
        }
        default {
            return ([string]$Rule.ruleType).ToLowerInvariant()
        }
    }
}

function Test-DetectionRuleCandidateAllowed {
    param($Rule)
    if ($null -eq $Rule) { return $false }

    if ([string]$Rule.ruleType -ne 'File') { return $true }
    if ($null -eq $Rule.file) { return $false }

    $target = Join-Path ([Environment]::ExpandEnvironmentVariables([string]$Rule.file.path)) ([string]$Rule.file.fileOrFolderName)
    $targetName = [IO.Path]::GetFileName($target)
    if ($targetName -match '(?i)(^setup|setup$|install|unins|uninstall|updater|update|crash|helper|repair)') {
        return $false
    }

    $windowsRoot = [Environment]::GetFolderPath('Windows')
    $packageCacheRoot = if ([string]::IsNullOrWhiteSpace($env:ProgramData)) { '' } else { Join-Path $env:ProgramData 'Package Cache' }
    foreach ($blockedRoot in @($windowsRoot, $ProofRoot, $SandboxSourceRoot, $env:TEMP, $env:TMP, $packageCacheRoot)) {
        if ([string]::IsNullOrWhiteSpace($blockedRoot)) { continue }
        $normalizedRoot = $blockedRoot.TrimEnd('\')
        if ($target.StartsWith($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Test-PathUnderAnyRoot {
    param([string]$Path, $RootMap)
    if ([string]::IsNullOrWhiteSpace($Path) -or $null -eq $RootMap) { return $false }
    $normalizedPath = $Path.TrimEnd('\')
    foreach ($root in $RootMap.Keys) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        if ($normalizedPath.Equals($root, [System.StringComparison]::OrdinalIgnoreCase) -or
            $normalizedPath.StartsWith("$root\", [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function New-FileCandidateNegativePhase {
    param($FileTarget, $NewExecutableMap, $NewProgramDirectoryMap, [string]$FallbackSummary, [string]$FallbackDetails)

    if ($null -eq $FileTarget) {
        return [pscustomobject]@{ success = $false; summary = 'File detection target could not be resolved.'; details = '' }
    }

    $fullName = [string]$FileTarget.fullName
    $identity = $fullName.ToLowerInvariant()
    if ($FileTarget.isFile -and $null -ne $NewExecutableMap -and $NewExecutableMap.ContainsKey($identity)) {
        return [pscustomobject]@{
            success = $true
            summary = 'Executable target was absent before install and present after install.'
            details = $fullName
        }
    }

    if (Test-PathUnderAnyRoot -Path $fullName -RootMap $NewProgramDirectoryMap) {
        return [pscustomobject]@{
            success = $true
            summary = 'Detection target is inside a newly created install directory.'
            details = $fullName
        }
    }

    if (-not $FileTarget.isFile -and $null -ne $NewProgramDirectoryMap -and $NewProgramDirectoryMap.ContainsKey($identity)) {
        return [pscustomobject]@{
            success = $true
            summary = 'Install directory was absent before install and present after install.'
            details = $fullName
        }
    }

    return [pscustomobject]@{
        success = $false
        summary = 'Detection target was not proven absent before install.'
        details = if ([string]::IsNullOrWhiteSpace($FallbackDetails)) { $fullName } else { "$fullName | $FallbackSummary $FallbackDetails" }
    }
}

function Test-UninstallEntryLooksLikeDependency {
    param($Entry)
    $displayName = [string]$Entry.displayName
    $publisher = [string]$Entry.publisher
    if ([string]::IsNullOrWhiteSpace($displayName)) { return $false }

    if ($displayName -match '(?i)microsoft\s+visual\s+c\+\+.*redistributable') { return $true }
    if ($displayName -match '(?i)microsoft\s+visual\s+c\+\+.*runtime') { return $true }
    if ($displayName -match '(?i)visual\s+c\+\+.*runtime') { return $true }
    if ($displayName -match '(?i)microsoft\s+edge\s+webview2\s+runtime') { return $true }
    if ($displayName -match '(?i)microsoft\s+edge\s+update') { return $true }
    if ($displayName -match '(?i)\.net\s+(desktop\s+)?runtime') { return $true }
    if ($displayName -match '(?i)windows\s+desktop\s+runtime') { return $true }
    if ($publisher -match '(?i)^microsoft' -and $displayName -match '(?i)(redistributable|runtime|webview2)') { return $true }

    return $false
}

function Test-CommandHasPlaceholder {
    param([string]$Command)
    if ([string]::IsNullOrWhiteSpace($Command)) { return $true }
    return $Command.Contains('<') -and $Command.Contains('>')
}

function Convert-ToSilentUninstallCommand {
    param($Entry, [string]$Command, [string]$Source)
    $trimmed = ([string]$Command).Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) { return $null }

    if ($trimmed -match '(?i)\bmsiexec(\.exe)?\b') {
        $normalized = $trimmed -replace '(?i)/(i|package)(?=\{)', '/x'
        $normalized = $normalized -replace '(?i)/(i|package)\s+', '/x '
        if ($normalized -notmatch '(?i)(^|\s)/(q|qn|quiet|passive)(\s|$)') {
            $normalized = "$normalized /quiet"
        }
        if ($normalized -notmatch '(?i)(^|\s)/norestart(\s|$)') {
            $normalized = "$normalized /norestart"
        }

        return [pscustomobject]@{
            command = $normalized.Trim()
            source = $Source
            summary = 'MSI uninstall command normalized from the new uninstall registry entry.'
        }
    }

    $displayName = [string]$Entry.displayName
    $publisher = [string]$Entry.publisher
    $looksInno = $trimmed -match '(?i)\\unins\d*\.exe\b'
    $looksFoxit = $displayName -match '(?i)foxit' -or $publisher -match '(?i)foxit' -or $trimmed -match '(?i)foxit'
    if (($looksInno -or $looksFoxit) -and
        $trimmed -notmatch '(?i)(^|\s)/(verysilent|silent|quiet|s)(\s|$)') {
        $trimmed = "$trimmed /VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
    }

    return [pscustomobject]@{
        command = $trimmed
        source = $Source
        summary = 'Uninstall command discovered from the new uninstall registry entry.'
    }
}

function Resolve-ProofUninstallCommand {
    param([string]$RequestedCommand, $NewUninstallEntries)
    if (-not (Test-CommandHasPlaceholder -Command $RequestedCommand)) {
        return [pscustomobject]@{
            command = $RequestedCommand
            source = 'Configured uninstall command'
            summary = 'Using the uninstall command already configured before Sandbox Proof.'
        }
    }

    foreach ($entry in @($NewUninstallEntries | Where-Object { -not (Test-UninstallEntryLooksLikeDependency -Entry $_) })) {
        $quiet = Convert-ToSilentUninstallCommand -Entry $entry -Command ([string]$entry.quietUninstallString) -Source "Auto-discovered QuietUninstallString: $($entry.displayName)"
        if ($null -ne $quiet -and -not (Test-CommandHasPlaceholder -Command $quiet.command)) {
            return $quiet
        }

        $regular = Convert-ToSilentUninstallCommand -Entry $entry -Command ([string]$entry.uninstallString) -Source "Auto-discovered UninstallString: $($entry.displayName)"
        if ($null -ne $regular -and -not (Test-CommandHasPlaceholder -Command $regular.command)) {
            return $regular
        }
    }

    return [pscustomobject]@{
        command = ''
        source = 'Auto-discovery failed'
        summary = 'Sandbox Proof could not find a usable uninstall command in new uninstall registry entries.'
    }
}

function Find-ExecutableDetectionTargets {
    param(
        [string]$Root,
        [string]$DisplayName,
        [string]$Publisher,
        [string]$DisplayVersion
    )

    $rootTarget = Split-FileDetectionTarget -Target $Root
    if ($null -eq $rootTarget) { return @() }
    if ($rootTarget.isFile) {
        return @([pscustomobject]@{ target = $rootTarget; rank = 100; depth = 0; fullName = $rootTarget.fullName })
    }

    $folder = Join-Path $rootTarget.path $rootTarget.name
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { return @() }

    $ranked = @()
    foreach ($exe in @(Get-ChildItem -LiteralPath $folder -Filter '*.exe' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 80)) {
        $target = Split-FileDetectionTarget -Target $exe.FullName
        if ($null -eq $target -or -not $target.isFile) { continue }

        $rank = 10
        if (-not [string]::IsNullOrWhiteSpace($target.version)) { $rank += 8 }
        if (-not [string]::IsNullOrWhiteSpace($DisplayVersion) -and -not [string]::IsNullOrWhiteSpace($target.version) -and (Compare-ProofValue -Actual $target.version -Expected $DisplayVersion -Operator 'Equals')) { $rank += 22 }
        if (Test-DetectionTextMatch -First $target.productName -Second $DisplayName) { $rank += 28 }
        if (Test-DetectionTextMatch -First $target.fileDescription -Second $DisplayName) { $rank += 18 }
        if (Test-DetectionTextMatch -First $target.companyName -Second $Publisher) { $rank += 16 }
        if (Test-DetectionTextMatch -First ([IO.Path]::GetFileNameWithoutExtension($target.name)) -Second $DisplayName) { $rank += 14 }
        if ($target.name -match '(?i)(unins|uninstall|setup|installer|update|crash|helper|bootstrap|repair)') { $rank -= 25 }

        $relative = $target.fullName.Substring([Math]::Min($folder.Length, $target.fullName.Length)).TrimStart('\')
        $depth = if ([string]::IsNullOrWhiteSpace($relative)) { 0 } else { ($relative -split '\\').Count }
        $ranked += [pscustomobject]@{
            target = $target
            rank = $rank
            depth = $depth
            fullName = $target.fullName
        }
    }

    return @($ranked |
        Sort-Object -Property @{ Expression = { $_.rank }; Descending = $true }, @{ Expression = { $_.depth }; Ascending = $true }, @{ Expression = { $_.fullName }; Ascending = $true } |
        Select-Object -First 5)
}

function Get-DetectionCandidates {
    param($NewUninstallEntries, $NewProgramDirectories, $NewExecutables, $NewShortcuts, $NewServices, $NewScheduledTasks)
    $candidates = New-Object System.Collections.ArrayList
    $seen = @{}
    $newExecutableMap = @{}
    foreach ($executable in @($NewExecutables)) {
        if (-not [string]::IsNullOrWhiteSpace($executable.fullName)) {
            $executableKey = ([string]$executable.fullName).ToLowerInvariant()
            $newExecutableMap[$executableKey] = $true
        }
    }

    $newProgramDirectoryMap = @{}
    foreach ($directory in @($NewProgramDirectories)) {
        if (-not [string]::IsNullOrWhiteSpace($directory.fullName)) {
            $directoryKey = (([string]$directory.fullName).TrimEnd('\')).ToLowerInvariant()
            $newProgramDirectoryMap[$directoryKey] = $true
        }
    }

    function Add-DetectionCandidate {
        param([string]$Type, [string]$Confidence, [int]$Score, [string]$Reason, $Rule, $AdditionalRules, $Evidence, $NegativePhase)
        if ($null -eq $Rule -or [string]::IsNullOrWhiteSpace([string]$Rule.ruleType)) { return }
        if (-not (Test-DetectionRuleCandidateAllowed -Rule $Rule)) { return }

        $identity = Get-RuleIdentity -Rule $Rule
        if ([string]::IsNullOrWhiteSpace($identity) -or $seen.ContainsKey($identity)) { return }

        $seen[$identity] = $true
        [void]$candidates.Add((New-Candidate -Type $Type -Confidence $Confidence -Score $Score -Reason $Reason -Evidence $Evidence -Rule $Rule -AdditionalRules $AdditionalRules -NegativePhase $NegativePhase))
    }

    foreach ($entry in @($NewUninstallEntries)) {
        if (Test-UninstallEntryLooksLikeDependency -Entry $entry) {
            Write-ProofLog "Skipping dependency uninstall entry as primary app detection: $($entry.displayName) $($entry.displayVersion)"
            continue
        }

        $negative = [pscustomobject]@{
            success = $true
            summary = 'Uninstall registry entry was absent before install and present after install.'
            details = "$($entry.hive)\$($entry.keyPath)"
        }

        if (-not [string]::IsNullOrWhiteSpace($entry.productCode)) {
            Add-DetectionCandidate -Type 'MsiProductCode' -Confidence 'High' -Score 96 -Reason 'New MSI ProductCode registered after install.' -Evidence $entry -Rule (New-MsiDetectionRule -Entry $entry) -AdditionalRules @() -NegativePhase $negative
        }

        if (-not [string]::IsNullOrWhiteSpace($entry.displayName) -and -not [string]::IsNullOrWhiteSpace($entry.displayVersion)) {
            Add-DetectionCandidate -Type 'Registry' -Confidence 'High' -Score 90 -Reason 'New uninstall entry with DisplayVersion after install.' -Evidence $entry -Rule (New-RegistryDetectionRule -Entry $entry -ValueName 'DisplayVersion' -Operator 'GreaterThanOrEqual' -Value ([string]$entry.displayVersion)) -AdditionalRules (New-RegistryIdentityAdditionalRules -Entry $entry -PrimaryValueName 'DisplayVersion') -NegativePhase $negative
        }
        elseif (-not [string]::IsNullOrWhiteSpace($entry.displayName)) {
            Add-DetectionCandidate -Type 'Registry' -Confidence 'Medium' -Score 72 -Reason 'New uninstall entry with DisplayName after install.' -Evidence $entry -Rule (New-RegistryDetectionRule -Entry $entry -ValueName 'DisplayName' -Operator 'Equals' -Value ([string]$entry.displayName)) -AdditionalRules (New-RegistryIdentityAdditionalRules -Entry $entry -PrimaryValueName 'DisplayName') -NegativePhase $negative
        }

        $targets = @(
            @{ Value = $entry.displayIcon; Source = 'DisplayIcon'; Score = 88; Confidence = 'High'; Reason = 'New uninstall entry DisplayIcon points to an installed executable.' },
            @{ Value = $entry.installLocation; Source = 'InstallLocation'; Score = 84; Confidence = 'High'; Reason = 'New uninstall entry InstallLocation contains an executable install footprint.' },
            @{ Value = $entry.uninstallString; Source = 'UninstallString'; Score = 68; Confidence = 'Medium'; Reason = 'New uninstall entry UninstallString points to an installed executable.' },
            @{ Value = $entry.quietUninstallString; Source = 'QuietUninstallString'; Score = 66; Confidence = 'Medium'; Reason = 'New uninstall entry QuietUninstallString points to an installed executable.' }
        )
        foreach ($targetValue in $targets) {
            $candidatePath = Resolve-PathCandidate -Value ([string]$targetValue.Value)
            $fileTarget = Split-FileDetectionTarget -Target $candidatePath
            if ($null -eq $fileTarget) { continue }

            if ($fileTarget.isFile) {
                $fileNegative = New-FileCandidateNegativePhase -FileTarget $fileTarget -NewExecutableMap $newExecutableMap -NewProgramDirectoryMap $newProgramDirectoryMap -FallbackSummary $negative.summary -FallbackDetails $negative.details
                Add-DetectionCandidate -Type 'File' -Confidence ([string]$targetValue.Confidence) -Score ([int]$targetValue.Score) -Reason ([string]$targetValue.Reason) -Evidence $entry -Rule (New-FileDetectionRule -FileTarget $fileTarget) -AdditionalRules (New-RegistryIdentityAdditionalRules -Entry $entry -PrimaryValueName '') -NegativePhase $fileNegative
                continue
            }

            foreach ($executable in @(Find-ExecutableDetectionTargets -Root $fileTarget.fullName -DisplayName ([string]$entry.displayName) -Publisher ([string]$entry.publisher) -DisplayVersion ([string]$entry.displayVersion) | Select-Object -First 3)) {
                $score = [Math]::Min(92, ([int]$targetValue.Score + [Math]::Max(0, [int]($executable.rank / 10))))
                $fileNegative = New-FileCandidateNegativePhase -FileTarget $executable.target -NewExecutableMap $newExecutableMap -NewProgramDirectoryMap $newProgramDirectoryMap -FallbackSummary $negative.summary -FallbackDetails $negative.details
                Add-DetectionCandidate -Type 'File' -Confidence ([string]$targetValue.Confidence) -Score $score -Reason ([string]$targetValue.Reason) -Evidence $entry -Rule (New-FileDetectionRule -FileTarget $executable.target) -AdditionalRules (New-RegistryIdentityAdditionalRules -Entry $entry -PrimaryValueName '') -NegativePhase $fileNegative
            }

            $folderNegative = New-FileCandidateNegativePhase -FileTarget $fileTarget -NewExecutableMap $newExecutableMap -NewProgramDirectoryMap $newProgramDirectoryMap -FallbackSummary $negative.summary -FallbackDetails $negative.details
            Add-DetectionCandidate -Type 'File' -Confidence 'Low' -Score 46 -Reason 'New uninstall entry InstallLocation folder exists; executable target was not stronger than folder detection.' -Evidence $entry -Rule (New-FileDetectionRule -FileTarget $fileTarget) -AdditionalRules (New-RegistryIdentityAdditionalRules -Entry $entry -PrimaryValueName '') -NegativePhase $folderNegative
        }
    }

    foreach ($directory in @($NewProgramDirectories | Select-Object -First 10)) {
        $negative = [pscustomobject]@{
            success = $true
            summary = 'Install directory was absent before install and present after install.'
            details = $directory.fullName
        }

        foreach ($executable in @(Find-ExecutableDetectionTargets -Root ([string]$directory.fullName) -DisplayName ([string]$directory.name) -Publisher '' -DisplayVersion '' | Select-Object -First 3)) {
            $score = [Math]::Min(86, 78 + [Math]::Max(0, [int]($executable.rank / 12)))
            $fileNegative = New-FileCandidateNegativePhase -FileTarget $executable.target -NewExecutableMap $newExecutableMap -NewProgramDirectoryMap $newProgramDirectoryMap -FallbackSummary $negative.summary -FallbackDetails $negative.details
            Add-DetectionCandidate -Type 'File' -Confidence 'Medium' -Score $score -Reason 'New install directory contains an executable footprint.' -Evidence $directory -Rule (New-FileDetectionRule -FileTarget $executable.target) -AdditionalRules @() -NegativePhase $fileNegative
        }

        $folderTarget = Split-FileDetectionTarget -Target ([string]$directory.fullName)
        if ($null -ne $folderTarget) {
            $folderNegative = New-FileCandidateNegativePhase -FileTarget $folderTarget -NewExecutableMap $newExecutableMap -NewProgramDirectoryMap $newProgramDirectoryMap -FallbackSummary $negative.summary -FallbackDetails $negative.details
            Add-DetectionCandidate -Type 'File' -Confidence 'Low' -Score 45 -Reason 'New top-level install directory appeared after install.' -Evidence $directory -Rule (New-FileDetectionRule -FileTarget $folderTarget) -AdditionalRules @() -NegativePhase $folderNegative
        }
    }

    foreach ($shortcut in @($NewShortcuts)) {
        $candidatePath = Resolve-PathCandidate -Value ([string]$shortcut.targetPath)
        $fileTarget = Split-FileDetectionTarget -Target $candidatePath
        if ($null -eq $fileTarget -or -not $fileTarget.isFile) { continue }

        $negative = [pscustomobject]@{
            success = $true
            summary = 'Shortcut was absent before install and present after install.'
            details = $shortcut.fullName
        }
        $fileNegative = New-FileCandidateNegativePhase -FileTarget $fileTarget -NewExecutableMap $newExecutableMap -NewProgramDirectoryMap $newProgramDirectoryMap -FallbackSummary $negative.summary -FallbackDetails $negative.details
        Add-DetectionCandidate -Type 'File' -Confidence 'High' -Score 94 -Reason 'New shortcut target points to the installed application executable.' -Evidence $shortcut -Rule (New-FileDetectionRule -FileTarget $fileTarget) -AdditionalRules @() -NegativePhase $fileNegative
    }

    foreach ($service in @($NewServices)) {
        $candidatePath = Resolve-PathCandidate -Value ([string]$service.pathName)
        $fileTarget = Split-FileDetectionTarget -Target $candidatePath
        if ($null -eq $fileTarget -or -not $fileTarget.isFile) { continue }

        $negative = [pscustomobject]@{
            success = $true
            summary = 'Service was absent before install and present after install.'
            details = $service.name
        }
        $fileNegative = New-FileCandidateNegativePhase -FileTarget $fileTarget -NewExecutableMap $newExecutableMap -NewProgramDirectoryMap $newProgramDirectoryMap -FallbackSummary $negative.summary -FallbackDetails $negative.details
        Add-DetectionCandidate -Type 'File' -Confidence 'High' -Score 86 -Reason 'New Windows service points to an installed executable.' -Evidence $service -Rule (New-FileDetectionRule -FileTarget $fileTarget) -AdditionalRules @() -NegativePhase $fileNegative
    }

    foreach ($task in @($NewScheduledTasks)) {
        foreach ($action in @($task.actions)) {
            $candidatePath = Resolve-PathCandidate -Value ("$($action.execute) $($action.arguments)")
            $fileTarget = Split-FileDetectionTarget -Target $candidatePath
            if ($null -eq $fileTarget -or -not $fileTarget.isFile) { continue }

            $negative = [pscustomobject]@{
                success = $true
                summary = 'Scheduled task was absent before install and present after install.'
                details = "$($task.taskPath)$($task.taskName)"
            }
            $fileNegative = New-FileCandidateNegativePhase -FileTarget $fileTarget -NewExecutableMap $newExecutableMap -NewProgramDirectoryMap $newProgramDirectoryMap -FallbackSummary $negative.summary -FallbackDetails $negative.details
            Add-DetectionCandidate -Type 'File' -Confidence 'Medium' -Score 82 -Reason 'New scheduled task action points to an installed executable.' -Evidence $task -Rule (New-FileDetectionRule -FileTarget $fileTarget) -AdditionalRules @() -NegativePhase $fileNegative
        }
    }

    return @($candidates)
}

function Get-LaunchTargets {
    param($NewUninstallEntries, $NewShortcuts, $NewExecutables, $NewProgramDirectories)
    $targets = New-Object System.Collections.ArrayList
    $seen = @{}

    function Add-LaunchTarget {
        param([string]$Path, [string]$Arguments, [string]$WorkingDirectory, [string]$Source, [int]$Score)
        $candidatePath = Resolve-PathCandidate -Value $Path
        if ([string]::IsNullOrWhiteSpace($candidatePath) -or -not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) { return }
        if ([IO.Path]::GetExtension($candidatePath) -ine '.exe') { return }
        $name = [IO.Path]::GetFileName($candidatePath)
        if ($name -match '(?i)(unins|uninstall|setup|installer|update|crash|helper|bootstrap|repair|vcredist|vc_redist)') { return }
        if ([string]::IsNullOrWhiteSpace($WorkingDirectory) -or -not (Test-Path -LiteralPath $WorkingDirectory -PathType Container)) {
            try { $WorkingDirectory = [IO.Path]::GetDirectoryName($candidatePath) } catch { $WorkingDirectory = '' }
        }

        $key = $candidatePath.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { return }
        $seen[$key] = $true
        [void]$targets.Add([pscustomobject]@{
            path = $candidatePath
            arguments = [string]$Arguments
            workingDirectory = [string]$WorkingDirectory
            source = [string]$Source
            score = $Score
        })
    }

    foreach ($shortcut in @($NewShortcuts)) {
        Add-LaunchTarget -Path ([string]$shortcut.targetPath) -Arguments ([string]$shortcut.arguments) -WorkingDirectory ([string]$shortcut.workingDirectory) -Source "Shortcut: $($shortcut.fullName)" -Score 100
    }

    foreach ($entry in @($NewUninstallEntries)) {
        if (Test-UninstallEntryLooksLikeDependency -Entry $entry) { continue }
        foreach ($value in @([string]$entry.displayIcon, [string]$entry.installLocation)) {
            if ([string]::IsNullOrWhiteSpace($value)) { continue }
            $candidatePath = Resolve-PathCandidate -Value $value
            if ([string]::IsNullOrWhiteSpace($candidatePath) -and (Test-Path -LiteralPath $value -PathType Container -ErrorAction SilentlyContinue)) {
                foreach ($exe in @(Find-ExecutableDetectionTargets -Root $value -DisplayName ([string]$entry.displayName) -Publisher ([string]$entry.publisher) -DisplayVersion ([string]$entry.displayVersion) | Select-Object -First 2)) {
                    Add-LaunchTarget -Path ([string]$exe.fullName) -Arguments '' -WorkingDirectory '' -Source "Uninstall entry: $($entry.displayName)" -Score 92
                }
                continue
            }

            Add-LaunchTarget -Path $candidatePath -Arguments '' -WorkingDirectory '' -Source "Uninstall entry: $($entry.displayName)" -Score 90
        }
    }

    foreach ($exe in @($NewExecutables)) {
        Add-LaunchTarget -Path ([string]$exe.fullName) -Arguments '' -WorkingDirectory '' -Source 'New executable' -Score 75
    }

    foreach ($directory in @($NewProgramDirectories | Select-Object -First 10)) {
        foreach ($exe in @(Find-ExecutableDetectionTargets -Root ([string]$directory.fullName) -DisplayName ([string]$directory.name) -Publisher '' -DisplayVersion '' | Select-Object -First 2)) {
            Add-LaunchTarget -Path ([string]$exe.fullName) -Arguments '' -WorkingDirectory '' -Source "Install directory: $($directory.fullName)" -Score 70
        }
    }

    return @($targets | Sort-Object -Property @{ Expression = { $_.score }; Descending = $true }, @{ Expression = { $_.path }; Ascending = $true })
}

function Get-WindowRect {
    param([IntPtr]$Handle)
    if ($Handle -eq [IntPtr]::Zero) { return $null }
    if (-not ('IwpNativeWindow' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class IwpNativeWindow {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
"@
    }

    $rect = New-Object IwpNativeWindow+RECT
    if (-not [IwpNativeWindow]::GetWindowRect($Handle, [ref]$rect)) { return $null }
    $width = [Math]::Max(0, $rect.Right - $rect.Left)
    $height = [Math]::Max(0, $rect.Bottom - $rect.Top)
    if ($width -lt 120 -or $height -lt 120) { return $null }
    return [pscustomobject]@{ left = $rect.Left; top = $rect.Top; width = $width; height = $height }
}

function Measure-WhiteWindowRatio {
    param([string]$ImagePath)
    if ([string]::IsNullOrWhiteSpace($ImagePath) -or -not (Test-Path -LiteralPath $ImagePath)) { return 0.0 }
    Add-Type -AssemblyName System.Drawing
    $bitmap = $null
    try {
        $bitmap = [System.Drawing.Bitmap]::FromFile($ImagePath)
        $sample = 0
        $blank = 0
        $stepX = [Math]::Max(1, [int]($bitmap.Width / 80))
        $stepY = [Math]::Max(1, [int]($bitmap.Height / 60))
        $startY = [Math]::Min($bitmap.Height - 1, [int]($bitmap.Height * 0.08))
        for ($y = $startY; $y -lt $bitmap.Height; $y += $stepY) {
            for ($x = 4; $x -lt ($bitmap.Width - 4); $x += $stepX) {
                $pixel = $bitmap.GetPixel($x, $y)
                $sample += 1
                $brightness = ($pixel.R + $pixel.G + $pixel.B) / 3
                $spread = ([Math]::Max($pixel.R, [Math]::Max($pixel.G, $pixel.B)) - [Math]::Min($pixel.R, [Math]::Min($pixel.G, $pixel.B)))
                if (($pixel.R -ge 245 -and $pixel.G -ge 245 -and $pixel.B -ge 245) -or
                    ($brightness -ge 185 -and $spread -le 70)) {
                    $blank += 1
                }
            }
        }

        if ($sample -eq 0) { return 0.0 }
        return [Math]::Round(($blank / $sample), 4)
    }
    finally {
        if ($null -ne $bitmap) { $bitmap.Dispose() }
    }
}

function Save-WindowScreenshot {
    param($Rect, [string]$Path)
    if ($null -eq $Rect) { return $false }
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Windows.Forms
    $bitmap = $null
    $graphics = $null
    try {
        $bitmap = New-Object System.Drawing.Bitmap ([int]$Rect.width), ([int]$Rect.height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen([int]$Rect.left, [int]$Rect.top, 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        return $true
    }
    catch {
        Write-ProofLog "Window screenshot failed: $($_.Exception.Message)"
        return $false
    }
    finally {
        if ($null -ne $graphics) { $graphics.Dispose() }
        if ($null -ne $bitmap) { $bitmap.Dispose() }
    }
}

function Invoke-WeintekSoftwareRenderWorkaround {
    param($NewExecutables, $NewProgramDirectories)

    $displaySettingPaths = New-Object System.Collections.ArrayList
    foreach ($exe in @($NewExecutables)) {
        if ([string]$exe.name -ieq 'DisplaySetting.exe') {
            [void]$displaySettingPaths.Add([string]$exe.fullName)
        }
    }

    foreach ($directory in @($NewProgramDirectories)) {
        $path = Join-Path ([string]$directory.fullName) 'DisplaySetting.exe'
        if (Test-Path -LiteralPath $path -PathType Leaf -ErrorAction SilentlyContinue) {
            [void]$displaySettingPaths.Add($path)
        }
    }

    $displaySettingPath = @($displaySettingPaths |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf -ErrorAction SilentlyContinue) } |
        Select-Object -Unique |
        Where-Object { $_ -match '(?i)(weintek|cmtviewer|easybuilder|easyaccess|cloudhmi)' } |
        Select-Object -First 1)

    if ($displaySettingPath.Count -eq 0) {
        return [pscustomobject]@{
            attempted = $false
            success = $true
            summary = 'No Weintek DisplaySetting.exe runtime workaround was required.'
            displaySettingPath = ''
            method = ''
        }
    }

    $path = [string]$displaySettingPath[0]
    Write-ProofLog "Applying Weintek Software render workaround with: $path"
    $process = $null
    try {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        Add-Type -AssemblyName System.Windows.Forms

        $process = Start-Process -FilePath $path -WorkingDirectory ([IO.Path]::GetDirectoryName($path)) -PassThru -WindowStyle Normal
        $handle = [IntPtr]::Zero
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 300
            try { $process.Refresh() } catch {}
            if ($process.HasExited) { break }
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                $handle = $process.MainWindowHandle
                break
            }
        }

        if ($handle -eq [IntPtr]::Zero) {
            return [pscustomobject]@{
                attempted = $true
                success = $false
                summary = 'DisplaySetting.exe did not open a usable settings window.'
                displaySettingPath = $path
                method = 'UIAutomation'
            }
        }

        $window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        $radioCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $radioButtons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $radioCondition)
        $softwareRadio = $null
        foreach ($radio in $radioButtons) {
            $name = [string]$radio.Current.Name
            if ($name -match '(?i)software\s*render') {
                $softwareRadio = $radio
                break
            }
        }

        if ($null -eq $softwareRadio) {
            return [pscustomobject]@{
                attempted = $true
                success = $false
                summary = 'DisplaySetting.exe opened, but the Software render option was not found.'
                displaySettingPath = $path
                method = 'UIAutomation'
            }
        }

        $selectionPattern = $null
        if ($softwareRadio.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectionPattern)) {
            $selectionPattern.Select()
        } else {
            $invokePattern = $null
            if ($softwareRadio.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invokePattern)) {
                $invokePattern.Invoke()
            } else {
                $softwareRadio.SetFocus()
                [System.Windows.Forms.SendKeys]::SendWait(' ')
            }
        }

        Start-Sleep -Milliseconds 300

        $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
        $okButton = $null
        foreach ($button in $buttons) {
            $name = [string]$button.Current.Name
            if ($name -match '^(?i:OK|Ok|Oke|Apply|Toepassen)$') {
                $okButton = $button
                break
            }
        }

        if ($null -ne $okButton) {
            $okPattern = $null
            if ($okButton.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$okPattern)) {
                $okPattern.Invoke()
            } else {
                $okButton.SetFocus()
                [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            }
        } else {
            $window.SetFocus()
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        }

        try { $process.WaitForExit(5000) | Out-Null } catch {}
        if (-not $process.HasExited) {
            try { $process.CloseMainWindow() | Out-Null } catch {}
        }

        Write-ProofLog 'Weintek Software render workaround applied.'
        return [pscustomobject]@{
            attempted = $true
            success = $true
            summary = 'Weintek DisplaySetting.exe was set to Software render before launch validation.'
            displaySettingPath = $path
            method = 'UIAutomation'
        }
    }
    catch {
        Write-ProofLog "Weintek Software render workaround failed: $($_.Exception.Message)"
        return [pscustomobject]@{
            attempted = $true
            success = $false
            summary = "Weintek Software render workaround failed: $($_.Exception.Message)"
            displaySettingPath = $path
            method = 'UIAutomation'
        }
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) {
            try { $process.CloseMainWindow() | Out-Null } catch {}
        }
    }
}

function Invoke-LaunchValidation {
    param($NewUninstallEntries, $NewShortcuts, $NewExecutables, $NewProgramDirectories)

    $targets = @(Get-LaunchTargets -NewUninstallEntries $NewUninstallEntries -NewShortcuts $NewShortcuts -NewExecutables $NewExecutables -NewProgramDirectories $NewProgramDirectories)
    if ($targets.Count -eq 0) {
        return [pscustomobject]@{
            success = $true
            summary = 'No launchable installed application target was found; launch validation was skipped.'
            target = $null
            processId = 0
            hasWindow = $false
            whiteRatio = 0.0
            screenshotPath = ''
            candidates = $targets
        }
    }

    $target = $targets | Select-Object -First 1
    Write-ProofLog "Launch validation target: $($target.path) [$($target.source)]"
    $process = $null
    $screenshotPath = Join-Path $LogsPath 'launch-window.png'
    try {
        $startProcessParameters = @{
            FilePath = [string]$target.path
            WorkingDirectory = [string]$target.workingDirectory
            PassThru = $true
            WindowStyle = 'Normal'
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$target.arguments)) {
            $startProcessParameters.ArgumentList = [string]$target.arguments
        }

        $process = Start-Process @startProcessParameters
        Start-Sleep -Seconds 15
        try { $process.Refresh() } catch {}

        $handle = $process.MainWindowHandle
        if ($handle -eq [IntPtr]::Zero) {
            foreach ($child in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.Path -ieq [string]$target.path } | Select-Object -First 1)) {
                $handle = $child.MainWindowHandle
                $process = $child
            }
        }

        $rect = Get-WindowRect -Handle $handle
        $hasWindow = $null -ne $rect
        $whiteRatio = 0.0
        if ($hasWindow -and (Save-WindowScreenshot -Rect $rect -Path $screenshotPath)) {
            $whiteRatio = Measure-WhiteWindowRatio -ImagePath $screenshotPath
        }

        $success = $hasWindow -and $whiteRatio -lt 0.82
        $summary = if (-not $hasWindow) {
            'Installed application launched but no usable window was detected.'
        } elseif ($whiteRatio -ge 0.82) {
            "Installed application launched to a mostly blank/light window (blank ratio $whiteRatio)."
        } else {
            "Installed application launched with a non-blank window (blank ratio $whiteRatio)."
        }

        return [pscustomobject]@{
            success = $success
            summary = $summary
            target = $target
            processId = if ($null -ne $process) { $process.Id } else { 0 }
            hasWindow = $hasWindow
            whiteRatio = $whiteRatio
            screenshotPath = if (Test-Path -LiteralPath $screenshotPath) { $screenshotPath } else { '' }
            candidates = $targets
        }
    }
    catch {
        return [pscustomobject]@{
            success = $false
            summary = "Launch validation failed: $($_.Exception.Message)"
            target = $target
            processId = if ($null -ne $process) { $process.Id } else { 0 }
            hasWindow = $false
            whiteRatio = 0.0
            screenshotPath = if (Test-Path -LiteralPath $screenshotPath) { $screenshotPath } else { '' }
            candidates = $targets
        }
    }
    finally {
        if ($null -ne $process) {
            try { $process.Refresh() } catch {}
            if (-not $process.HasExited) {
                try { [void]$process.CloseMainWindow(); Start-Sleep -Seconds 2 } catch {}
                try { $process.Refresh() } catch {}
                if (-not $process.HasExited) {
                    try { $process.Kill($true) } catch { try { $process.Kill() } catch {} }
                }
            }
        }
    }
}

function Write-Report {
    param($Result)
    $lines = @()
    $lines += 'Intune Win Packager - Windows Sandbox Proof'
    $lines += '================================================'
    $lines += ''
    $lines += "Installer type: $($Result.request.installerType)"
    $lines += "Setup path: $($Result.request.sandboxSetupFilePath)"
    $lines += ''
    $lines += 'Pre-check'
    $lines += "Detection rule supplied before proof: $($Result.precheck.detectionRuleAvailable)"
    $lines += "Additional detection rules supplied: $($Result.precheck.additionalDetectionRuleCount)"
    $lines += "Pre-check summary: $($Result.precheck.summary)"
    $lines += ''
    $lines += 'Install proof'
    $lines += "Install command: $($Result.request.installCommand)"
    if (-not [string]::IsNullOrWhiteSpace([string]$Result.install.executionMode)) {
        $lines += "Install execution mode: $($Result.install.executionMode)"
    }
    $lines += "Install exit code: $($Result.install.exitCode)"
    $lines += "Install timed out: $($Result.install.timedOut)"
    $installExitCode = [int]$Result.install.exitCode
    $installTimedOut = [bool]$Result.install.timedOut
    if ($installTimedOut) {
        $lines += 'Install verdict: Failed - command timed out before completing unattended.'
    }
    elseif ($installExitCode -in @(0, 3010, 1641)) {
        $lines += 'Install verdict: Completed - exit code is accepted for proof.'
    }
    else {
        $exitExplanation = switch ($installExitCode) {
            1602 { 'cancelled by installer/user or blocked by a required prompt; check silent switches' }
            1618 { 'another installation is already in progress' }
            1623 { 'system not supported' }
            1625 { 'blocked by policy' }
            1628 { 'invalid command-line parameters' }
            1633 { 'system not supported' }
            1638 { 'another version is already installed' }
            1639 { 'invalid command-line parameters' }
            1640 { 'blocked by policy' }
            1643 { 'blocked by policy' }
            1644 { 'blocked by policy' }
            1649 { 'blocked by policy' }
            1650 { 'invalid command-line parameters' }
            1654 { 'system not supported' }
            default { 'installer returned a failure code' }
        }
        $lines += "Install verdict: Failed - $exitExplanation."
    }
    $lines += ''
    $lines += 'Detection proof'
    $lines += "Pre-install detection: $($Result.preInstallDetection.success) - $($Result.preInstallDetection.summary)"
    $lines += "Post-install detection: $($Result.postInstallDetection.success) - $($Result.postInstallDetection.summary)"
    $lines += "Post-uninstall detection: $($Result.postUninstallDetection.success) - $($Result.postUninstallDetection.summary)"
    $lines += ''
    if ($null -ne $Result.launchValidation) {
        $lines += 'Launch proof'
        $lines += "Launch validation: $($Result.launchValidation.success) - $($Result.launchValidation.summary)"
        if ($null -ne $Result.launchValidation.target) {
            $lines += "Launch target: $($Result.launchValidation.target.path)"
            $lines += "Launch source: $($Result.launchValidation.target.source)"
        }
        if ($null -ne $Result.launchRemediation -and [bool]$Result.launchRemediation.attempted) {
            $lines += "Launch remediation: $($Result.launchRemediation.success) - $($Result.launchRemediation.summary)"
            $lines += "Launch remediation tool: $($Result.launchRemediation.displaySettingPath)"
        }
        $lines += "Launch window detected: $($Result.launchValidation.hasWindow)"
        $lines += "Launch white ratio: $($Result.launchValidation.whiteRatio)"
        if (-not [string]::IsNullOrWhiteSpace($Result.launchValidation.screenshotPath)) {
            $lines += "Launch screenshot: $($Result.launchValidation.screenshotPath)"
        }
        $lines += ''
    }
    $lines += 'Uninstall proof'
    $lines += "Uninstall command: $($Result.uninstall.command)"
    if (-not [string]::IsNullOrWhiteSpace([string]$Result.uninstall.executionMode)) {
        $lines += "Uninstall execution mode: $($Result.uninstall.executionMode)"
    }
    if ($null -ne $Result.uninstallResolution) {
        $lines += "Uninstall command source: $($Result.uninstallResolution.source)"
        $lines += "Uninstall command resolution: $($Result.uninstallResolution.summary)"
    }
    $lines += "Uninstall exit code: $($Result.uninstall.exitCode)"
    $lines += "Uninstall timed out: $($Result.uninstall.timedOut)"
    if ([bool]$Result.uninstallValidation.success) {
        $lines += "Uninstall verdict: Completed - $($Result.uninstallValidation.summary)"
    } else {
        $lines += "Uninstall verdict: Failed - $($Result.uninstallValidation.summary)"
    }
    $lines += "Remaining uninstall entries after uninstall: $(@($Result.uninstallValidation.remainingUninstallEntries).Count)"
    $lines += "Remaining install directories after uninstall: $(@($Result.uninstallValidation.remainingProgramDirectories).Count)"
    $lines += "Remaining executables after uninstall: $(@($Result.uninstallValidation.remainingExecutables).Count)"
    $lines += "Remaining shortcuts after uninstall: $(@($Result.uninstallValidation.remainingShortcuts).Count)"
    $lines += ''
    $lines += "New uninstall entries: $(@($Result.diff.newUninstallEntries).Count)"
    foreach ($entry in @($Result.diff.newUninstallEntries | Select-Object -First 12)) {
        $lines += "- $($entry.displayName) $($entry.displayVersion) [$($entry.hive)\$($entry.keyPath)]"
    }
    $lines += ''
    $lines += "New install directories: $(@($Result.diff.newProgramDirectories).Count)"
    foreach ($directory in @($Result.diff.newProgramDirectories | Select-Object -First 12)) {
        $lines += "- $($directory.fullName)"
    }
    $lines += ''
    $lines += "New executables: $(@($Result.diff.newExecutables).Count)"
    foreach ($executable in @($Result.diff.newExecutables | Select-Object -First 20)) {
        $versionLabel = if ([string]::IsNullOrWhiteSpace($executable.version)) { '' } else { " [$($executable.version)]" }
        $lines += "- $($executable.fullName)$versionLabel"
    }
    $lines += ''
    $lines += "New shortcuts: $(@($Result.diff.newShortcuts).Count)"
    foreach ($shortcut in @($Result.diff.newShortcuts | Select-Object -First 12)) {
        $lines += "- $($shortcut.fullName) -> $($shortcut.targetPath)"
    }
    $lines += ''
    $lines += "New services: $(@($Result.diff.newServices).Count)"
    foreach ($service in @($Result.diff.newServices | Select-Object -First 12)) {
        $lines += "- $($service.name): $($service.pathName)"
    }
    $lines += ''
    $lines += "New scheduled tasks: $(@($Result.diff.newScheduledTasks).Count)"
    foreach ($task in @($Result.diff.newScheduledTasks | Select-Object -First 12)) {
        $lines += "- $($task.taskPath)$($task.taskName)"
    }
    $lines += ''
    $lines += "Detection candidates: $(@($Result.candidates).Count)"
    $lines += "Proven candidates: $(@($Result.candidates | Where-Object { $_.proof.success }).Count)"
    foreach ($candidate in @($Result.candidates | Select-Object -First 12)) {
        $proofLabel = if ($candidate.proof.success) { 'PROVEN' } else { 'UNPROVEN' }
        $lines += "- [$($candidate.confidence), score $($candidate.score)] [$proofLabel] $($candidate.type): $($candidate.reason)"
        if (@($candidate.additionalRules).Count -gt 0) {
            $lines += "  Additional rules: $(@($candidate.additionalRules).Count)"
        }
        $lines += "  Proof: $($candidate.proof.summary)"
        if ($null -ne $candidate.proof.uninstallPhase) {
            $lines += "  Uninstall proof: $($candidate.proof.uninstallPhase.summary)"
        }
        $lines += "  Rule: $($candidate.rule | ConvertTo-Json -Compress -Depth 8)"
    }
    $lines += ''
    $lines += 'Full machine-readable evidence is available in result.json.'
    Set-Content -LiteralPath $ReportPath -Value $lines -Encoding UTF8
}

try {
    Start-Transcript -LiteralPath $TranscriptPath -Force | Out-Null
} catch {}

try {
    Write-ProofLog 'Starting sandbox proof run.'
    $inputData = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json

    $baseline = Get-Snapshot -Name 'baseline'
    $preDetection = Test-ProofDetection -Rule $inputData.detectionRule

    $installResult = Invoke-ProofCommand -Command ([string]$inputData.installCommand) -WorkingDirectory ([string]$inputData.sandboxWorkingDirectory) -TimeoutMinutes ([int]$inputData.timeoutMinutes) -Phase 'install'
    Start-Sleep -Seconds 3

    $postInstall = Get-Snapshot -Name 'post-install'
    $postDetection = Test-ProofDetection -Rule $inputData.detectionRule

    $diff = [pscustomobject]@{
        newUninstallEntries = @(Compare-ById -Before $baseline.uninstallEntries -After $postInstall.uninstallEntries)
        newProgramDirectories = @(Compare-ById -Before $baseline.programDirectories -After $postInstall.programDirectories)
        newExecutables = @(Compare-ById -Before $baseline.executables -After $postInstall.executables)
        newServices = @(Compare-ById -Before $baseline.services -After $postInstall.services)
        newScheduledTasks = @(Compare-ById -Before $baseline.scheduledTasks -After $postInstall.scheduledTasks)
        newShortcuts = @(Compare-ById -Before $baseline.shortcuts -After $postInstall.shortcuts)
    }

    $candidates = @(Get-DetectionCandidates -NewUninstallEntries $diff.newUninstallEntries -NewProgramDirectories $diff.newProgramDirectories -NewExecutables $diff.newExecutables -NewShortcuts $diff.newShortcuts -NewServices $diff.newServices -NewScheduledTasks $diff.newScheduledTasks)
    $configuredCandidate = New-ConfiguredDetectionCandidate -Rule $inputData.detectionRule -PreDetection $preDetection -ExistingCandidates $candidates
    if ($null -ne $configuredCandidate) {
        $candidates += $configuredCandidate
    }
    $candidates = @($candidates | ForEach-Object { Complete-CandidateProof -Candidate $_ })
    $launchRemediation = Invoke-WeintekSoftwareRenderWorkaround -NewExecutables $diff.newExecutables -NewProgramDirectories $diff.newProgramDirectories
    $launchValidation = Invoke-LaunchValidation -NewUninstallEntries $diff.newUninstallEntries -NewShortcuts $diff.newShortcuts -NewExecutables $diff.newExecutables -NewProgramDirectories $diff.newProgramDirectories
    $uninstallResolution = Resolve-ProofUninstallCommand -RequestedCommand ([string]$inputData.uninstallCommand) -NewUninstallEntries $diff.newUninstallEntries
    Write-ProofLog "Resolved uninstall command source: $($uninstallResolution.source)"
    Write-ProofLog "Resolved uninstall command: $($uninstallResolution.command)"
    $uninstallResult = Invoke-ProofCommand -Command ([string]$uninstallResolution.command) -WorkingDirectory ([string]$inputData.sandboxWorkingDirectory) -TimeoutMinutes ([int]$inputData.timeoutMinutes) -Phase 'uninstall'
    Start-Sleep -Seconds 3
    $postUninstall = Get-Snapshot -Name 'post-uninstall'
    $postUninstallDetection = Test-ProofDetection -Rule $inputData.detectionRule
    $candidates = @($candidates | ForEach-Object { Complete-CandidateUninstallProof -Candidate $_ })

    $uninstallDiff = [pscustomobject]@{
        remainingUninstallEntries = @(Compare-ById -Before $baseline.uninstallEntries -After $postUninstall.uninstallEntries)
        remainingProgramDirectories = @(Compare-ById -Before $baseline.programDirectories -After $postUninstall.programDirectories)
        remainingExecutables = @(Compare-ById -Before $baseline.executables -After $postUninstall.executables)
        remainingServices = @(Compare-ById -Before $baseline.services -After $postUninstall.services)
        remainingScheduledTasks = @(Compare-ById -Before $baseline.scheduledTasks -After $postUninstall.scheduledTasks)
        remainingShortcuts = @(Compare-ById -Before $baseline.shortcuts -After $postUninstall.shortcuts)
    }

    $installAccepted = Test-ProofCommandSucceeded -Result $installResult
    $uninstallAccepted = Test-ProofCommandSucceeded -Result $uninstallResult
    $provenCandidates = @($candidates | Where-Object { $_.proof.success })
    $installDetectionCandidates = @($candidates | Where-Object { $_.proof.positivePhase.success })
    $uninstallValidation = [pscustomobject]@{
        success = $uninstallAccepted -and ($candidates.Count -eq 0 -or $provenCandidates.Count -gt 0)
        summary = if (-not $uninstallAccepted) {
            "Uninstall command exited with $($uninstallResult.exitCode)."
        } elseif ($candidates.Count -gt 0 -and $provenCandidates.Count -eq 0) {
            "Uninstall command completed, but no detection candidate cleared after uninstall."
        } elseif ($installDetectionCandidates.Count -gt 0) {
            "Uninstall command completed and $($provenCandidates.Count) detection candidate(s) cleared after uninstall."
        } else {
            'Uninstall command completed; no detection candidates were available to validate.'
        }
        remainingUninstallEntries = $uninstallDiff.remainingUninstallEntries
        remainingProgramDirectories = $uninstallDiff.remainingProgramDirectories
        remainingExecutables = $uninstallDiff.remainingExecutables
        remainingServices = $uninstallDiff.remainingServices
        remainingScheduledTasks = $uninstallDiff.remainingScheduledTasks
        remainingShortcuts = $uninstallDiff.remainingShortcuts
    }

    $failureKind = if (-not $installAccepted) {
        'Install'
    } elseif (-not [bool]$uninstallValidation.success) {
        'Uninstall'
    } elseif ($candidates.Count -eq 0 -or $provenCandidates.Count -eq 0) {
        'Detection'
    } elseif (-not [bool]$launchValidation.success) {
        'LaunchValidation'
    } else {
        ''
    }

    $failureMessage = switch ($failureKind) {
        'Install' { "Install command failed with exit code $($installResult.exitCode)." }
        'Uninstall' { [string]$uninstallValidation.summary }
        'Detection' { 'No detection candidate passed install and uninstall validation.' }
        'LaunchValidation' { [string]$launchValidation.summary }
        default { '' }
    }

    $blockingFailureKind = if ($failureKind -eq 'LaunchValidation') { '' } else { $failureKind }

    $result = [pscustomobject]@{
        schemaVersion = 2
        completedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        failed = -not [string]::IsNullOrWhiteSpace($blockingFailureKind)
        failureKind = $failureKind
        error = $failureMessage
        precheck = [pscustomobject]@{
            detectionRuleAvailable = [bool]$inputData.precheckDetectionRuleAvailable
            additionalDetectionRuleCount = [int]$inputData.precheckAdditionalDetectionRuleCount
            summary = [string]$inputData.precheckSummary
            ruleType = [string]$inputData.detectionRule.ruleType
        }
        request = $inputData
        install = $installResult
        uninstall = $uninstallResult
        uninstallResolution = $uninstallResolution
        uninstallValidation = $uninstallValidation
        launchRemediation = $launchRemediation
        launchValidation = $launchValidation
        preInstallDetection = $preDetection
        postInstallDetection = $postDetection
        postUninstallDetection = $postUninstallDetection
        diff = $diff
        candidates = $candidates
        snapshots = [pscustomobject]@{
            baseline = $baseline
            postInstall = $postInstall
            postUninstall = $postUninstall
        }
    }

    $result | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    Write-Report -Result $result
    Set-Content -LiteralPath $CompletedMarkerPath -Value (Get-Date).ToUniversalTime().ToString('o') -Encoding UTF8
    Write-ProofLog 'Sandbox proof run completed.'
}
catch {
    $failure = [pscustomobject]@{
        schemaVersion = 1
        completedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        failed = $true
        error = $_.Exception.Message
    }
    $failure | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    Set-Content -LiteralPath $ReportPath -Value @('Sandbox proof failed.', $_.Exception.Message) -Encoding UTF8
    Write-ProofLog "Sandbox proof failed: $($_.Exception.Message)"
}
finally {
    try { Stop-Transcript | Out-Null } catch {}
}
""";
    }

    private sealed record SandboxProofInput
    {
        public InstallerType InstallerType { get; init; }

        public string HostSetupFilePath { get; init; } = string.Empty;

        public string HostSourceFolder { get; init; } = string.Empty;

        public string SandboxSetupFilePath { get; init; } = string.Empty;

        public string SandboxWorkingDirectory { get; init; } = string.Empty;

        public string InstallCommand { get; init; } = string.Empty;

        public string UninstallCommand { get; init; } = string.Empty;

        public IntuneDetectionRule DetectionRule { get; init; } = new();

        public string PrecheckSummary { get; init; } = string.Empty;

        public bool PrecheckDetectionRuleAvailable { get; init; }

        public int PrecheckAdditionalDetectionRuleCount { get; init; }

        public int TimeoutMinutes { get; init; }

        public DateTimeOffset CreatedAtUtc { get; init; }
    }
}
