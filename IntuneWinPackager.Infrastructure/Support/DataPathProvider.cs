using System.IO;

namespace IntuneWinPackager.Infrastructure.Support;

public static class DataPathProvider
{
    private const string AppDataFolderName = "IntuneWinPackager";
    private static string? workspaceRoot;

    public static string BaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppDataFolderName);

    public static string SettingsFilePath => Path.Combine(BaseDirectory, "settings.json");

    public static string ProfilesFilePath => Path.Combine(BaseDirectory, "profiles.json");

    public static string HistoryFilePath => Path.Combine(BaseDirectory, "history.json");

    public static string WorkspaceDirectory => string.IsNullOrWhiteSpace(workspaceRoot)
        ? BaseDirectory
        : workspaceRoot;

    public static string UpdatesDirectory => Path.Combine(WorkspaceDirectory, "updates");

    public static string CatalogDownloadsDirectory => Path.Combine(WorkspaceDirectory, "catalog-downloads");

    public static string CatalogProfilesFilePath => Path.Combine(BaseDirectory, "catalog-profiles.v1.json");

    public static string CatalogDatabaseFilePath => Path.Combine(BaseDirectory, "catalog-store.v1.db");

    public static string CatalogIconsDirectory => Path.Combine(WorkspaceDirectory, "catalog-icons");

    public static string SandboxProofRunsDirectory => Path.Combine(WorkspaceDirectory, "sandbox-proof", "runs");

    public static void ConfigureWorkspaceRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            workspaceRoot = null;
            return;
        }

        try
        {
            workspaceRoot = Path.GetFullPath(path);
        }
        catch
        {
            workspaceRoot = null;
        }
    }

    public static void EnsureBaseDirectory()
    {
        Directory.CreateDirectory(BaseDirectory);
    }

    public static void EnsureWorkspaceDirectories()
    {
        Directory.CreateDirectory(WorkspaceDirectory);
        Directory.CreateDirectory(UpdatesDirectory);
        Directory.CreateDirectory(CatalogDownloadsDirectory);
        Directory.CreateDirectory(CatalogIconsDirectory);
        Directory.CreateDirectory(SandboxProofRunsDirectory);
    }
}
