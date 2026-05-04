using System.Diagnostics;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;
using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace IntuneWinPackager.Core.Services;

public sealed class DetectionTestService : IDetectionTestService
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(8);

    private readonly IProcessRunner _processRunner;

    public DetectionTestService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<DetectionTestResult> TestAsync(
        InstallerType installerType,
        IntuneDetectionRule detectionRule,
        CancellationToken cancellationToken = default)
    {
        if (detectionRule is null || detectionRule.RuleType == IntuneDetectionRuleType.None)
        {
            return Failed("No detection rule is configured.");
        }

        return detectionRule.RuleType switch
        {
            IntuneDetectionRuleType.MsiProductCode => OperatingSystem.IsWindows()
                ? TestMsiDetectionWindows(detectionRule.Msi)
                : Failed("MSI detection test is only supported on Windows."),
            IntuneDetectionRuleType.Registry => OperatingSystem.IsWindows()
                ? TestRegistryDetectionWindows(detectionRule.Registry)
                : Failed("Registry detection test is only supported on Windows."),
            IntuneDetectionRuleType.File => TestFileDetection(detectionRule.File),
            IntuneDetectionRuleType.Script => await TestScriptDetectionAsync(installerType, detectionRule.Script, cancellationToken),
            _ => Failed($"Detection rule type '{detectionRule.RuleType}' is not supported in local tests.")
        };
    }

    public async Task<DetectionProofResult> ProveAsync(
        DetectionProofRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.DetectionRule.RuleType == IntuneDetectionRuleType.None)
        {
            return new DetectionProofResult
            {
                Success = false,
                Mode = request?.Mode ?? DetectionProofMode.PassiveRuleControl,
                Summary = "Detection proof requires a configured detection rule.",
                NegativePhase = new DetectionProofPhaseResult
                {
                    PhaseName = "Phase A",
                    Success = false,
                    Summary = "Missing detection rule.",
                    Details = "No detection rule was provided."
                },
                PositivePhase = new DetectionProofPhaseResult
                {
                    PhaseName = "Phase B",
                    Success = false,
                    Summary = "Missing detection rule.",
                    Details = "No detection rule was provided."
                }
            };
        }

        return request.Mode == DetectionProofMode.ActiveInstallFlow
            ? await ProveWithActiveInstallFlowAsync(request, cancellationToken)
            : await ProveWithPassiveRuleControlAsync(request, cancellationToken);
    }

    private async Task<DetectionProofResult> ProveWithPassiveRuleControlAsync(
        DetectionProofRequest request,
        CancellationToken cancellationToken)
    {
        var negativeRule = BuildNegativeControlRule(request.DetectionRule);
        var negative = await TestAsync(request.InstallerType, negativeRule, cancellationToken);
        var positive = await TestAsync(request.InstallerType, request.DetectionRule, cancellationToken);

        var negativeOk = !negative.Success;
        var positiveOk = positive.Success;
        var success = negativeOk && positiveOk;

        return new DetectionProofResult
        {
            Success = success,
            Mode = DetectionProofMode.PassiveRuleControl,
            Summary = success
                ? "Two-phase detection proof passed (negative control failed, positive rule passed)."
                : "Two-phase detection proof failed. Review both phases.",
            NegativePhase = new DetectionProofPhaseResult
            {
                PhaseName = "Phase A (negative control)",
                Success = negativeOk,
                Summary = negativeOk
                    ? "Negative control correctly failed."
                    : "Negative control unexpectedly passed (possible false-positive rule).",
                Details = negative.Details
            },
            PositivePhase = new DetectionProofPhaseResult
            {
                PhaseName = "Phase B (positive rule)",
                Success = positiveOk,
                Summary = positiveOk
                    ? "Positive rule passed."
                    : "Positive rule failed (app may not be installed or rule is incorrect).",
                Details = positive.Details
            }
        };
    }

    private async Task<DetectionProofResult> ProveWithActiveInstallFlowAsync(
        DetectionProofRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.InstallCommand))
        {
            return await ProveWithPassiveRuleControlAsync(request with
            {
                Mode = DetectionProofMode.PassiveRuleControl
            }, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.UninstallCommand))
        {
            await ExecuteCommandAsync(request.UninstallCommand, request.WorkingDirectory, cancellationToken);
        }

        var phaseA = await TestAsync(request.InstallerType, request.DetectionRule, cancellationToken);
        var phaseAOk = !phaseA.Success;

        var installResult = await ExecuteCommandAsync(request.InstallCommand, request.WorkingDirectory, cancellationToken);
        if (installResult.ExitCode != 0 || installResult.TimedOut)
        {
            return new DetectionProofResult
            {
                Success = false,
                Mode = DetectionProofMode.ActiveInstallFlow,
                Summary = $"Install phase failed (exit code {installResult.ExitCode}).",
                NegativePhase = new DetectionProofPhaseResult
                {
                    PhaseName = "Phase A (pre-install)",
                    Success = phaseAOk,
                    Summary = phaseAOk ? "Pre-install detection correctly failed." : "Pre-install detection unexpectedly passed.",
                    Details = phaseA.Details
                },
                PositivePhase = new DetectionProofPhaseResult
                {
                    PhaseName = "Phase B (post-install)",
                    Success = false,
                    Summary = "Install command failed before post-install detection.",
                    Details = $"Install command exited with {installResult.ExitCode}."
                }
            };
        }

        var phaseB = await TestAsync(request.InstallerType, request.DetectionRule, cancellationToken);
        var phaseBOk = phaseB.Success;

        return new DetectionProofResult
        {
            Success = phaseAOk && phaseBOk,
            Mode = DetectionProofMode.ActiveInstallFlow,
            Summary = phaseAOk && phaseBOk
                ? "Two-phase active detection proof passed."
                : "Two-phase active detection proof failed.",
            NegativePhase = new DetectionProofPhaseResult
            {
                PhaseName = "Phase A (pre-install)",
                Success = phaseAOk,
                Summary = phaseAOk ? "Pre-install detection correctly failed." : "Pre-install detection unexpectedly passed.",
                Details = phaseA.Details
            },
            PositivePhase = new DetectionProofPhaseResult
            {
                PhaseName = "Phase B (post-install)",
                Success = phaseBOk,
                Summary = phaseBOk ? "Post-install detection passed." : "Post-install detection failed.",
                Details = phaseB.Details
            }
        };
    }

    [SupportedOSPlatform("windows")]
    private static DetectionTestResult TestMsiDetectionWindows(MsiDetectionRule rule)
    {
        var normalizedProductCode = NormalizeProductCode(rule.ProductCode);
        if (string.IsNullOrWhiteSpace(normalizedProductCode))
        {
            return Failed("MSI ProductCode is invalid.");
        }

        var roots = new[]
        {
            (Hive: RegistryHive.LocalMachine, View: RegistryView.Registry64, Path: $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{normalizedProductCode}"),
            (Hive: RegistryHive.LocalMachine, View: RegistryView.Registry32, Path: $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{normalizedProductCode}"),
            (Hive: RegistryHive.CurrentUser, View: RegistryView.Default, Path: $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{normalizedProductCode}")
        };

        foreach (var root in roots)
        {
            try
            {
                using var key = RegistryKey
                    .OpenBaseKey(root.Hive, root.View)
                    .OpenSubKey(root.Path);
                if (key is null)
                {
                    continue;
                }

                var detectedVersion = key.GetValue("DisplayVersion")?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(rule.ProductVersion))
                {
                    var expected = NormalizeVersion(rule.ProductVersion);
                    var actual = NormalizeVersion(detectedVersion);
                    if (!EvaluateOperator(actual, expected, rule.ProductVersionOperator))
                    {
                        return Failed(
                            $"MSI ProductCode was found, but version comparison failed. " +
                            $"Operator '{rule.ProductVersionOperator}', expected '{rule.ProductVersion}', found '{detectedVersion}'.");
                    }
                }

                var details = string.IsNullOrWhiteSpace(detectedVersion)
                    ? $"ProductCode {normalizedProductCode} exists."
                    : $"ProductCode {normalizedProductCode} exists with DisplayVersion '{detectedVersion}'.";

                return Succeeded("MSI detection passed.", details);
            }
            catch
            {
                // Keep checking remaining views/hives.
            }
        }

        return Failed($"MSI ProductCode '{normalizedProductCode}' was not found in uninstall registry.");
    }

    [SupportedOSPlatform("windows")]
    private static DetectionTestResult TestRegistryDetectionWindows(RegistryDetectionRule rule)
    {
        if (!TryMapHive(rule.Hive, out var hive))
        {
            return Failed("Registry hive is not valid.");
        }

        if (string.IsNullOrWhiteSpace(rule.KeyPath))
        {
            return Failed("Registry key path is required.");
        }

        var views = ResolveRegistryViews(hive, rule.Check32BitOn64System);
        foreach (var view in views)
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(rule.KeyPath);
                if (rule.Operator == IntuneDetectionOperator.Exists)
                {
                    if (key is null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(rule.ValueName))
                    {
                        return Succeeded("Registry detection passed.", $"Registry key '{rule.Hive}\\{rule.KeyPath}' exists.");
                    }

                    if (key.GetValue(rule.ValueName) is not null)
                    {
                        return Succeeded(
                            "Registry detection passed.",
                            $"Registry value '{rule.ValueName}' exists under '{rule.Hive}\\{rule.KeyPath}'.");
                    }

                    continue;
                }

                if (key is null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rule.ValueName))
                {
                    return Failed("Registry value name is required for comparison operators.");
                }

                var currentValue = key.GetValue(rule.ValueName)?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(currentValue))
                {
                    continue;
                }

                if (EvaluateOperator(currentValue, rule.Value, rule.Operator))
                {
                    return Succeeded(
                        "Registry detection passed.",
                        $"Registry comparison passed for '{rule.ValueName}' with value '{currentValue}'.");
                }
            }
            catch
            {
                // Keep checking remaining views/hives.
            }
        }

        return Failed("Registry detection did not match on this device.");
    }

    private static DetectionTestResult TestFileDetection(FileDetectionRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Path) || string.IsNullOrWhiteSpace(rule.FileOrFolderName))
        {
            return Failed("File detection requires both path and file/folder name.");
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(rule.Path.Trim());
        var target = Path.Combine(expandedPath, rule.FileOrFolderName.Trim());
        var fileExists = File.Exists(target);
        var directoryExists = Directory.Exists(target);
        var exists = fileExists || directoryExists;

        if (rule.Operator == IntuneDetectionOperator.Exists)
        {
            return exists
                ? Succeeded("File detection passed.", $"Detected '{target}'.")
                : Failed($"'{target}' was not found.");
        }

        if (!exists)
        {
            return Failed($"'{target}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(rule.Value))
        {
            return Failed("File detection comparison requires a value.");
        }

        var actual = ResolveFileComparisonValue(target, fileExists);
        if (string.IsNullOrWhiteSpace(actual))
        {
            return Failed($"Could not read comparison value for '{target}'.");
        }

        return EvaluateOperator(actual, rule.Value, rule.Operator)
            ? Succeeded("File detection passed.", $"Compared '{actual}' with expected '{rule.Value}'.")
            : Failed($"File comparison failed. Actual '{actual}', expected '{rule.Value}'.");
    }

    private async Task<DetectionTestResult> TestScriptDetectionAsync(
        InstallerType installerType,
        ScriptDetectionRule rule,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rule.ScriptBody))
        {
            return Failed("Detection script is empty.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "IntuneWinPackager", "detection-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var scriptPath = Path.Combine(tempRoot, "detect.ps1");
        await File.WriteAllTextAsync(scriptPath, rule.ScriptBody, cancellationToken);

        var outputLines = new List<string>();
        var errorLines = new List<string>();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ScriptTimeout);

            var processResult = await _processRunner.RunAsync(
                new ProcessRunRequest
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File {QuoteArgument(scriptPath)}",
                    WorkingDirectory = tempRoot
                },
                new InlineProgress<ProcessOutputLine>(line =>
                {
                    if (string.IsNullOrWhiteSpace(line.Text))
                    {
                        return;
                    }

                    if (line.Severity == LogSeverity.Error)
                    {
                        errorLines.Add(line.Text.Trim());
                    }
                    else
                    {
                        outputLines.Add(line.Text.Trim());
                    }
                }),
                timeoutCts.Token);

            if (processResult.TimedOut)
            {
                return Failed("Detection script timed out.");
            }

            var stdOut = string.Join(Environment.NewLine, outputLines.Where(line => !string.IsNullOrWhiteSpace(line)));
            var stdErr = string.Join(Environment.NewLine, errorLines.Where(line => !string.IsNullOrWhiteSpace(line)));
            var hasStdOut = !string.IsNullOrWhiteSpace(stdOut);
            var hasStdErr = !string.IsNullOrWhiteSpace(stdErr);
            var compliant = processResult.ExitCode == 0 && hasStdOut && !hasStdErr;
            var summary = compliant
                ? $"{installerType} script detection passed (exit 0 with STDOUT content)."
                : $"{installerType} script detection failed Intune compliance check (needs exit 0 + STDOUT, no STDERR).";

            return new DetectionTestResult
            {
                Success = compliant,
                Summary = summary,
                Details = compliant
                    ? "Script produced a valid installed signal for Intune."
                    : $"ExitCode={processResult.ExitCode}, HasStdOut={hasStdOut}, HasStdErr={hasStdErr}.",
                ExitCode = processResult.ExitCode,
                HasStdOut = hasStdOut,
                HasStdErr = hasStdErr,
                StandardOutput = stdOut,
                StandardError = stdErr
            };
        }
        catch (OperationCanceledException)
        {
            return Failed("Detection script test was cancelled or timed out.");
        }
        catch (Exception ex)
        {
            return Failed($"Detection script test failed: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private async Task<ProcessRunResult> ExecuteCommandAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeout);

        return await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.CurrentDirectory
                    : workingDirectory
            },
            cancellationToken: timeoutCts.Token);
    }

    private static IntuneDetectionRule BuildNegativeControlRule(IntuneDetectionRule original)
    {
        if (original.RuleType == IntuneDetectionRuleType.MsiProductCode)
        {
            return original with
            {
                Msi = original.Msi with
                {
                    ProductCode = $"{{{Guid.NewGuid():D}}}",
                    ProductVersion = string.IsNullOrWhiteSpace(original.Msi.ProductVersion)
                        ? string.Empty
                        : original.Msi.ProductVersion + ".negative"
                }
            };
        }

        if (original.RuleType == IntuneDetectionRuleType.File)
        {
            return original with
            {
                File = original.File with
                {
                    FileOrFolderName = original.File.FileOrFolderName + ".iwp-negative-proof",
                    Value = string.IsNullOrWhiteSpace(original.File.Value)
                        ? original.File.Value
                        : original.File.Value + ".iwp-negative-proof"
                }
            };
        }

        if (original.RuleType == IntuneDetectionRuleType.Registry)
        {
            var registry = original.Registry;
            if (registry.Operator == IntuneDetectionOperator.Exists)
            {
                if (!string.IsNullOrWhiteSpace(registry.ValueName))
                {
                    return original with
                    {
                        Registry = registry with
                        {
                            ValueName = registry.ValueName + "_iwp_negative_proof"
                        }
                    };
                }

                return original with
                {
                    Registry = registry with
                    {
                        KeyPath = registry.KeyPath + "\\_iwp_negative_proof"
                    }
                };
            }

            return original with
            {
                Registry = registry with
                {
                    Value = string.IsNullOrWhiteSpace(registry.Value)
                        ? "__iwp_negative_proof__"
                        : registry.Value + "__iwp_negative_proof__"
                }
            };
        }

        if (original.RuleType == IntuneDetectionRuleType.Script)
        {
            var scriptBody = original.Script.ScriptBody;
            scriptBody = new Regex(@"(?im)(\$displayVersion\s*=\s*"")([^""]+)("")")
                .Replace(scriptBody, "$1$2.iwp-negative-proof$3", 1);
            scriptBody = new Regex(@"(?im)(\$expectedVersion\s*=\s*"")([^""]+)("")")
                .Replace(scriptBody, "$1$2.iwp-negative-proof$3", 1);

            if (scriptBody.Equals(original.Script.ScriptBody, StringComparison.Ordinal))
            {
                scriptBody = "exit 1";
            }

            return original with
            {
                Script = original.Script with
                {
                    ScriptBody = scriptBody
                }
            };
        }

        return original;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryMapHive(string hiveValue, out RegistryHive hive)
    {
        var normalized = (hiveValue ?? string.Empty).Trim().ToUpperInvariant();
        hive = normalized switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            "HKCC" or "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
            _ => RegistryHive.LocalMachine
        };

        return normalized is "HKLM" or "HKEY_LOCAL_MACHINE" or
            "HKCU" or "HKEY_CURRENT_USER" or
            "HKCR" or "HKEY_CLASSES_ROOT" or
            "HKU" or "HKEY_USERS" or
            "HKCC" or "HKEY_CURRENT_CONFIG";
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<RegistryView> ResolveRegistryViews(RegistryHive hive, bool prefer32BitOn64)
    {
        if (hive != RegistryHive.LocalMachine)
        {
            return [RegistryView.Default];
        }

        if (!Environment.Is64BitOperatingSystem)
        {
            return [RegistryView.Default];
        }

        return prefer32BitOn64
            ? [RegistryView.Registry32]
            : [RegistryView.Registry64, RegistryView.Registry32];
    }

    private static string ResolveFileComparisonValue(string fileOrFolderPath, bool isFile)
    {
        if (isFile)
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(fileOrFolderPath);
                var version = NormalizeVersion(versionInfo.ProductVersion ?? versionInfo.FileVersion ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
            catch
            {
                // fall through to file length
            }

            try
            {
                var info = new FileInfo(fileOrFolderPath);
                return info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        try
        {
            var info = new DirectoryInfo(fileOrFolderPath);
            return info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool EvaluateOperator(string actualRaw, string expectedRaw, IntuneDetectionOperator @operator)
    {
        var actual = (actualRaw ?? string.Empty).Trim();
        var expected = (expectedRaw ?? string.Empty).Trim();

        if (@operator == IntuneDetectionOperator.Equals)
        {
            return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        if (@operator == IntuneDetectionOperator.NotEquals)
        {
            return !actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        if (TryParseVersion(actual, out var actualVersion) && TryParseVersion(expected, out var expectedVersion))
        {
            var compare = actualVersion.CompareTo(expectedVersion);
            return CompareOrdering(compare, @operator);
        }

        if (decimal.TryParse(actual, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var actualNumber) &&
            decimal.TryParse(expected, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var expectedNumber))
        {
            var compare = actualNumber.CompareTo(expectedNumber);
            return CompareOrdering(compare, @operator);
        }

        var textCompare = string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase);
        return CompareOrdering(textCompare, @operator);
    }

    private static bool CompareOrdering(int compare, IntuneDetectionOperator @operator)
    {
        return @operator switch
        {
            IntuneDetectionOperator.GreaterThan => compare > 0,
            IntuneDetectionOperator.GreaterThanOrEqual => compare >= 0,
            IntuneDetectionOperator.LessThan => compare < 0,
            IntuneDetectionOperator.LessThanOrEqual => compare <= 0,
            IntuneDetectionOperator.Equals => compare == 0,
            IntuneDetectionOperator.NotEquals => compare != 0,
            _ => false
        };
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        value = NormalizeVersion(value);
        return Version.TryParse(value, out version!);
    }

    private static string NormalizeVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        var plusIndex = normalized.IndexOf('+');
        if (plusIndex > 0)
        {
            normalized = normalized[..plusIndex];
        }

        return normalized;
    }

    private static string NormalizeProductCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().Trim('{', '}');
        if (!Guid.TryParse(trimmed, out var parsed))
        {
            return string.Empty;
        }

        return $"{{{parsed:D}}}".ToUpperInvariant();
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Contains(' ') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public InlineProgress(Action<T> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }

    private static DetectionTestResult Failed(string message)
    {
        return new DetectionTestResult
        {
            Success = false,
            Summary = message,
            Details = message,
            ExitCode = -1
        };
    }

    private static DetectionTestResult Succeeded(string summary, string details)
    {
        return new DetectionTestResult
        {
            Success = true,
            Summary = summary,
            Details = details,
            ExitCode = 0
        };
    }
}
