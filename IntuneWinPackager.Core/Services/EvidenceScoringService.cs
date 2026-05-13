using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Core.Services;

public sealed class EvidenceScoringService : IEvidenceScoringService
{
    public EvidenceDecision Score(EvidenceScoringRequest request)
    {
        request ??= new EvidenceScoringRequest();
        var candidates = request.Candidates ?? [];
        if (candidates.Count == 0)
        {
            return new EvidenceDecision
            {
                Candidates = [],
                Summary = "No evidence candidates were provided."
            };
        }

        var anyProofAvailable = candidates.Any(candidate => candidate.ProofAvailable);
        var scored = candidates
            .Select(candidate => ScoreCandidate(candidate, request, anyProofAvailable))
            .OrderByDescending(candidate => StatusRank(candidate.Score.Status))
            .ThenByDescending(candidate => candidate.Score.Value)
            .ThenBy(candidate => DetectionRulePriority(candidate.Candidate.DetectionRule.RuleType))
            .ToList();

        var best = scored.FirstOrDefault(candidate => candidate.Score.Status != EvidenceDecisionStatus.Rejected);
        return new EvidenceDecision
        {
            BestCandidate = best,
            Candidates = scored,
            Summary = best is null
                ? "Evidence candidates were found, but none were safe to recommend."
                : $"{best.Candidate.DisplayName} selected from {best.Candidate.Source} evidence ({best.Score.Status}, {best.Score.Value}/100)."
        };
    }

    private static ScoredEvidenceCandidate ScoreCandidate(
        EvidenceCandidate candidate,
        EvidenceScoringRequest request,
        bool anyProofAvailable)
    {
        candidate ??= new EvidenceCandidate();
        var rejectionReason = ValidateCandidate(candidate, request);
        if (string.IsNullOrWhiteSpace(rejectionReason) &&
            request.PreferProvenWhenProofExists &&
            anyProofAvailable &&
            candidate.ProofAvailable &&
            !candidate.IsProven)
        {
            rejectionReason = "Proof was available, but this candidate did not pass proof validation.";
        }

        if (!string.IsNullOrWhiteSpace(rejectionReason))
        {
            return new ScoredEvidenceCandidate
            {
                Candidate = candidate,
                Score = new EvidenceScore
                {
                    Value = 0,
                    Status = EvidenceDecisionStatus.Rejected,
                    RejectionReason = rejectionReason,
                    RecommendedAction = "Do not apply automatically. Gather stronger evidence or choose a different rule.",
                    Factors = [rejectionReason]
                }
            };
        }

        var factors = new List<string>();
        var score = candidate.BaseScore > 0 ? Math.Clamp(candidate.BaseScore, 0, 100) : 45;
        AddFactor(factors, $"Base score {score}.");

        var sourceScore = SourceScore(candidate.Source);
        score += sourceScore;
        AddFactor(factors, $"{candidate.Source} source {FormatDelta(sourceScore)}.");

        var ruleScore = DetectionRuleScore(candidate.DetectionRule.RuleType);
        score += ruleScore;
        AddFactor(factors, $"{candidate.DetectionRule.RuleType} rule {FormatDelta(ruleScore)}.");

        if (candidate.ProofAvailable)
        {
            score += 10;
            AddFactor(factors, "Proof available +10.");
        }

        if (candidate.IsProven)
        {
            score += 25;
            AddFactor(factors, "Proof passed +25.");
        }

        var strongEvidenceCount = candidate.Provenance.Count(item => item.IsStrongEvidence);
        var weakEvidenceCount = candidate.Provenance.Count(item => !item.IsStrongEvidence);
        var provenanceScore = Math.Min(15, strongEvidenceCount * 5) + Math.Min(6, weakEvidenceCount * 2);
        if (provenanceScore > 0)
        {
            score += provenanceScore;
            AddFactor(factors, $"Provenance {FormatDelta(provenanceScore)}.");
        }

        var additionalRuleScore = Math.Min(5, candidate.AdditionalDetectionRules.Count * 2);
        if (additionalRuleScore > 0)
        {
            score += additionalRuleScore;
            AddFactor(factors, $"Additional identity rules {FormatDelta(additionalRuleScore)}.");
        }

        if (candidate.RequiresUserReview)
        {
            score -= 20;
            AddFactor(factors, "Requires review -20.");
        }

        if (candidate.Source == EvidenceSourceType.Heuristic)
        {
            score -= 20;
            AddFactor(factors, "Heuristic fallback -20.");
        }

        if (candidate.DetectionRule.RuleType == IntuneDetectionRuleType.Script &&
            candidate.Source is not EvidenceSourceType.UserConfirmed and not EvidenceSourceType.LocalProofStore &&
            !candidate.IsProven)
        {
            score -= 25;
            AddFactor(factors, "Unproven script detection -25.");
        }

        score = Math.Clamp(score, 0, 100);
        var status = ResolveStatus(score, candidate);
        return new ScoredEvidenceCandidate
        {
            Candidate = candidate,
            Score = new EvidenceScore
            {
                Value = score,
                Status = status,
                RecommendedAction = ResolveRecommendedAction(status),
                Factors = factors
            }
        };
    }

    private static string ValidateCandidate(EvidenceCandidate candidate, EvidenceScoringRequest request)
    {
        if (candidate.Kind != EvidenceCandidateKind.DetectionRule)
        {
            return "Only detection rule evidence can be scored by this service version.";
        }

        return candidate.DetectionRule.RuleType switch
        {
            IntuneDetectionRuleType.None => "Detection rule is missing.",
            IntuneDetectionRuleType.MsiProductCode => ValidateMsi(candidate, request),
            IntuneDetectionRuleType.File => ValidateFile(candidate),
            IntuneDetectionRuleType.Registry => ValidateRegistry(candidate),
            IntuneDetectionRuleType.Script => ValidateScript(candidate),
            _ => "Detection rule type is unknown."
        };
    }

    private static string ValidateMsi(EvidenceCandidate candidate, EvidenceScoringRequest request)
    {
        if (string.IsNullOrWhiteSpace(candidate.DetectionRule.Msi.ProductCode))
        {
            return "MSI product code is missing.";
        }

        var hasTrustedNonMsiProof = candidate.IsProven ||
                                    candidate.Source is EvidenceSourceType.UserConfirmed or EvidenceSourceType.LocalProofStore;
        if (request.InstallerType != InstallerType.Msi && !hasTrustedNonMsiProof)
        {
            return "MSI product code detection for a non-MSI installer requires sandbox proof or user-confirmed evidence.";
        }

        return string.Empty;
    }

    private static string ValidateFile(EvidenceCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.DetectionRule.File.Path))
        {
            return "File detection path is missing.";
        }

        return string.IsNullOrWhiteSpace(candidate.DetectionRule.File.FileOrFolderName)
            ? "File detection target name is missing."
            : string.Empty;
    }

    private static string ValidateRegistry(EvidenceCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.DetectionRule.Registry.Hive))
        {
            return "Registry hive is missing.";
        }

        if (string.IsNullOrWhiteSpace(candidate.DetectionRule.Registry.KeyPath))
        {
            return "Registry key path is missing.";
        }

        if (candidate.DetectionRule.Registry.Operator != IntuneDetectionOperator.Exists &&
            string.IsNullOrWhiteSpace(candidate.DetectionRule.Registry.ValueName))
        {
            return "Registry comparison value name is missing.";
        }

        return candidate.DetectionRule.Registry.Operator != IntuneDetectionOperator.Exists &&
               string.IsNullOrWhiteSpace(candidate.DetectionRule.Registry.Value)
            ? "Registry comparison value is missing."
            : string.Empty;
    }

    private static string ValidateScript(EvidenceCandidate candidate)
    {
        return string.IsNullOrWhiteSpace(candidate.DetectionRule.Script.ScriptBody)
            ? "Script detection body is missing."
            : string.Empty;
    }

    private static int SourceScore(EvidenceSourceType source)
    {
        return source switch
        {
            EvidenceSourceType.UserConfirmed => 35,
            EvidenceSourceType.LocalProofStore => 32,
            EvidenceSourceType.SandboxSnapshot => 25,
            EvidenceSourceType.InstallerMetadata => 15,
            EvidenceSourceType.SourceManifest => 10,
            EvidenceSourceType.ExecutionProbe => 8,
            EvidenceSourceType.Heuristic => 0,
            _ => 0
        };
    }

    private static int DetectionRuleScore(IntuneDetectionRuleType ruleType)
    {
        return ruleType switch
        {
            IntuneDetectionRuleType.MsiProductCode => 25,
            IntuneDetectionRuleType.Registry => 18,
            IntuneDetectionRuleType.File => 16,
            IntuneDetectionRuleType.Script => 4,
            _ => 0
        };
    }

    private static int DetectionRulePriority(IntuneDetectionRuleType ruleType)
    {
        return ruleType switch
        {
            IntuneDetectionRuleType.MsiProductCode => 0,
            IntuneDetectionRuleType.Registry => 1,
            IntuneDetectionRuleType.File => 2,
            IntuneDetectionRuleType.Script => 3,
            _ => 99
        };
    }

    private static EvidenceDecisionStatus ResolveStatus(int score, EvidenceCandidate candidate)
    {
        if ((candidate.IsProven ||
             candidate.Source is EvidenceSourceType.UserConfirmed or EvidenceSourceType.LocalProofStore) &&
            score >= 80)
        {
            return EvidenceDecisionStatus.Proven;
        }

        if (score >= 80)
        {
            return EvidenceDecisionStatus.Recommended;
        }

        return score >= 45
            ? EvidenceDecisionStatus.NeedsReview
            : EvidenceDecisionStatus.Rejected;
    }

    private static int StatusRank(EvidenceDecisionStatus status)
    {
        return status switch
        {
            EvidenceDecisionStatus.Proven => 3,
            EvidenceDecisionStatus.Recommended => 2,
            EvidenceDecisionStatus.NeedsReview => 1,
            _ => 0
        };
    }

    private static string ResolveRecommendedAction(EvidenceDecisionStatus status)
    {
        return status switch
        {
            EvidenceDecisionStatus.Proven => "Safe to apply automatically when the request context still matches.",
            EvidenceDecisionStatus.Recommended => "Suggest to the user and allow sandbox proof to confirm.",
            EvidenceDecisionStatus.NeedsReview => "Show as a candidate, but require review or stronger proof.",
            _ => "Do not use without stronger evidence."
        };
    }

    private static void AddFactor(ICollection<string> factors, string factor)
    {
        if (!string.IsNullOrWhiteSpace(factor))
        {
            factors.Add(factor);
        }
    }

    private static string FormatDelta(int value)
    {
        return value >= 0 ? $"+{value}" : value.ToString();
    }
}
