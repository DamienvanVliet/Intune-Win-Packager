using IntuneWinPackager.Core.Interfaces;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class ToolLocatorService : IToolLocatorService
{
    public string? LocateToolPath()
    {
        var directMatch = GetCandidatePaths().FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(directMatch))
        {
            return directMatch;
        }

        foreach (var root in GetDeepSearchRoots())
        {
            var deepMatch = SafeEnumerateFiles(root, "IntuneWinAppUtil.exe")
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(deepMatch))
            {
                return deepMatch;
            }
        }

        return null;
    }

    public IReadOnlyList<string> GetCandidatePaths()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "IntuneWinAppUtil.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "IntuneWinAppUtil.exe"),
            Path.Combine(Environment.CurrentDirectory, "IntuneWinAppUtil.exe"),
            Path.Combine(Environment.CurrentDirectory, "tools", "IntuneWinAppUtil.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "IntuneWinAppUtil.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Intune Win32 Content Prep Tool", "IntuneWinAppUtil.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Intune Win32 Content Prep Tool", "IntuneWinAppUtil.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IntuneWinPackager", "Tools", "IntuneWinAppUtil.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "IntuneWinAppUtil.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WinGet", "Links", "IntuneWinAppUtil.exe")
        };

        candidates.AddRange(GetPathEnvironmentCandidates());

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> GetPathEnvironmentCandidates()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = pathValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            yield return Path.Combine(directory, "IntuneWinAppUtil.exe");
        }
    }

    private static IEnumerable<string> GetDeepSearchRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var roots = new[]
        {
            Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"),
            Path.Combine(localAppData, "Programs", "WinGet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };

        return roots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string fileName)
    {
        var pending = new Queue<string>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            string[] files;
            string[] directories;

            try
            {
                files = Directory.GetFiles(current, fileName, SearchOption.TopDirectoryOnly);
                directories = Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var directory in directories)
            {
                pending.Enqueue(directory);
            }
        }
    }
}
