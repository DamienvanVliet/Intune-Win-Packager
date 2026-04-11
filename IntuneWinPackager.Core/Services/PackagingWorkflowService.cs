using System.IO;
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
        CancellationToken cancellationToken = default)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;

        var validation = _validationService.Validate(request);
        if (!validation.IsValid)
        {
            return new PackagingResult
            {
                Success = false,
                Message = string.Join(Environment.NewLine, validation.Errors),
                ExitCode = -1,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
        }

        Directory.CreateDirectory(request.Configuration.OutputFolder);

        var setupFileName = Path.GetFileName(request.Configuration.SetupFilePath);
        var arguments = $"-c {Quote(request.Configuration.SourceFolder)} -s {Quote(setupFileName)} -o {Quote(request.Configuration.OutputFolder)} -q";

        logProgress?.Report($"Starting packaging for {setupFileName}");
        logProgress?.Report($"Running: {Path.GetFileName(request.IntuneWinAppUtilPath)} {arguments}");

        var processResult = await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = request.IntuneWinAppUtilPath,
                Arguments = arguments,
                WorkingDirectory = request.Configuration.SourceFolder
            },
            new Progress<ProcessOutputLine>(line =>
            {
                if (!string.IsNullOrWhiteSpace(line.Text))
                {
                    logProgress?.Report(line.Text);
                }
            }),
            cancellationToken);

        var outputPackagePath = ResolveOutputPackagePath(request.Configuration.OutputFolder, setupFileName, startedAtUtc);

        var completedAtUtc = DateTimeOffset.UtcNow;
        if (processResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(outputPackagePath))
        {
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
