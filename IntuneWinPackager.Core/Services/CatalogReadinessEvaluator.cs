using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Core.Services;

public static class CatalogReadinessEvaluator
{
    public static CatalogReadinessEvaluation Evaluate(
        PackageCatalogEntry? entry,
        CatalogPackageProfile? profile = null)
    {
        entry ??= new PackageCatalogEntry();

        return profile is null
            ? EvaluateCatalogMetadata(entry)
            : EvaluateLocalProfile(profile);
    }

    public static bool IsCatalogEntryReady(PackageCatalogEntry? entry)
        => EvaluateCatalogMetadata(entry ?? new PackageCatalogEntry()).State == CatalogReadinessState.CatalogReady;

    public static bool IsUsableDetectionRule(IntuneDetectionRule? rule)
    {
        if (rule is null)
        {
            return false;
        }

        return rule.RuleType switch
        {
            IntuneDetectionRuleType.MsiProductCode => IsValidMsiProductCode(rule.Msi.ProductCode),
            IntuneDetectionRuleType.File => HasText(rule.File.Path) &&
                                            HasText(rule.File.FileOrFolderName) &&
                                            (rule.File.Operator == IntuneDetectionOperator.Exists || HasText(rule.File.Value)),
            IntuneDetectionRuleType.Registry => IsKnownRegistryHive(rule.Registry.Hive) &&
                                                HasText(rule.Registry.KeyPath) &&
                                                (rule.Registry.Operator == IntuneDetectionOperator.Exists ||
                                                 (HasText(rule.Registry.ValueName) && HasText(rule.Registry.Value))),
            IntuneDetectionRuleType.Script => HasText(rule.Script.ScriptBody),
            _ => false
        };
    }

    private static CatalogReadinessEvaluation EvaluateCatalogMetadata(PackageCatalogEntry entry)
    {
        var hasInstallSource = HasFactualInstallerSource(entry);
        var hasDetectionRule = HasFactualDetectionRule(entry);
        var hasPlaceholders = HasCommandPlaceholders(entry);
        var state = hasInstallSource && hasDetectionRule && !hasPlaceholders
            ? CatalogReadinessState.CatalogReady
            : CatalogReadinessState.NeedsReview;

        return new CatalogReadinessEvaluation
        {
            State = state,
            HasFactualInstallerSource = hasInstallSource,
            HasFactualDetectionRule = hasDetectionRule,
            HasUnresolvedCommandPlaceholders = hasPlaceholders,
            HasLocalInstaller = HasExistingFile(entry.LocalInstallerPath),
            Summary = BuildCatalogSummary(state, hasInstallSource, hasDetectionRule, hasPlaceholders)
        };
    }

    private static CatalogReadinessEvaluation EvaluateLocalProfile(CatalogPackageProfile profile)
    {
        var hasInstaller = HasExistingFile(profile.InstallerPath);
        var hasDetectionRule = profile.DetectionReady &&
                               profile.DetectionRuleType != IntuneDetectionRuleType.None &&
                               IsUsableDetectionRule(profile.IntuneRules.DetectionRule);
        var hasPlaceholders =
            ContainsTemplatePlaceholder(profile.InstallCommand) ||
            ContainsTemplatePlaceholder(profile.UninstallCommand);
        var state = !hasInstaller || !hasDetectionRule || hasPlaceholders
            ? CatalogReadinessState.Blocked
            : profile.Confidence == CatalogProfileConfidence.Verified
                ? CatalogReadinessState.Ready
                : CatalogReadinessState.NeedsReview;

        return new CatalogReadinessEvaluation
        {
            State = state,
            HasFactualInstallerSource = hasInstaller,
            HasFactualDetectionRule = hasDetectionRule,
            HasUnresolvedCommandPlaceholders = hasPlaceholders,
            HasLocalInstaller = hasInstaller,
            Summary = BuildProfileSummary(state, hasInstaller, hasDetectionRule, hasPlaceholders)
        };
    }

    private static bool HasFactualInstallerSource(PackageCatalogEntry entry)
    {
        if (HasExistingFile(entry.LocalInstallerPath) || IsDirectInstallerUrl(entry.InstallerDownloadUrl))
        {
            return true;
        }

        return entry.InstallerVariants.Any(variant => IsDirectInstallerUrl(variant.InstallerDownloadUrl));
    }

    private static bool HasFactualDetectionRule(PackageCatalogEntry entry)
    {
        return entry.InstallerVariants.Any(variant =>
            variant.IsDeterministicDetection &&
            IsUsableDetectionRule(variant.DetectionRule));
    }

    private static bool HasCommandPlaceholders(PackageCatalogEntry entry)
    {
        return ContainsTemplatePlaceholder(entry.SuggestedInstallCommand) ||
               ContainsTemplatePlaceholder(entry.SuggestedUninstallCommand) ||
               entry.InstallerVariants.Any(variant =>
                   ContainsTemplatePlaceholder(variant.SuggestedInstallCommand) ||
                   ContainsTemplatePlaceholder(variant.SuggestedUninstallCommand));
    }

    private static string BuildCatalogSummary(
        CatalogReadinessState state,
        bool hasInstallSource,
        bool hasDetectionRule,
        bool hasPlaceholders)
    {
        if (state == CatalogReadinessState.CatalogReady)
        {
            return "Catalog metadata includes a direct installer source and a complete deterministic detection rule. Run Sandbox Proof to validate it locally.";
        }

        var reasons = new List<string>();
        if (!hasInstallSource)
        {
            reasons.Add("missing direct installer URL or local installer file");
        }

        if (!hasDetectionRule)
        {
            reasons.Add("missing complete deterministic detection rule");
        }

        if (hasPlaceholders)
        {
            reasons.Add("command template still has placeholders");
        }

        return reasons.Count == 0
            ? "Catalog metadata needs review before it can be treated as ready."
            : $"Review needed: {string.Join("; ", reasons)}.";
    }

    private static string BuildProfileSummary(
        CatalogReadinessState state,
        bool hasInstaller,
        bool hasDetectionRule,
        bool hasPlaceholders)
    {
        return state switch
        {
            CatalogReadinessState.Ready => "Validated local profile: installer file exists, detection rule is complete, and local proof passed.",
            CatalogReadinessState.NeedsReview => "Local profile is prepared, but it has not passed local proof yet.",
            _ => BuildBlockedProfileSummary(hasInstaller, hasDetectionRule, hasPlaceholders)
        };
    }

    private static string BuildBlockedProfileSummary(bool hasInstaller, bool hasDetectionRule, bool hasPlaceholders)
    {
        var reasons = new List<string>();
        if (!hasInstaller)
        {
            reasons.Add("local installer file is missing");
        }

        if (!hasDetectionRule)
        {
            reasons.Add("saved detection rule is incomplete");
        }

        if (hasPlaceholders)
        {
            reasons.Add("saved command still has placeholders");
        }

        return reasons.Count == 0
            ? "Local profile is blocked until its saved evidence is refreshed."
            : $"Blocked: {string.Join("; ", reasons)}.";
    }

    private static bool IsDirectInstallerUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExistingFile(string value)
        => !string.IsNullOrWhiteSpace(value) && File.Exists(value);

    private static bool ContainsTemplatePlaceholder(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Contains('<') && value.Contains('>');

    private static bool HasText(string value)
        => !string.IsNullOrWhiteSpace(value);

    private static bool IsValidMsiProductCode(string value)
        => Guid.TryParse(value?.Trim().Trim('{', '}'), out _);

    private static bool IsKnownRegistryHive(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized is "HKLM" or "HKEY_LOCAL_MACHINE" or
            "HKCU" or "HKEY_CURRENT_USER" or
            "HKCR" or "HKEY_CLASSES_ROOT" or
            "HKU" or "HKEY_USERS" or
            "HKCC" or "HKEY_CURRENT_CONFIG";
    }
}
