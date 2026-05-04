namespace IntuneWinPackager.Models.Entities;

public sealed record ImeDetectionFeedback
{
    public string Signal { get; init; } = string.Empty;

    public string Recommendation { get; init; } = string.Empty;

    public int ConfidenceScore { get; init; }
}

