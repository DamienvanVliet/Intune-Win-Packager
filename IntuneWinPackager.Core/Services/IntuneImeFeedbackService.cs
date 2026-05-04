using System.Text.RegularExpressions;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Services;

public sealed class IntuneImeFeedbackService : IIntuneImeFeedbackService
{
    private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private readonly string _logsDirectory;

    public IntuneImeFeedbackService(string? logsDirectory = null)
    {
        _logsDirectory = string.IsNullOrWhiteSpace(logsDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft",
                "IntuneManagementExtension",
                "Logs")
            : logsDirectory;
    }

    public async Task<IReadOnlyList<ImeDetectionFeedback>> AnalyzeRecentDetectionFailuresAsync(
        string packageIdOrNameHint,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_logsDirectory))
        {
            return
            [
                new ImeDetectionFeedback
                {
                    Signal = "IME logs unavailable",
                    Recommendation = $"Log directory not found: {_logsDirectory}",
                    ConfidenceScore = 10
                }
            ];
        }

        var hint = (packageIdOrNameHint ?? string.Empty).Trim();
        var files = Directory
            .EnumerateFiles(_logsDirectory, "IntuneManagementExtension*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(info => info.Exists)
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Take(3)
            .ToList();

        if (files.Count == 0)
        {
            return [];
        }

        var matches = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(file.FullName, cancellationToken);
            var lines = content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Contains("detection", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("0x87D1041C", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("discovery", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(hint) ||
                        line.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(line.Trim());
                    }
                }
            }
        }

        if (matches.Count == 0)
        {
            return [];
        }

        var feedback = new List<ImeDetectionFeedback>();
        if (matches.Any(line => line.Contains("0x87D1041C", StringComparison.OrdinalIgnoreCase)))
        {
            feedback.Add(new ImeDetectionFeedback
            {
                Signal = "Detected 0x87D1041C (app not detected after install)",
                Recommendation = "Review detection rule identity and version operator. Prefer exact MSI ProductCode or strict EXE registry identity.",
                ConfidenceScore = 95
            });
        }

        if (matches.Any(line => line.Contains("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("HKCU", StringComparison.OrdinalIgnoreCase)))
        {
            feedback.Add(new ImeDetectionFeedback
            {
                Signal = "User hive detection observed in failure logs",
                Recommendation = "If install context is System, move detection to HKLM or switch install context to User.",
                ConfidenceScore = 84
            });
        }

        if (matches.Any(line => line.Contains("script", StringComparison.OrdinalIgnoreCase) &&
                                (line.Contains("no output", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("stdout", StringComparison.OrdinalIgnoreCase))))
        {
            feedback.Add(new ImeDetectionFeedback
            {
                Signal = "Script detection output issue",
                Recommendation = "Ensure success path writes STDOUT content and exits with code 0; avoid Write-Error or throw in success paths.",
                ConfidenceScore = 88
            });
        }

        if (matches.Any(line => line.Contains("file", StringComparison.OrdinalIgnoreCase) &&
                                line.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            feedback.Add(new ImeDetectionFeedback
            {
                Signal = "File detection path not found",
                Recommendation = "Use a stable binary path/value pair and verify x86/x64 view alignment.",
                ConfidenceScore = 76
            });
        }

        if (feedback.Count == 0)
        {
            var sample = MultiSpaceRegex.Replace(matches[0], " ");
            feedback.Add(new ImeDetectionFeedback
            {
                Signal = "Generic detection failure signal",
                Recommendation = $"Review IME line: {sample}",
                ConfidenceScore = 40
            });
        }

        return feedback
            .GroupBy(item => item.Signal, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.ConfidenceScore).First())
            .OrderByDescending(item => item.ConfidenceScore)
            .ToList();
    }
}
