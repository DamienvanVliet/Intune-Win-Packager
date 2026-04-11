namespace IntuneWinPackager.Models.Entities;

public sealed record PackageConfiguration
{
    public string SourceFolder { get; init; } = string.Empty;

    public string SetupFilePath { get; init; } = string.Empty;

    public string OutputFolder { get; init; } = string.Empty;

    public string InstallCommand { get; init; } = string.Empty;

    public string UninstallCommand { get; init; } = string.Empty;
}
