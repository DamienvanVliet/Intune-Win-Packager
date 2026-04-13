namespace IntuneWinPackager.App.Services;

public interface IThemeService
{
    event EventHandler? ThemeChanged;

    IReadOnlyList<string> ThemeOptions { get; }

    string CurrentTheme { get; }

    string CurrentThemeCode { get; }

    string NormalizeTheme(string? theme);

    string DisplayNameFromCode(string? themeCode);

    void SetTheme(string theme);
}
