using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface IEvidenceScoringService
{
    EvidenceDecision Score(EvidenceScoringRequest request);
}
