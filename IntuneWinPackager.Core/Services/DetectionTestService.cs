using System.Diagnostics;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace IntuneWinPackager.Core.Services;

public sealed class DetectionTestService : IDetectionTestService
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(45);

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
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return Failed(
                            $"MSI ProductCode was found, but version mismatch. Expected '{rule.ProductVersion}', found '{detectedVersion}'.");
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
