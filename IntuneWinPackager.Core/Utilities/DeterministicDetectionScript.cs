using System.Text.RegularExpressions;

namespace IntuneWinPackager.Core.Utilities;

public static class DeterministicDetectionScript
{
    public const string ExeRegistryExactMarker = "# IWP-DETECTION:EXE-REGISTRY-EXACT";
    public const string AppxIdentityExactMarker = "# IWP-DETECTION:APPX-IDENTITY-EXACT";

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static string BuildExactExeRegistryScript(
        string displayName,
        string publisher,
        string displayVersion)
    {
        var escapedDisplayName = EscapePowerShellDoubleQuoted(displayName);
        var escapedPublisher = EscapePowerShellDoubleQuoted(publisher);
        var escapedDisplayVersion = EscapePowerShellDoubleQuoted(displayVersion);

        return string.Join(Environment.NewLine,
        [
            ExeRegistryExactMarker,
            $"$displayName = \"{escapedDisplayName}\"",
            $"$publisher = \"{escapedPublisher}\"",
            $"$displayVersion = \"{escapedDisplayVersion}\"",
            "$roots = @(",
            "    'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',",
            "    'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',",
            "    'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'",
            ")",
            "$match = Get-ItemProperty -Path $roots -ErrorAction SilentlyContinue | Where-Object {",
            "    $_.DisplayName -eq $displayName -and",
            "    $_.Publisher -eq $publisher -and",
            "    $_.DisplayVersion -eq $displayVersion",
            "} | Select-Object -First 1",
            "if ($null -ne $match) { exit 0 }",
            "exit 1"
        ]);
    }

    public static string BuildExactAppxIdentityScript(
        string packageIdentity,
        string version,
        string publisher = "")
    {
        var escapedIdentity = EscapePowerShellDoubleQuoted(packageIdentity);
        var escapedVersion = EscapePowerShellDoubleQuoted(version);
        var escapedPublisher = EscapePowerShellDoubleQuoted(publisher);

        var publisherPredicate = string.IsNullOrWhiteSpace(escapedPublisher)
            ? string.Empty
            : " -and $_.Publisher -eq $publisher";

        return string.Join(Environment.NewLine,
        [
            AppxIdentityExactMarker,
            $"$packageName = \"{escapedIdentity}\"",
            $"$expectedVersion = \"{escapedVersion}\"",
            string.IsNullOrWhiteSpace(escapedPublisher)
                ? "$publisher = \"\""
                : $"$publisher = \"{escapedPublisher}\"",
            "$match = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Where-Object {",
            "    $_.Version.ToString() -eq $expectedVersion" + publisherPredicate,
            "} | Select-Object -First 1",
            "if ($null -ne $match) { exit 0 }",
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
        if (normalized.Contains(ExeRegistryExactMarker.ToLowerInvariant(), StringComparison.Ordinal))
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
        if (normalized.Contains(AppxIdentityExactMarker.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return true;
        }

        return normalized.Contains("get-appxpackage", StringComparison.Ordinal) &&
               normalized.Contains("-name", StringComparison.Ordinal) &&
               normalized.Contains("version.tostring()-eq", StringComparison.Ordinal);
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
