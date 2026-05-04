using System.Text.RegularExpressions;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Core.Services;

public sealed class CuratedDetectionProfileService : ICuratedDetectionProfileService
{
    private static readonly IReadOnlyList<CuratedDetectionProfile> Profiles =
    [
        new CuratedDetectionProfile
        {
            ProfileId = "winget:7zip.7zip:exe",
            PackageId = "7zip.7zip",
            DisplayName = "7-Zip",
            Publisher = "Igor Pavlov",
            InstallerType = InstallerType.Exe,
            VersionPattern = @"^\d+(\.\d+){0,3}$",
            ConfidenceScore = 94,
            ConfidenceLabel = "verified",
            IsSignedProfile = true,
            Rules = new IntuneWin32AppRules
            {
                DetectionIntent = DetectionDeploymentIntent.Install,
                ExeIdentityLockEnabled = true,
                EnforceStrictScriptPolicy = true,
                DetectionRule = new IntuneDetectionRule
                {
                    RuleType = IntuneDetectionRuleType.Registry,
                    Registry = new RegistryDetectionRule
                    {
                        Hive = "HKEY_LOCAL_MACHINE",
                        KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip",
                        ValueName = "DisplayVersion",
                        Operator = IntuneDetectionOperator.Equals
                    }
                },
                AdditionalDetectionRules =
                [
                    new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Registry,
                        Registry = new RegistryDetectionRule
                        {
                            Hive = "HKEY_LOCAL_MACHINE",
                            KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip",
                            ValueName = "DisplayName",
                            Operator = IntuneDetectionOperator.Equals,
                            Value = "7-Zip"
                        }
                    },
                    new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Registry,
                        Registry = new RegistryDetectionRule
                        {
                            Hive = "HKEY_LOCAL_MACHINE",
                            KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip",
                            ValueName = "Publisher",
                            Operator = IntuneDetectionOperator.Equals,
                            Value = "Igor Pavlov"
                        }
                    }
                ],
                DetectionProvenance =
                [
                    new DetectionFieldProvenance
                    {
                        FieldName = "DisplayName",
                        FieldValue = "7-Zip",
                        Source = DetectionProvenanceSource.ManifestSource,
                        IsStrongEvidence = true,
                        Notes = "Curated vendor profile"
                    },
                    new DetectionFieldProvenance
                    {
                        FieldName = "Publisher",
                        FieldValue = "Igor Pavlov",
                        Source = DetectionProvenanceSource.ManifestSource,
                        IsStrongEvidence = true,
                        Notes = "Curated vendor profile"
                    }
                ]
            }
        },
        new CuratedDetectionProfile
        {
            ProfileId = "winget:microsoft.visualstudiocode:msi",
            PackageId = "microsoft.visualstudiocode",
            DisplayName = "Visual Studio Code",
            Publisher = "Microsoft Corporation",
            InstallerType = InstallerType.Msi,
            VersionPattern = @"^\d+(\.\d+){1,3}$",
            ConfidenceScore = 91,
            ConfidenceLabel = "verified",
            IsSignedProfile = true,
            Rules = new IntuneWin32AppRules
            {
                DetectionIntent = DetectionDeploymentIntent.Install,
                DetectionRule = new IntuneDetectionRule
                {
                    RuleType = IntuneDetectionRuleType.MsiProductCode
                },
                DetectionProvenance =
                [
                    new DetectionFieldProvenance
                    {
                        FieldName = "ProductCode",
                        FieldValue = "{PRODUCT-CODE}",
                        Source = DetectionProvenanceSource.ManifestSource,
                        IsStrongEvidence = true,
                        Notes = "Product code populated from current MSI metadata."
                    }
                ]
            }
        }
    ];

    public Task<CuratedDetectionProfile?> FindBestMatchAsync(
        DetectionProfileQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query ??= new DetectionProfileQuery();
        var best = Profiles
            .Select(profile => (Profile: profile, Score: Score(profile, query)))
            .Where(item => item.Score >= 50)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Profile.ConfidenceScore)
            .Select(item => item.Profile)
            .FirstOrDefault();

        return Task.FromResult(best);
    }

    private static int Score(CuratedDetectionProfile profile, DetectionProfileQuery query)
    {
        var score = 0;
        var queryPackageId = (query.PackageId ?? string.Empty).Trim();
        var queryName = (query.Name ?? string.Empty).Trim();
        var queryPublisher = (query.Publisher ?? string.Empty).Trim();
        var queryVersion = (query.Version ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(queryPackageId))
        {
            if (queryPackageId.Equals(profile.PackageId, StringComparison.OrdinalIgnoreCase))
            {
                score += 70;
            }
            else if (queryPackageId.Contains(profile.PackageId, StringComparison.OrdinalIgnoreCase) ||
                     profile.PackageId.Contains(queryPackageId, StringComparison.OrdinalIgnoreCase))
            {
                score += 35;
            }
        }

        if (!string.IsNullOrWhiteSpace(queryName))
        {
            if (queryName.Equals(profile.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                score += 45;
            }
            else if (queryName.Contains(profile.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                     profile.DisplayName.Contains(queryName, StringComparison.OrdinalIgnoreCase))
            {
                score += 22;
            }
        }

        if (!string.IsNullOrWhiteSpace(queryPublisher))
        {
            if (queryPublisher.Equals(profile.Publisher, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }
            else if (queryPublisher.Contains(profile.Publisher, StringComparison.OrdinalIgnoreCase) ||
                     profile.Publisher.Contains(queryPublisher, StringComparison.OrdinalIgnoreCase))
            {
                score += 8;
            }
        }

        if (query.InstallerType != InstallerType.Unknown)
        {
            score += query.InstallerType == profile.InstallerType ? 25 : -20;
        }

        if (!string.IsNullOrWhiteSpace(queryVersion))
        {
            var regex = new Regex(profile.VersionPattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            score += regex.IsMatch(queryVersion) ? 10 : -5;
        }

        return score;
    }
}
