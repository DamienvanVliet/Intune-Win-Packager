using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface IIntuneImeFeedbackService
{
    Task<IReadOnlyList<ImeDetectionFeedback>> AnalyzeRecentDetectionFailuresAsync(
        string packageIdOrNameHint,
        CancellationToken cancellationToken = default);
}

