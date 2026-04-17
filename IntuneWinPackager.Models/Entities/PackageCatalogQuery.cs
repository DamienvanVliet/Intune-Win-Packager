namespace IntuneWinPackager.Models.Entities;

public sealed record PackageCatalogQuery
{
    public string SearchTerm { get; init; } = string.Empty;

    public int MaxResults { get; init; } = 24;

    public bool IncludeWinget { get; init; } = true;

    public bool IncludeChocolatey { get; init; } = true;

    public bool IncludeGitHubReleases { get; init; }
}
