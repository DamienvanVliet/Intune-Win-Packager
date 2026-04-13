namespace IntuneWinPackager.App.Services;

public interface ILocalizationService
{
    event EventHandler? LanguageChanged;

    IReadOnlyList<string> LanguageOptions { get; }

    string CurrentLanguage { get; }

    string CurrentLanguageCode { get; }

    string NormalizeLanguage(string? language);

    string DisplayNameFromCode(string? languageCode);

    string Translate(string key);

    void SetLanguage(string language);
}
