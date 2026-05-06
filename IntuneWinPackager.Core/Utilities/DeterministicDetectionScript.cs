using System.Text.RegularExpressions;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Core.Utilities;

public static class DeterministicDetectionScript
{
    public const string Utf8Bom = "\uFEFF";
    public const string ExeRegistryExactMarker = "# IWP-DETECTION:EXE-REGISTRY-EXACT";
    public const string AppxIdentityExactMarker = "# IWP-DETECTION:APPX-IDENTITY-EXACT";

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ExitZeroRegex = new(@"\bexit\s+0\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string BuildExactExeRegistryScript(
        string displayName,
        string publisher,
        string displayVersion,
        IntuneDetectionOperator versionOperator = IntuneDetectionOperator.Equals)
    {
        var escapedDisplayName = EscapePowerShellDoubleQuoted(displayName);
        var escapedPublisher = EscapePowerShellDoubleQuoted(publisher);
        var escapedDisplayVersion = EscapePowerShellDoubleQuoted(displayVersion);
        var versionOperatorMode = versionOperator == IntuneDetectionOperator.GreaterThanOrEqual
            ? "GreaterThanOrEqual"
            : "Equals";

        return string.Join(Environment.NewLine,
        [
            Utf8Bom + ExeRegistryExactMarker,
            $"$displayName = \"{escapedDisplayName}\"",
            $"$publisher = \"{escapedPublisher}\"",
            $"$displayVersion = \"{escapedDisplayVersion}\"",
            $"$versionOperator = \"{versionOperatorMode}\"",
            "function Test-IwpVersionMatch([string]$actual, [string]$expected, [string]$mode) {",
            "    if ($mode -eq 'GreaterThanOrEqual') {",
            "        $actualVersion = $null",
            "        $expectedVersion = $null",
            "        if ([version]::TryParse($actual, [ref]$actualVersion) -and [version]::TryParse($expected, [ref]$expectedVersion)) {",
            "            return $actualVersion -ge $expectedVersion",
            "        }",
            "        return $actual -ge $expected",
            "    }",
            "    return $actual -eq $expected",
            "}",
            "$roots = @(",
            "    'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',",
            "    'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',",
            "    'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'",
            ")",
            "$match = Get-ItemProperty -Path $roots -ErrorAction SilentlyContinue | Where-Object {",
            "    $_.DisplayName -eq $displayName -and",
            "    $_.Publisher -eq $publisher -and",
            "    (Test-IwpVersionMatch ([string]$_.DisplayVersion) $displayVersion $versionOperator)",
            "} | Select-Object -First 1",
            "if ($null -ne $match) {",
            "    Write-Output (\"detected:{0}\" -f $match.DisplayVersion)",
            "    exit 0",
            "}",
            "exit 1"
        ]);
    }

    public static string BuildExactAppxIdentityScript(
        string packageIdentity,
        string version,
        string publisher = "",
        IntuneDetectionOperator versionOperator = IntuneDetectionOperator.Equals)
    {
        var escapedIdentity = EscapePowerShellDoubleQuoted(packageIdentity);
        var escapedVersion = EscapePowerShellDoubleQuoted(version);
        var escapedPublisher = EscapePowerShellDoubleQuoted(publisher);
        var versionOperatorMode = versionOperator == IntuneDetectionOperator.GreaterThanOrEqual
            ? "GreaterThanOrEqual"
            : "Equals";

        var publisherPredicate = string.IsNullOrWhiteSpace(escapedPublisher)
            ? string.Empty
            : " -and $_.Publisher -eq $publisher";

        return string.Join(Environment.NewLine,
        [
            Utf8Bom + AppxIdentityExactMarker,
            $"$packageName = \"{escapedIdentity}\"",
            $"$expectedVersion = \"{escapedVersion}\"",
            $"$versionOperator = \"{versionOperatorMode}\"",
            "function Test-IwpVersionMatch([string]$actual, [string]$expected, [string]$mode) {",
            "    if ($mode -eq 'GreaterThanOrEqual') {",
            "        $actualVersion = $null",
            "        $expectedVersion = $null",
            "        if ([version]::TryParse($actual, [ref]$actualVersion) -and [version]::TryParse($expected, [ref]$expectedVersion)) {",
            "            return $actualVersion -ge $expectedVersion",
            "        }",
            "        return $actual -ge $expected",
            "    }",
            "    return $actual -eq $expected",
            "}",
            string.IsNullOrWhiteSpace(escapedPublisher)
                ? "$publisher = \"\""
                : $"$publisher = \"{escapedPublisher}\"",
            "$match = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Where-Object {",
            "    (Test-IwpVersionMatch $_.Version.ToString() $expectedVersion $versionOperator)" + publisherPredicate,
            "} | Select-Object -First 1",
            "if ($null -ne $match) {",
            "    Write-Output (\"detected:{0}\" -f $match.Version.ToString())",
            "    exit 0",
            "}",
            "exit 1"
        ]);
    }

    public static bool IsExactExeRegistryScript(string? scriptBody)
    {
        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            return false;
        }

        var normalized = NormalizeScript(scriptBody);
        if (normalized.Contains(NormalizeScript(ExeRegistryExactMarker), StringComparison.Ordinal))
        {
            return true;
        }

        return normalized.Contains("get-itemproperty", StringComparison.Ordinal) &&
               normalized.Contains("displayname-eq$displayname", StringComparison.Ordinal) &&
               normalized.Contains("publisher-eq$publisher", StringComparison.Ordinal) &&
               normalized.Contains("displayversion-eq$displayversion", StringComparison.Ordinal);
    }

    public static bool IsExactAppxIdentityScript(string? scriptBody)
    {
        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            return false;
        }

        var normalized = NormalizeScript(scriptBody);
        if (normalized.Contains(NormalizeScript(AppxIdentityExactMarker), StringComparison.Ordinal))
        {
            return true;
        }

        return normalized.Contains("get-appxpackage", StringComparison.Ordinal) &&
               normalized.Contains("-name", StringComparison.Ordinal) &&
               normalized.Contains("version.tostring()-eq", StringComparison.Ordinal);
    }

    public static bool IsIntuneCompliantSuccessSignalScript(string? scriptBody)
    {
        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            return false;
        }

        var normalized = NormalizeScript(scriptBody);
        if (!normalized.Contains("exit0", StringComparison.Ordinal))
        {
            return false;
        }

        if (!normalized.Contains("exit1", StringComparison.Ordinal))
        {
            return false;
        }

        return WritesStdout(scriptBody);
    }

    public static string NormalizeForIntuneScriptPolicy(string? scriptBody)
    {
        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            return EnsureUtf8Bom(string.Empty);
        }

        var normalized = EnsureUtf8Bom(scriptBody.TrimStart('\uFEFF'));
        if (!WritesStdout(normalized) && ExitZeroRegex.IsMatch(normalized))
        {
            normalized = ExitZeroRegex.Replace(
                normalized,
                "Write-Output 'detected'; exit 0",
                count: 1);
        }

        if (!NormalizeScript(normalized).Contains("exit1", StringComparison.Ordinal))
        {
            normalized = normalized.TrimEnd() + Environment.NewLine + "exit 1";
        }

        return normalized;
    }

    public static bool IsStrictIntuneScriptPolicyCompliant(string? scriptBody)
    {
        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            return false;
        }

        if (!IsUtf8BomPrefixed(scriptBody))
        {
            return false;
        }

        if (!IsIntuneCompliantSuccessSignalScript(scriptBody))
        {
            return false;
        }

        var normalized = NormalizeScript(scriptBody);
        if (normalized.Contains("write-error", StringComparison.Ordinal) ||
            normalized.Contains("throw", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    public static bool IsUtf8BomPrefixed(string? scriptBody)
    {
        return !string.IsNullOrEmpty(scriptBody) && scriptBody.StartsWith(Utf8Bom, StringComparison.Ordinal);
    }

    public static string EnsureUtf8Bom(string scriptBody)
    {
        if (string.IsNullOrEmpty(scriptBody))
        {
            return Utf8Bom;
        }

        return IsUtf8BomPrefixed(scriptBody)
            ? scriptBody
            : Utf8Bom + scriptBody;
    }

    private static bool WritesStdout(string scriptBody)
    {
        var normalized = NormalizeScript(scriptBody);
        return normalized.Contains("write-output", StringComparison.Ordinal) ||
               normalized.Contains("echo", StringComparison.Ordinal) ||
               normalized.Contains("[console]::out.writeline", StringComparison.Ordinal);
    }

    private static string EscapePowerShellDoubleQuoted(string value)
    {
        return (value ?? string.Empty).Replace("\"", "`\"", StringComparison.Ordinal);
    }

    private static string NormalizeScript(string scriptBody)
    {
        var lower = scriptBody.Trim().ToLowerInvariant();
        return WhitespaceRegex.Replace(lower, string.Empty);
    }
}
