using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record CatalogReadinessEvaluation
{
    public CatalogReadinessState State { get; init; } = CatalogReadinessState.NeedsReview;

    public bool HasFactualInstallerSource { get; init; }

    public bool HasFactualDetectionRule { get; init; }

    public bool HasUnresolvedCommandPlaceholders { get; init; }

    public bool HasLocalInstaller { get; init; }

    public string Summary { get; init; } = string.Empty;
}
