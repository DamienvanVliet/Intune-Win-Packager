namespace IntuneWinPackager.App.Services;

public sealed class DensityService : IDensityService
{
    private const string DefaultDensityCode = "comfortable";

    private static readonly Dictionary<string, string> DisplayToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Comfortable"] = "comfortable"
    };

    private static readonly Dictionary<string, string> CodeToDisplay = new(StringComparer.OrdinalIgnoreCase)
    {
        ["comfortable"] = "Comfortable"
    };

    private static readonly Dictionary<string, string> CodeToResourceSource = new(StringComparer.OrdinalIgnoreCase)
    {
        ["comfortable"] = "Styles/Density.Comfortable.xaml"
    };

    public event EventHandler? DensityChanged;

    public IReadOnlyList<string> DensityOptions { get; } = ["Comfortable"];

    public string CurrentDensity { get; private set; } = "Comfortable";

    public string CurrentDensityCode { get; private set; } = DefaultDensityCode;

    public string NormalizeDensity(string? density)
    {
        if (!string.IsNullOrWhiteSpace(density) &&
            DisplayToCode.ContainsKey(density.Trim()))
        {
            return density.Trim();
        }

        return DisplayNameFromCode(DefaultDensityCode);
    }

    public string DisplayNameFromCode(string? densityCode)
    {
        if (!string.IsNullOrWhiteSpace(densityCode) &&
            CodeToDisplay.TryGetValue(densityCode.Trim(), out var displayName))
        {
            return displayName;
        }

        return CodeToDisplay[DefaultDensityCode];
    }

    public void SetDensity(string density)
    {
        var normalizedDisplay = NormalizeDensity(density);
        var normalizedCode = DisplayToCode[normalizedDisplay];

        if (string.Equals(normalizedCode, CurrentDensityCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SwapDensityDictionary(normalizedCode);

        CurrentDensity = normalizedDisplay;
        CurrentDensityCode = normalizedCode;
        DensityChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SwapDensityDictionary(string densityCode)
    {
        if (!CodeToResourceSource.TryGetValue(densityCode, out var source))
        {
            source = CodeToResourceSource[DefaultDensityCode];
        }

        var appResources = System.Windows.Application.Current.Resources.MergedDictionaries;
        var existing = appResources.FirstOrDefault(dictionary =>
            dictionary.Source is not null &&
            dictionary.Source.OriginalString.Contains("Styles/Density.", StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            appResources.Remove(existing);
        }

        appResources.Add(new System.Windows.ResourceDictionary
        {
            Source = new Uri(source, UriKind.Relative)
        });
    }
}
