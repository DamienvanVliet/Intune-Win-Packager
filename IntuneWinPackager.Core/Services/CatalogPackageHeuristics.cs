using System.Text;
using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Services;

public static class CatalogPackageHeuristics
{
    private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "app",
        "apps",
        "application",
        "setup",
        "installer",
        "install",
        "package",
        "software",
        "tool",
        "tools",
        "helper",
        "client",
        "desktop",
        "windows",
        "win",
        "x64",
        "x86",
        "amd64",
        "arm64",
        "stable",
        "release",
        "inc",
        "llc",
        "ltd",
        "corp",
        "corporation",
        "company",
        "co",
        "technologies",
        "technology",
        "systems"
    };

    public static IReadOnlyList<string> BuildMatchPatterns(PackageCatalogEntry entry)
    {
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddPattern(patterns, entry.Name);
        AddPattern(patterns, entry.PackageId);

        if (!string.IsNullOrWhiteSpace(entry.PackageId))
        {
            foreach (var segment in SplitIdentifier(entry.PackageId))
            {
                AddPattern(patterns, segment);
            }
        }

        if (patterns.Count == 0)
        {
            patterns.Add("Package");
        }

        return patterns.ToList();
    }

    public static string BuildDetectionScript(PackageCatalogEntry entry)
    {
        var patterns = BuildMatchPatterns(entry);
        var idTokens = BuildIdentifierTokens(entry);
        var publisherHints = BuildPublisherHints(entry);
        var versionHints = BuildVersionHints(entry);

        return string.Join(
            Environment.NewLine,
            "# Intune fallback detection script generated from catalog metadata.",
            "# Uses weighted matching to reduce false positives from display-name-only checks.",
            $"$patterns = {ToPowerShellArray(patterns)}",
            $"$idTokens = {ToPowerShellArray(idTokens)}",
            $"$publisherHints = {ToPowerShellArray(publisherHints)}",
            $"$versionHints = {ToPowerShellArray(versionHints)}",
            "$minScore = 55",
            "$roots = @(",
            "  'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',",
            "  'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',",
            "  'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'",
            ")",
            "function Normalize-Text([string]$value) {",
            "  if ([string]::IsNullOrWhiteSpace($value)) { return '' }",
            "  return ([regex]::Replace($value.ToLowerInvariant(), '[^a-z0-9]+', ' ')).Trim()",
            "}",
            "function Contains-Token([string]$text, [string]$token) {",
            "  if ([string]::IsNullOrWhiteSpace($text) -or [string]::IsNullOrWhiteSpace($token)) { return $false }",
            "  $normalizedText = Normalize-Text $text",
            "  $normalizedToken = Normalize-Text $token",
            "  if ([string]::IsNullOrWhiteSpace($normalizedText) -or [string]::IsNullOrWhiteSpace($normalizedToken)) { return $false }",
            "  return $normalizedText -like ('*' + $normalizedToken + '*')",
            "}",
            "$candidates = @()",
            "foreach ($root in $roots) {",
            "  foreach ($key in Get-ChildItem -Path $root -ErrorAction SilentlyContinue) {",
            "    try {",
            "      $item = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction Stop",
            "      $displayName = [string]$item.DisplayName",
            "      if ([string]::IsNullOrWhiteSpace($displayName)) { continue }",
            "      $candidates += [PSCustomObject]@{",
            "        KeyName = [string]$key.PSChildName",
            "        DisplayName = $displayName",
            "        Publisher = [string]$item.Publisher",
            "        DisplayVersion = [string]$item.DisplayVersion",
            "        UninstallString = [string]$item.UninstallString",
            "        QuietUninstallString = [string]$item.QuietUninstallString",
            "        InstallLocation = [string]$item.InstallLocation",
            "        DisplayIcon = [string]$item.DisplayIcon",
            "      }",
            "    } catch { }",
            "  }",
            "}",
            "if ($candidates.Count -eq 0) { exit 1 }",
            "$bestScore = -1",
            "foreach ($candidate in $candidates) {",
            "  $score = 0",
            "  $name = Normalize-Text $candidate.DisplayName",
            "  $keyName = Normalize-Text $candidate.KeyName",
            "  foreach ($pattern in $patterns) {",
            "    $normalizedPattern = Normalize-Text $pattern",
            "    if ([string]::IsNullOrWhiteSpace($normalizedPattern)) { continue }",
            "    if ($name -eq $normalizedPattern) { $score += 70; continue }",
            "    if ($name -like ($normalizedPattern + '*')) { $score += 45 }",
            "    elseif ($name -like ('*' + $normalizedPattern + '*')) { $score += 30 }",
            "    if ($keyName -like ('*' + $normalizedPattern + '*')) { $score += 20 }",
            "  }",
            "  $tokenHits = 0",
            "  foreach ($token in $idTokens) {",
            "    if ([string]::IsNullOrWhiteSpace($token)) { continue }",
            "    if (Contains-Token $candidate.DisplayName $token -or",
            "        Contains-Token $candidate.KeyName $token -or",
            "        Contains-Token $candidate.UninstallString $token -or",
            "        Contains-Token $candidate.QuietUninstallString $token -or",
            "        Contains-Token $candidate.InstallLocation $token -or",
            "        Contains-Token $candidate.DisplayIcon $token) {",
            "      $tokenHits++",
            "    }",
            "  }",
            "  $score += [Math]::Min(($tokenHits * 8), 32)",
            "  $publisherMatched = $false",
            "  foreach ($hint in $publisherHints) {",
            "    if (Contains-Token $candidate.Publisher $hint) {",
            "      $publisherMatched = $true",
            "      break",
            "    }",
            "  }",
            "  if ($publisherMatched) { $score += 18 }",
            "  if ($versionHints.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($candidate.DisplayVersion)) {",
            "    foreach ($versionHint in $versionHints) {",
            "      if ([string]::IsNullOrWhiteSpace($versionHint)) { continue }",
            "      if ($candidate.DisplayVersion -like ($versionHint + '*')) {",
            "        $score += 12",
            "        break",
            "      }",
            "    }",
            "  }",
            "  if ($name -like '*updater*' -or $name -like '*auto update*') { $score -= 20 }",
            "  if ($score -gt $bestScore) {",
            "    $bestScore = $score",
            "  }",
            "}",
            "if ($bestScore -ge $minScore) { exit 0 }",
            "exit 1");
    }

    private static IReadOnlyList<string> BuildIdentifierTokens(PackageCatalogEntry entry)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in SplitIdentifier(entry.PackageId))
        {
            if (IsMeaningfulToken(token))
            {
                tokens.Add(token);
            }
        }

        foreach (var token in SplitIdentifier(entry.Name))
        {
            if (IsMeaningfulToken(token))
            {
                tokens.Add(token);
            }
        }

        if (tokens.Count == 0)
        {
            foreach (var pattern in BuildMatchPatterns(entry))
            {
                var normalized = NormalizeToken(pattern);
                if (normalized.Length >= 3)
                {
                    tokens.Add(normalized);
                }
            }
        }

        return tokens.ToList();
    }

    private static IReadOnlyList<string> BuildPublisherHints(PackageCatalogEntry entry)
    {
        var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPattern(hints, entry.Publisher);

        foreach (var token in SplitIdentifier(entry.Publisher))
        {
            if (token.Length >= 4 && !GenericTokens.Contains(token))
            {
                hints.Add(token);
            }
        }

        return hints.ToList();
    }

    private static IReadOnlyList<string> BuildVersionHints(PackageCatalogEntry entry)
    {
        var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddVersionHints(hints, entry.BuildVersion);
        AddVersionHints(hints, entry.Version);
        return hints.ToList();
    }

    private static void AddPattern(ISet<string> bucket, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 2)
        {
            bucket.Add(trimmed);
        }
    }

    private static IEnumerable<string> SplitIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(['.', '-', '_', ' ', '/', '\\', '(', ')', '[', ']', '{', '}', ',', ';', ':', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !string.IsNullOrWhiteSpace(token));
    }

    private static bool IsMeaningfulToken(string token)
    {
        var normalized = NormalizeToken(token);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (GenericTokens.Contains(normalized))
        {
            return false;
        }

        if (normalized.Length >= 3)
        {
            return true;
        }

        return normalized.Length == 2 &&
               normalized.Any(char.IsLetter) &&
               normalized.Any(char.IsDigit);
    }

    private static string NormalizeToken(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static void AddVersionHints(ISet<string> bucket, string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return;
        }

        var extracted = ExtractNumericVersion(rawVersion);
        if (string.IsNullOrWhiteSpace(extracted))
        {
            return;
        }

        var segments = extracted
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment.All(char.IsDigit))
            .ToArray();
        if (segments.Length == 0)
        {
            return;
        }

        bucket.Add(string.Join('.', segments));
        if (segments.Length >= 2)
        {
            bucket.Add($"{segments[0]}.{segments[1]}");
        }

        bucket.Add(segments[0]);
    }

    private static string ExtractNumericVersion(string value)
    {
        var builder = new StringBuilder();
        var started = false;

        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                builder.Append(character);
                started = true;
                continue;
            }

            if (started && character == '.')
            {
                if (builder.Length > 0 && builder[^1] != '.')
                {
                    builder.Append(character);
                }

                continue;
            }

            if (started)
            {
                break;
            }
        }

        return builder.ToString().Trim('.');
    }

    private static string ToPowerShellArray(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return "@()";
        }

        var escaped = string.Join(", ", values.Select(value => $"'{EscapePowerShellSingleQuoted(value)}'"));
        return $"@({escaped})";
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
