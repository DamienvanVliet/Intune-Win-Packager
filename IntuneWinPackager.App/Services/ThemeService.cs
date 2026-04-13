namespace IntuneWinPackager.App.Services;

public sealed class ThemeService : IThemeService
{
    private const string DefaultThemeCode = "light";

    private static readonly Dictionary<string, string> DisplayToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Light"] = "light",
        ["Dark"] = "dark"
    };

    private static readonly Dictionary<string, string> CodeToDisplay = new(StringComparer.OrdinalIgnoreCase)
    {
        ["light"] = "Light",
        ["dark"] = "Dark"
    };

    private static readonly Dictionary<string, string> CodeToResourceSource = new(StringComparer.OrdinalIgnoreCase)
    {
        ["light"] = "Styles/Colors.Light.xaml",
        ["dark"] = "Styles/Colors.Dark.xaml"
    };

    public event EventHandler? ThemeChanged;

    public IReadOnlyList<string> ThemeOptions { get; } = ["Light", "Dark"];

    public string CurrentTheme { get; private set; } = "Light";

    public string CurrentThemeCode { get; private set; } = DefaultThemeCode;

    public string NormalizeTheme(string? theme)
    {
        if (!string.IsNullOrWhiteSpace(theme) &&
            DisplayToCode.ContainsKey(theme.Trim()))
        {
            return theme.Trim();
        }

        return DisplayNameFromCode(DefaultThemeCode);
    }

    public string DisplayNameFromCode(string? themeCode)
    {
        if (!string.IsNullOrWhiteSpace(themeCode) &&
            CodeToDisplay.TryGetValue(themeCode.Trim(), out var displayName))
        {
            return displayName;
        }

        return CodeToDisplay[DefaultThemeCode];
    }

    public void SetTheme(string theme)
    {
        var normalizedDisplay = NormalizeTheme(theme);
        var normalizedCode = DisplayToCode[normalizedDisplay];

        if (string.Equals(normalizedCode, CurrentThemeCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SwapThemeDictionary(normalizedCode);

        CurrentTheme = normalizedDisplay;
        CurrentThemeCode = normalizedCode;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SwapThemeDictionary(string themeCode)
    {
        if (!CodeToResourceSource.TryGetValue(themeCode, out var source))
        {
            source = CodeToResourceSource[DefaultThemeCode];
        }

        var appResources = System.Windows.Application.Current.Resources.MergedDictionaries;
        var existing = appResources.FirstOrDefault(dictionary =>
            dictionary.Source is not null &&
            dictionary.Source.OriginalString.Contains("Styles/Colors.", StringComparison.OrdinalIgnoreCase));

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
