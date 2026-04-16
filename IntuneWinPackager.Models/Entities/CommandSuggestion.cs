using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record CommandSuggestion
{
    public string InstallCommand { get; init; } = string.Empty;

    public string UninstallCommand { get; init; } = string.Empty;

    public IntuneWin32AppRules SuggestedRules { get; init; } = new();

    public SuggestionConfidenceLevel ConfidenceLevel { get; init; } = SuggestionConfidenceLevel.Low;

    public int ConfidenceScore { get; init; }

    public string ConfidenceReason { get; init; } = string.Empty;

    public string FingerprintEngine { get; init; } = string.Empty;

    public bool UsedKnowledgeCache { get; init; }
}
