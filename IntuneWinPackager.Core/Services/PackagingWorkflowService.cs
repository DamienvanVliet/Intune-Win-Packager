using System.IO;
using System.Text.RegularExpressions;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Process;

namespace IntuneWinPackager.Core.Services;

public sealed class PackagingWorkflowService : IPackagingWorkflowService
{
    private readonly IValidationService _validationService;
    private readonly IProcessRunner _processRunner;

    public PackagingWorkflowService(
        IValidationService validationService,
        IProcessRunner processRunner)
    {
        _validationService = validationService;
        _processRunner = processRunner;
    }

    public async Task<PackagingResult> PackageAsync(
        PackagingRequest request,
        IProgress<string>? logProgress = null,
        IProgress<PackagingProgressUpdate>? progressUpdate = null,
        CancellationToken cancellationToken = default)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var lastReportedProgress = 0d;

        void ReportProgress(double percentage, string step, string detail, bool isIndeterminate = false)
        {
            var normalized = Math.Clamp(percentage, 0, 100);
            if (!isIndeterminate)
            {
                normalized = Math.Max(normalized, lastReportedProgress);
                lastReportedProgress = normalized;
            }

            progressUpdate?.Report(new PackagingProgressUpdate
            {
                Percentage = normalized,
                IsIndeterminate = isIndeterminate,
                Step = step,
                Detail = detail
            });
        }

        ReportProgress(5, "Validating Input", "Checking source, setup, output, and tool path.");

        var validation = _validationService.Validate(request);
        if (!validation.IsValid)
        {
            ReportProgress(100, "Validation Failed", "Resolve input errors before packaging.");

            return new PackagingResult
            {
                Success = false,
                Message = string.Join(Environment.NewLine, validation.Errors),
                ExitCode = -1,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
        }

        ReportProgress(15, "Preparing Output", "Ensuring output folder exists.");
        Directory.CreateDirectory(request.Configuration.OutputFolder);
        if (request.UseLowImpactMode)
        {
            ReportProgress(18, "Performance Mode", "Low impact mode is enabled to keep your PC responsive.");
            logProgress?.Report("Low impact mode enabled: packaging process priority will be reduced.");
        }

        var setupFileName = Path.GetFileName(request.Configuration.SetupFilePath);
        var arguments = $"-c {Quote(request.Configuration.SourceFolder)} -s {Quote(setupFileName)} -o {Quote(request.Configuration.OutputFolder)} -q";

        ReportProgress(25, "Starting Packager", "Launching IntuneWinAppUtil.exe.");
        logProgress?.Report($"Starting packaging for {setupFileName}");
        logProgress?.Report($"Running: {Path.GetFileName(request.IntuneWinAppUtilPath)} {arguments}");

        var processResult = await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = request.IntuneWinAppUtilPath,
                Arguments = arguments,
                WorkingDirectory = request.Configuration.SourceFolder,
                PreferLowImpact = request.UseLowImpactMode
            },
            new Progress<ProcessOutputLine>(line =>
            {
                if (!string.IsNullOrWhiteSpace(line.Text))
                {
                    logProgress?.Report(line.Text);
                    TryReportProgressFromToolOutput(line.Text, ReportProgress);
                }
            }),
            cancellationToken);

        ReportProgress(92, "Resolving Output", "Locating generated .intunewin package.");
        var outputPackagePath = ResolveOutputPackagePath(request.Configuration.OutputFolder, setupFileName, startedAtUtc);

        var completedAtUtc = DateTimeOffset.UtcNow;
        if (processResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(outputPackagePath))
        {
            ReportProgress(100, "Completed", "Packaging completed successfully.");

            return new PackagingResult
            {
                Success = true,
                Message = "Packaging completed successfully.",
                OutputPackagePath = outputPackagePath,
                ExitCode = processResult.ExitCode,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtc
            };
        }

        var failureMessage = processResult.ExitCode == 0
            ? "IntuneWinAppUtil finished but no .intunewin file was found in the output folder."
            : $"Packaging failed with exit code {processResult.ExitCode}.";

        ReportProgress(100, "Failed", failureMessage);

        return new PackagingResult
        {
            Success = false,
            Message = failureMessage,
            OutputPackagePath = outputPackagePath,
            ExitCode = processResult.ExitCode,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc
        };
    }

    private static void TryReportProgressFromToolOutput(
        string line,
        Action<double, string, string, bool> reportProgress)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var lowered = line.Trim().ToLowerInvariant();

        var percentMatch = Regex.Match(lowered, @"\b(?<p>\d{1,3})\s*%");
        if (percentMatch.Success && int.TryParse(percentMatch.Groups["p"].Value, out var parsedPercent))
        {
            var mapped = Math.Clamp(25 + (parsedPercent * 0.65), 25, 95);
            reportProgress(mapped, "Packaging", line.Trim(), false);
            return;
        }

        if (lowered.Contains("validating parameters"))
        {
            reportProgress(35, "Validating Parameters", line.Trim(), false);
            return;
        }

        if (lowered.Contains("validated parameters"))
        {
            reportProgress(42, "Parameters Validated", line.Trim(), false);
            return;
        }

        if (lowered.Contains("compressing the source folder"))
        {
            reportProgress(58, "Compressing Files", line.Trim(), false);
            return;
        }

        if (lowered.Contains("calculated size"))
        {
            reportProgress(66, "Analyzing Contents", line.Trim(), false);
            return;
        }

        if (lowered.Contains("encrypt"))
        {
            reportProgress(78, "Encrypting Package", line.Trim(), false);
            return;
        }

        if (lowered.Contains("catalog") || lowered.Contains("detection.xml") || lowered.Contains("manifest"))
        {
            reportProgress(88, "Finalizing Package", line.Trim(), false);
            return;
        }

        if (lowered.Contains("done") || lowered.Contains("completed"))
        {
            reportProgress(95, "Finishing Up", line.Trim(), false);
        }
    }

    private static string Quote(string value) => $"\"{value}\"";

    private static string? ResolveOutputPackagePath(string outputFolder, string setupFileName, DateTimeOffset startedAtUtc)
    {
        var expectedPath = Path.Combine(outputFolder, $"{Path.GetFileNameWithoutExtension(setupFileName)}.intunewin");
        if (File.Exists(expectedPath))
        {
            return expectedPath;
        }

        if (!Directory.Exists(outputFolder))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(outputFolder, "*.intunewin", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && file.LastWriteTimeUtc >= startedAtUtc.UtcDateTime.AddMinutes(-1))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }
}
