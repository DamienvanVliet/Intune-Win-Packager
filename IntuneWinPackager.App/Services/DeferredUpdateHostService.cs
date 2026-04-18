using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace IntuneWinPackager.App.Services;

internal static class DeferredUpdateHostService
{
    private const string DeferredModeFlag = "--deferred-update";
    private const int MaxWaitSeconds = 240;

    public static bool TryHandleStartup(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args is null || args.Length == 0 || !args.Contains(DeferredModeFlag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        exitCode = RunDeferredUpdate(args);
        return true;
    }

    private static int RunDeferredUpdate(string[] args)
    {
        if (!TryParseOptions(args, out var options, out var parseError))
        {
            TryWriteLog(null, $"Deferred updater parse error: {parseError}");
            return 2;
        }

        TryWriteLog(options.LaunchLogPath, "Deferred updater host started.");
        TryCreateMarker(options.LaunchMarkerPath, options.LaunchLogPath);

        WaitForParentExit(options.ParentPid, options.LaunchLogPath);
        WaitForFileUnlock(options.TargetExePath, options.LaunchLogPath);

        if (string.IsNullOrWhiteSpace(options.InstallerPath) || !File.Exists(options.InstallerPath))
        {
            TryWriteLog(options.LaunchLogPath, $"Installer file missing: {options.InstallerPath}");
            return 3;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = options.InstallerPath,
                Arguments = options.InstallerArguments ?? string.Empty,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(options.InstallerPath) ?? Environment.CurrentDirectory
            };

            var installerProcess = Process.Start(startInfo);
            if (installerProcess is null)
            {
                TryWriteLog(options.LaunchLogPath, "Installer process could not be started.");
                return 4;
            }

            TryWriteLog(options.LaunchLogPath, $"Installer launched. PID={installerProcess.Id}.");
            return 0;
        }
        catch (Exception ex)
        {
            TryWriteLog(options.LaunchLogPath, $"Installer launch failed: {ex}");
            return 5;
        }
    }

    private static void WaitForParentExit(int parentPid, string? logPath)
    {
        if (parentPid <= 0)
        {
            return;
        }

        for (var attempt = 0; attempt < MaxWaitSeconds; attempt++)
        {
            if (!IsProcessAlive(parentPid))
            {
                TryWriteLog(logPath, $"Parent process {parentPid} exited.");
                return;
            }

            Thread.Sleep(1000);
        }

        TryWriteLog(logPath, $"Parent process {parentPid} did not exit within timeout. Continuing.");
    }

    private static void WaitForFileUnlock(string? path, string? logPath)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var targetPath = path;
        for (var attempt = 0; attempt < MaxWaitSeconds; attempt++)
        {
            if (TryOpenExclusive(targetPath))
            {
                TryWriteLog(logPath, $"File unlock confirmed: {targetPath}");
                return;
            }

            Thread.Sleep(1000);
        }

        TryWriteLog(logPath, $"File unlock timeout reached for: {targetPath}. Continuing.");
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryOpenExclusive(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryCreateMarker(string? markerPath, string? logPath)
    {
        if (string.IsNullOrWhiteSpace(markerPath))
        {
            return;
        }

        try
        {
            var markerDirectory = Path.GetDirectoryName(markerPath);
            if (!string.IsNullOrWhiteSpace(markerDirectory))
            {
                Directory.CreateDirectory(markerDirectory);
            }

            File.WriteAllText(markerPath, $"started={DateTimeOffset.UtcNow:O}");
        }
        catch (Exception ex)
        {
            TryWriteLog(logPath, $"Could not create launch marker file: {ex.Message}");
        }
    }

    private static void TryWriteLog(string? logPath, string message)
    {
        try
        {
            var baseDirectory = string.IsNullOrWhiteSpace(logPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntuneWinPackager", "updates")
                : Path.GetDirectoryName(logPath) ?? Environment.CurrentDirectory;
            Directory.CreateDirectory(baseDirectory);

            var effectivePath = string.IsNullOrWhiteSpace(logPath)
                ? Path.Combine(baseDirectory, $"launch-host-fallback-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log")
                : logPath;

            var line = $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}";
            File.AppendAllText(effectivePath, line);
        }
        catch
        {
            // Logging should never break update flow.
        }
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> args,
        out DeferredUpdateOptions options,
        out string error)
    {
        options = DeferredUpdateOptions.Empty;
        error = string.Empty;

        var values = ParseArguments(args);
        if (!TryGetValue(values, "--parent-pid", out var parentPidRaw) ||
            !int.TryParse(parentPidRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentPid))
        {
            error = "Missing or invalid --parent-pid argument.";
            return false;
        }

        if (!TryGetValue(values, "--installer-path", out var installerPath) || string.IsNullOrWhiteSpace(installerPath))
        {
            error = "Missing --installer-path argument.";
            return false;
        }

        TryGetValue(values, "--target-exe-path", out var targetExePath);
        TryGetValue(values, "--installer-arguments", out var installerArguments);
        TryGetValue(values, "--launch-marker-path", out var launchMarkerPath);
        TryGetValue(values, "--launch-log-path", out var launchLogPath);

        options = new DeferredUpdateOptions(
            parentPid,
            targetExePath ?? string.Empty,
            installerPath,
            installerArguments ?? string.Empty,
            launchMarkerPath ?? string.Empty,
            launchLogPath ?? string.Empty);
        return true;
    }

    private static Dictionary<string, string> ParseArguments(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var separatorIndex = arg.IndexOf('=');
            if (separatorIndex > 2)
            {
                var key = arg[..separatorIndex];
                var value = separatorIndex + 1 < arg.Length ? arg[(separatorIndex + 1)..] : string.Empty;
                values[key] = value;
                continue;
            }

            var keyOnly = arg;
            if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[keyOnly] = args[index + 1];
                index++;
            }
            else
            {
                values[keyOnly] = string.Empty;
            }
        }

        return values;
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, string> values, string key, out string? value)
    {
        if (values.TryGetValue(key, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    private sealed record DeferredUpdateOptions(
        int ParentPid,
        string TargetExePath,
        string InstallerPath,
        string InstallerArguments,
        string LaunchMarkerPath,
        string LaunchLogPath)
    {
        public static DeferredUpdateOptions Empty => new(0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    }
}
