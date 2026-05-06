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

    public static string UpdatesDirectory => Path.Combine(BaseDirectory, "updates");

    public static string CatalogDownloadsDirectory => Path.Combine(BaseDirectory, "catalog-downloads");

    public static string CatalogProfilesFilePath => Path.Combine(BaseDirectory, "catalog-profiles.v1.json");

    public static string CatalogDatabaseFilePath => Path.Combine(BaseDirectory, "catalog-store.v1.db");

    public static string CatalogIconsDirectory => Path.Combine(BaseDirectory, "catalog-icons");

    public static string SandboxProofRunsDirectory => Path.Combine(BaseDirectory, "sandbox-proof", "runs");

    public static void EnsureBaseDirectory()
    {
        Directory.CreateDirectory(BaseDirectory);
    }
}
