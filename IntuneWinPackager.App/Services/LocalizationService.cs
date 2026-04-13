namespace IntuneWinPackager.App.Services;

public sealed class LocalizationService : ILocalizationService
{
    private const string DefaultLanguageCode = "en";

    private static readonly Dictionary<string, string> DisplayToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "en",
        ["Nederlands"] = "nl"
    };

    private static readonly Dictionary<string, string> CodeToDisplay = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English",
        ["nl"] = "Nederlands"
    };

    private static readonly Dictionary<string, string> CodeToResourceSource = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "Localization/Strings.en.xaml",
        ["nl"] = "Localization/Strings.nl.xaml"
    };

    public event EventHandler? LanguageChanged;

    public IReadOnlyList<string> LanguageOptions { get; } = ["English", "Nederlands"];

    public string CurrentLanguage { get; private set; } = "English";

    public string CurrentLanguageCode { get; private set; } = DefaultLanguageCode;

    public string NormalizeLanguage(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language) &&
            DisplayToCode.ContainsKey(language.Trim()))
        {
            return language.Trim();
        }

        return DisplayNameFromCode(DefaultLanguageCode);
    }

    public string DisplayNameFromCode(string? languageCode)
    {
        if (!string.IsNullOrWhiteSpace(languageCode) &&
            CodeToDisplay.TryGetValue(languageCode.Trim(), out var displayName))
        {
            return displayName;
        }

        return CodeToDisplay[DefaultLanguageCode];
    }

    public string Translate(string key)
    {
        if (System.Windows.Application.Current.TryFindResource(key) is string translated &&
            !string.IsNullOrWhiteSpace(translated))
        {
            return translated;
        }

        return key;
    }

    public void SetLanguage(string language)
    {
        var normalizedDisplay = NormalizeLanguage(language);
        var normalizedCode = DisplayToCode[normalizedDisplay];

        if (string.Equals(normalizedCode, CurrentLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SwapLanguageDictionary(normalizedCode);

        CurrentLanguage = normalizedDisplay;
        CurrentLanguageCode = normalizedCode;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SwapLanguageDictionary(string languageCode)
    {
        if (!CodeToResourceSource.TryGetValue(languageCode, out var source))
        {
            source = CodeToResourceSource[DefaultLanguageCode];
        }

        var appResources = System.Windows.Application.Current.Resources.MergedDictionaries;
        var existing = appResources.FirstOrDefault(dictionary =>
            dictionary.Source is not null &&
            dictionary.Source.OriginalString.Contains("Localization/Strings.", StringComparison.OrdinalIgnoreCase));

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
