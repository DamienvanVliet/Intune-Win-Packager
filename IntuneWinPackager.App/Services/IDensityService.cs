namespace IntuneWinPackager.App.Services;

public interface IDensityService
{
    event EventHandler? DensityChanged;

    IReadOnlyList<string> DensityOptions { get; }

    string CurrentDensity { get; }

    string CurrentDensityCode { get; }

    string NormalizeDensity(string? density);

    string DisplayNameFromCode(string? densityCode);

    void SetDensity(string density);
}
