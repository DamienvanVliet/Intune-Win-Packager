using System.IO;

namespace IntuneWinPackager.Infrastructure.Support;

internal static class DataPathProvider
{
    private const string AppDataFolderName = "IntuneWinPackager";

    public static string BaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppDataFolderName);

    public static string SettingsFilePath => Path.Combine(BaseDirectory, "settings.json");

    public static string ProfilesFilePath => Path.Combine(BaseDirectory, "profiles.json");

    public static string HistoryFilePath => Path.Combine(BaseDirectory, "history.json");

    public static void EnsureBaseDirectory()
    {
        Directory.CreateDirectory(BaseDirectory);
    }
}
