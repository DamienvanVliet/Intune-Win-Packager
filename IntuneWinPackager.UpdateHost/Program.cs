using System.Diagnostics;
using System.Globalization;

const int ParentWaitTimeoutMs = 30_000;
const int ParentWaitPollMs = 200;
const int PostExitGraceMs = 450;

var options = ParseOptions(args);
if (options is null)
{
    TryWriteLog(null, "Update host parse error: missing required arguments.");
    return 2;
}

TryWriteLog(options.LaunchLogPath, "Update host started.");
TryCreateMarker(options.LaunchMarkerPath, options.LaunchLogPath);

WaitForParentExit(options.ParentPid, options.LaunchLogPath);

if (!string.IsNullOrWhiteSpace(options.TargetExePath))
{
    TryWriteLog(options.LaunchLogPath, $"Skipping explicit file-lock probing for faster handoff: {options.TargetExePath}");
}

if (!File.Exists(options.InstallerPath))
{
    TryWriteLog(options.LaunchLogPath, $"Installer not found: {options.InstallerPath}");
    return 3;
}

try
{
    var startInfo = new ProcessStartInfo
    {
        FileName = options.InstallerPath,
        Arguments = options.InstallerArguments,
        UseShellExecute = true,
        WorkingDirectory = Path.GetDirectoryName(options.InstallerPath) ?? Environment.CurrentDirectory
    };

    var installerProcess = Process.Start(startInfo);
    if (installerProcess is null)
    {
        TryWriteLog(options.LaunchLogPath, "Installer process could not be started.");
        return 4;
    }

    TryWriteLog(options.LaunchLogPath, $"Installer launched (PID={installerProcess.Id}).");
    return 0;
}
catch (Exception ex)
{
    TryWriteLog(options.LaunchLogPath, $"Installer launch error: {ex}");
    return 5;
}

static void WaitForParentExit(int parentPid, string launchLogPath)
{
    if (parentPid <= 0)
    {
        return;
    }

    var stopwatch = Stopwatch.StartNew();
    while (stopwatch.ElapsedMilliseconds < ParentWaitTimeoutMs)
    {
        if (!IsProcessAlive(parentPid))
        {
            TryWriteLog(launchLogPath, $"Parent process exited (PID={parentPid}).");
            Thread.Sleep(PostExitGraceMs);
            return;
        }

        Thread.Sleep(ParentWaitPollMs);
    }

    TryWriteLog(launchLogPath, $"Parent process wait timeout reached (PID={parentPid}). Continuing.");
}

static bool IsProcessAlive(int processId)
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

static void TryCreateMarker(string markerPath, string launchLogPath)
{
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
        TryWriteLog(launchLogPath, $"Marker write failed: {ex.Message}");
    }
}

static void TryWriteLog(string? logPath, string message)
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

static UpdateHostOptions? ParseOptions(IReadOnlyList<string> args)
{
    var values = ParseArgumentMap(args);

    if (!TryGetValue(values, "--parent-pid", out var parentPidRaw) ||
        !int.TryParse(parentPidRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentPid))
    {
        return null;
    }

    if (!TryGetValue(values, "--target-exe-path", out var targetExePath) || string.IsNullOrWhiteSpace(targetExePath))
    {
        return null;
    }

    if (!TryGetValue(values, "--installer-path", out var installerPath) || string.IsNullOrWhiteSpace(installerPath))
    {
        return null;
    }

    if (!TryGetValue(values, "--installer-arguments", out var installerArguments))
    {
        installerArguments = string.Empty;
    }

    if (!TryGetValue(values, "--launch-marker-path", out var launchMarkerPath) || string.IsNullOrWhiteSpace(launchMarkerPath))
    {
        return null;
    }

    if (!TryGetValue(values, "--launch-log-path", out var launchLogPath) || string.IsNullOrWhiteSpace(launchLogPath))
    {
        return null;
    }

    return new UpdateHostOptions(
        parentPid,
        targetExePath,
        installerPath,
        installerArguments ?? string.Empty,
        launchMarkerPath,
        launchLogPath);
}

static Dictionary<string, string> ParseArgumentMap(IReadOnlyList<string> args)
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

        if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            values[arg] = args[index + 1];
            index++;
        }
        else
        {
            values[arg] = string.Empty;
        }
    }

    return values;
}

static bool TryGetValue(IReadOnlyDictionary<string, string> values, string key, out string? value)
{
    if (values.TryGetValue(key, out var parsed))
    {
        value = parsed;
        return true;
    }

    value = null;
    return false;
}

sealed record UpdateHostOptions(
    int ParentPid,
    string TargetExePath,
    string InstallerPath,
    string InstallerArguments,
    string LaunchMarkerPath,
    string LaunchLogPath);
