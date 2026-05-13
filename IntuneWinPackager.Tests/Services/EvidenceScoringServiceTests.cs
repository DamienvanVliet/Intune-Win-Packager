using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Tests.Services;

public sealed class EvidenceScoringServiceTests
{
    [Fact]
    public void Score_PrefersProvenSandboxCandidate_WhenProofExists()
    {
        var sut = new EvidenceScoringService();

        var decision = sut.Score(new EvidenceScoringRequest
        {
            InstallerType = InstallerType.Exe,
            Candidates =
            [
                new EvidenceCandidate
                {
                    CandidateId = "heuristic-registry",
                    DisplayName = "Heuristic registry",
                    Kind = EvidenceCandidateKind.DetectionRule,
                    Source = EvidenceSourceType.Heuristic,
                    BaseScore = 95,
                    ProofAvailable = false,
                    DetectionRule = RegistryRule("DisplayVersion", "1.0.0")
                },
                new EvidenceCandidate
                {
                    CandidateId = "sandbox-file",
                    DisplayName = "Sandbox file",
                    Kind = EvidenceCandidateKind.DetectionRule,
                    Source = EvidenceSourceType.SandboxSnapshot,
                    BaseScore = 70,
                    ProofAvailable = true,
                    IsProven = true,
                    DetectionRule = FileRule(),
                    Provenance =
                    [
                        new DetectionFieldProvenance
                        {
                            FieldName = "FileDetectionTarget",
                            FieldValue = @"C:\Program Files\Vendor\App.exe",
                            Source = DetectionProvenanceSource.LocalUninstallRegistry,
                            IsStrongEvidence = true
                        }
                    ]
                }
            ]
        });

        Assert.NotNull(decision.BestCandidate);
        Assert.Equal("sandbox-file", decision.BestCandidate!.Candidate.CandidateId);
        Assert.Equal(EvidenceDecisionStatus.Proven, decision.BestCandidate.Score.Status);
    }

    [Fact]
    public void Score_RejectsMsiMetadata_ForExeInstallerWithoutProof()
    {
        var sut = new EvidenceScoringService();

        var decision = sut.Score(new EvidenceScoringRequest
        {
            InstallerType = InstallerType.Exe,
            Candidates =
            [
                new EvidenceCandidate
                {
                    CandidateId = "msi-for-exe",
                    DisplayName = "MSI ProductCode",
                    Kind = EvidenceCandidateKind.DetectionRule,
                    Source = EvidenceSourceType.InstallerMetadata,
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.MsiProductCode,
                        Msi = new MsiDetectionRule
                        {
                            ProductCode = "{11111111-2222-3333-4444-555555555555}"
                        }
                    }
                }
            ]
        });

        Assert.Null(decision.BestCandidate);
        var scored = Assert.Single(decision.Candidates);
        Assert.Equal(EvidenceDecisionStatus.Rejected, scored.Score.Status);
        Assert.Contains("requires sandbox proof", scored.Score.RejectionReason);
    }

    [Fact]
    public void Score_AcceptsProvenMsiProductCode_ForExeWrapperInstall()
    {
        var sut = new EvidenceScoringService();

        var decision = sut.Score(new EvidenceScoringRequest
        {
            InstallerType = InstallerType.Exe,
            Candidates =
            [
                new EvidenceCandidate
                {
                    CandidateId = "proven-msi-for-exe",
                    DisplayName = "Proven MSI ProductCode",
                    Kind = EvidenceCandidateKind.DetectionRule,
                    Source = EvidenceSourceType.SandboxSnapshot,
                    BaseScore = 70,
                    ProofAvailable = true,
                    IsProven = true,
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.MsiProductCode,
                        Msi = new MsiDetectionRule
                        {
                            ProductCode = "{11111111-2222-3333-4444-555555555555}"
                        }
                    }
                }
            ]
        });

        Assert.NotNull(decision.BestCandidate);
        Assert.Equal("proven-msi-for-exe", decision.BestCandidate!.Candidate.CandidateId);
        Assert.Equal(EvidenceDecisionStatus.Proven, decision.BestCandidate.Score.Status);
    }

    [Fact]
    public void Score_RecommendsMsiProductCode_ForMsiInstallerMetadata()
    {
        var sut = new EvidenceScoringService();

        var decision = sut.Score(new EvidenceScoringRequest
        {
            InstallerType = InstallerType.Msi,
            Candidates =
            [
                new EvidenceCandidate
                {
                    CandidateId = "msi-metadata",
                    DisplayName = "MSI metadata",
                    Kind = EvidenceCandidateKind.DetectionRule,
                    Source = EvidenceSourceType.InstallerMetadata,
                    BaseScore = 60,
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.MsiProductCode,
                        Msi = new MsiDetectionRule
                        {
                            ProductCode = "{11111111-2222-3333-4444-555555555555}"
                        }
                    },
                    Provenance =
                    [
                        new DetectionFieldProvenance
                        {
                            FieldName = "ProductCode",
                            FieldValue = "{11111111-2222-3333-4444-555555555555}",
                            Source = DetectionProvenanceSource.InstallerMetadata,
                            IsStrongEvidence = true
                        }
                    ]
                }
            ]
        });

        Assert.NotNull(decision.BestCandidate);
        Assert.Equal("msi-metadata", decision.BestCandidate!.Candidate.CandidateId);
        Assert.Equal(EvidenceDecisionStatus.Recommended, decision.BestCandidate.Score.Status);
    }

    [Fact]
    public void Score_KeepsUnprovenScriptAsReviewCandidate()
    {
        var sut = new EvidenceScoringService();

        var decision = sut.Score(new EvidenceScoringRequest
        {
            InstallerType = InstallerType.Exe,
            Candidates =
            [
                new EvidenceCandidate
                {
                    CandidateId = "script",
                    DisplayName = "Script fallback",
                    Kind = EvidenceCandidateKind.DetectionRule,
                    Source = EvidenceSourceType.SourceManifest,
                    BaseScore = 80,
                    RequiresUserReview = true,
                    DetectionRule = new IntuneDetectionRule
                    {
                        RuleType = IntuneDetectionRuleType.Script,
                        Script = new ScriptDetectionRule
                        {
                            ScriptBody = "Write-Output 'Detected'; exit 0"
                        }
                    }
                }
            ]
        });

        Assert.NotNull(decision.BestCandidate);
        Assert.Equal(EvidenceDecisionStatus.NeedsReview, decision.BestCandidate!.Score.Status);
        Assert.Contains("Require", string.Join(" ", decision.BestCandidate.Score.Factors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Score_ReturnsNoBestCandidate_WhenInputIsEmpty()
    {
        var sut = new EvidenceScoringService();

        var decision = sut.Score(new EvidenceScoringRequest());

        Assert.False(decision.HasRecommendedCandidate);
        Assert.Empty(decision.Candidates);
    }

    private static IntuneDetectionRule FileRule()
    {
        return new IntuneDetectionRule
        {
            RuleType = IntuneDetectionRuleType.File,
            File = new FileDetectionRule
            {
                Path = @"C:\Program Files\Vendor",
                FileOrFolderName = "App.exe",
                Operator = IntuneDetectionOperator.Exists
            }
        };
    }

    private static IntuneDetectionRule RegistryRule(string valueName, string value)
    {
        return new IntuneDetectionRule
        {
            RuleType = IntuneDetectionRuleType.Registry,
            Registry = new RegistryDetectionRule
            {
                Hive = "HKEY_LOCAL_MACHINE",
                KeyPath = @"SOFTWARE\Vendor\App",
                ValueName = valueName,
                Operator = IntuneDetectionOperator.Equals,
                Value = value
            }
        };
    }
}
