namespace IntuneWinPackager.Models.Entities;

public sealed record PackagingProgressUpdate
{
    public double Percentage { get; init; }

    public bool IsIndeterminate { get; init; }

    public string Step { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;
}
