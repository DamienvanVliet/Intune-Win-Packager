namespace IntuneWinPackager.Models.Entities;

public sealed record CommandSuggestion
{
    public string InstallCommand { get; init; } = string.Empty;

    public string UninstallCommand { get; init; } = string.Empty;
}
