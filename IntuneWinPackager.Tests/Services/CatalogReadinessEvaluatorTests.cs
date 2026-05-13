using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Tests.Services;

public sealed class CatalogReadinessEvaluatorTests
{
    [Fact]
    public void Evaluate_DoesNotTreatPackageIdAsInstallerSource()
    {
        var entry = new PackageCatalogEntry
        {
            PackageId = "Contoso.Tool",
            InstallerVariants =
            [
                new CatalogInstallerVariant
                {
                    PackageId = "Contoso.Tool",
                    IsDeterministicDetection = true,
                    DetectionRule = FileRule()
                }
            ]
        };

        var result = CatalogReadinessEvaluator.Evaluate(entry);

        Assert.Equal(CatalogReadinessState.NeedsReview, result.State);
        Assert.False(result.HasFactualInstallerSource);
        Assert.True(result.HasFactualDetectionRule);
        Assert.Contains("missing direct installer", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_WithDirectUrlAndDeterministicFileRule_IsCatalogReady()
    {
        var entry = new PackageCatalogEntry
        {
            PackageId = "Contoso.Tool",
            InstallerVariants =
            [
                new CatalogInstallerVariant
                {
                    PackageId = "Contoso.Tool",
                    InstallerDownloadUrl = "https://download.contoso.example/tool.exe",
                    IsDeterministicDetection = true,
                    DetectionRule = FileRule()
                }
            ]
        };

        var result = CatalogReadinessEvaluator.Evaluate(entry);

        Assert.Equal(CatalogReadinessState.CatalogReady, result.State);
        Assert.True(result.HasFactualInstallerSource);
        Assert.True(result.HasFactualDetectionRule);
    }

    [Fact]
    public void Evaluate_WithIncompleteRegistryRule_NeedsReview()
    {
        var entry = new PackageCatalogEntry
        {
            PackageId = "Contoso.Tool",
            InstallerDownloadUrl = "https://download.contoso.example/tool.exe",
            InstallerVariants =
            [
                new CatalogInstallerVariant
                {
                    PackageId = "Contoso.Tool",
                    InstallerDownloadUrl = "https://download.contoso.example/tool.exe",
                    IsDeterministicDetection = true,
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Registry,
                        Registry = new RegistryDetectionRule
                        {
                            Hive = "HKEY_LOCAL_MACHINE",
                            KeyPath = @"SOFTWARE\Contoso\Tool",
                            Operator = IntuneDetectionOperator.Equals,
                            ValueName = "DisplayVersion"
                        }
                    }
                }
            ]
        };

        var result = CatalogReadinessEvaluator.Evaluate(entry);

        Assert.Equal(CatalogReadinessState.NeedsReview, result.State);
        Assert.False(result.HasFactualDetectionRule);
        Assert.Contains("missing complete deterministic detection", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_VerifiedLocalProfileWithCompleteRule_IsReady()
    {
        var tempInstaller = Path.Combine(Path.GetTempPath(), $"iwp-readiness-{Guid.NewGuid():N}.msi");
        File.WriteAllText(tempInstaller, "test");
        try
        {
            var profile = new CatalogPackageProfile
            {
                InstallerPath = tempInstaller,
                DetectionReady = true,
                DetectionRuleType = IntuneDetectionRuleType.MsiProductCode,
                Confidence = CatalogProfileConfidence.Verified,
                IntuneRules = new IntuneWin32AppRules
                {
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.MsiProductCode,
                        Msi = new MsiDetectionRule
                        {
                            ProductCode = "{11111111-2222-3333-4444-555555555555}"
                        }
                    }
                }
            };

            var result = CatalogReadinessEvaluator.Evaluate(new PackageCatalogEntry(), profile);

            Assert.Equal(CatalogReadinessState.Ready, result.State);
            Assert.True(result.HasLocalInstaller);
            Assert.True(result.HasFactualDetectionRule);
        }
        finally
        {
            File.Delete(tempInstaller);
        }
    }

    [Fact]
    public void Evaluate_ProfileWithRuleTypeButNoSavedRule_IsBlocked()
    {
        var tempInstaller = Path.Combine(Path.GetTempPath(), $"iwp-readiness-{Guid.NewGuid():N}.msi");
        File.WriteAllText(tempInstaller, "test");
        try
        {
            var profile = new CatalogPackageProfile
            {
                InstallerPath = tempInstaller,
                DetectionReady = true,
                DetectionRuleType = IntuneDetectionRuleType.MsiProductCode,
                Confidence = CatalogProfileConfidence.Verified
            };

            var result = CatalogReadinessEvaluator.Evaluate(new PackageCatalogEntry(), profile);

            Assert.Equal(CatalogReadinessState.Blocked, result.State);
            Assert.False(result.HasFactualDetectionRule);
            Assert.Contains("saved detection rule is incomplete", result.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempInstaller);
        }
    }

    private static IntuneDetectionRule FileRule()
    {
        return new IntuneDetectionRule
        {
            RuleType = IntuneDetectionRuleType.File,
            File = new FileDetectionRule
            {
                Path = @"C:\Program Files\Contoso Tool",
                FileOrFolderName = "ContosoTool.exe",
                Operator = IntuneDetectionOperator.Equals,
                Value = "1.2.3"
            }
        };
    }
}
