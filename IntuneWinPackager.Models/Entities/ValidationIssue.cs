namespace IntuneWinPackager.Models.Entities;

public sealed record ValidationIssue
{
    public string Key { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
