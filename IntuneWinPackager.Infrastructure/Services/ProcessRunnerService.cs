using System.Diagnostics;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class ProcessRunnerService : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        IProgress<ProcessOutputLine>? outputProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new ArgumentException("Process file name is required.", nameof(request));
        }

        var processStartInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            Arguments = request.Arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? Environment.CurrentDirectory
                : request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process
        {
            StartInfo = processStartInfo,
            EnableRaisingEvents = true
        };

        var exitCodeCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                outputProgress?.Report(new ProcessOutputLine
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Severity = LogSeverity.Info,
                    Text = args.Data
                });
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                outputProgress?.Report(new ProcessOutputLine
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Severity = LogSeverity.Error,
                    Text = args.Data
                });
            }
        };

        process.Exited += (_, _) =>
        {
            exitCodeCompletion.TrySetResult(process.ExitCode);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process {request.FileName}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort process termination.
            }

            exitCodeCompletion.TrySetCanceled(cancellationToken);
        });

        try
        {
            var exitCode = await exitCodeCompletion.Task;
            await process.WaitForExitAsync(cancellationToken);

            return new ProcessRunResult
            {
                ExitCode = exitCode,
                TimedOut = false
            };
        }
        catch (OperationCanceledException)
        {
            return new ProcessRunResult
            {
                ExitCode = -1,
                TimedOut = true
            };
        }
    }
}
