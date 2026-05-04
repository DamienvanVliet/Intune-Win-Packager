using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace IntuneWinPackager.Tests.Services;

public sealed class DetectionTestServiceTests
{
    [Fact]
    public async Task TestAsync_ScriptDetection_Fails_WhenExitZeroHasNoStdOut()
    {
        var runner = new FakeProcessRunner(
            new ProcessRunResult
            {
                ExitCode = 0,
                TimedOut = false
            },
            []);
        var sut = new DetectionTestService(runner);

        var result = await sut.TestAsync(
            InstallerType.Exe,
            new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.Script,
                Script = new ScriptDetectionRule
                {
                    ScriptBody = "exit 0"
                }
            });

        Assert.False(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.HasStdOut);
    }

    [Fact]
    public async Task TestAsync_ScriptDetection_Passes_WhenExitZeroHasStdOutAndNoStdErr()
    {
        var runner = new FakeProcessRunner(
            new ProcessRunResult
            {
                ExitCode = 0,
                TimedOut = false
            },
            [
                new ProcessOutputLine
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Severity = LogSeverity.Info,
                    Text = "detected:1.2.3"
                }
            ]);
        var sut = new DetectionTestService(runner);

        var result = await sut.TestAsync(
            InstallerType.AppxMsix,
            new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.Script,
                Script = new ScriptDetectionRule
                {
                    ScriptBody = "Write-Output 'detected:1.2.3'; exit 0"
                }
            });

        Assert.True(result.Success, $"Summary={result.Summary}; Details={result.Details}; Exit={result.ExitCode}; StdOut={result.StandardOutput}; StdErr={result.StandardError}");
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.HasStdOut);
        Assert.False(result.HasStdErr);
    }

    [Fact]
    public async Task TestAsync_MsiDetection_Passes_WhenProductCodeExistsAndVersionIsOptional()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var productCode = "{A6BE1587-0A9A-42B7-8E24-4F12A08BE9C2}";
        var keyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{productCode}";

        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        using var created = Registry.CurrentUser.CreateSubKey(keyPath);
        created?.SetValue("DisplayVersion", "3.2.1");

        try
        {
            var sut = new DetectionTestService(new FakeProcessRunner(new ProcessRunResult(), []));
            var result = await sut.TestAsync(
                InstallerType.Msi,
                new IntuneDetectionRule
                {
                    RuleType = IntuneDetectionRuleType.MsiProductCode,
                    Msi = new MsiDetectionRule
                    {
                        ProductCode = productCode,
                        ProductVersion = string.Empty
                    }
                });

            Assert.True(result.Success);
            Assert.Contains(productCode, result.Details, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task ProveAsync_PassiveMode_ReturnsSuccessfulTwoPhaseProof()
    {
        var sut = new DetectionTestService(new ScriptAwareProcessRunner());

        var proof = await sut.ProveAsync(new DetectionProofRequest
        {
            InstallerType = InstallerType.Exe,
            Mode = DetectionProofMode.PassiveRuleControl,
            DetectionRule = new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.Script,
                Script = new ScriptDetectionRule
                {
                    ScriptBody = "Write-Output 'detected:1.0.0'; exit 0"
                }
            }
        });

        Assert.True(proof.Success);
        Assert.True(proof.NegativePhase.Success);
        Assert.True(proof.PositivePhase.Success);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly ProcessRunResult _result;
        private readonly IReadOnlyList<ProcessOutputLine> _lines;

        public FakeProcessRunner(ProcessRunResult result, IReadOnlyList<ProcessOutputLine> lines)
        {
            _result = result;
            _lines = lines;
        }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            IProgress<ProcessOutputLine>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var line in _lines)
            {
                outputProgress?.Report(line);
            }

            return Task.FromResult(_result);
        }
    }

    private sealed class ScriptAwareProcessRunner : IProcessRunner
    {
        private static readonly Regex FileArgumentRegex = new("-File\\s+(?:\"(?<pathq>[^\"]+)\"|(?<pathu>[^\\s]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            IProgress<ProcessOutputLine>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (!request.FileName.Contains("powershell", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ProcessRunResult { ExitCode = 0, TimedOut = false });
            }

            var match = FileArgumentRegex.Match(request.Arguments ?? string.Empty);
            if (!match.Success)
            {
                return Task.FromResult(new ProcessRunResult { ExitCode = 1, TimedOut = false });
            }

            var path = match.Groups["pathq"].Success
                ? match.Groups["pathq"].Value
                : match.Groups["pathu"].Value;
            var scriptBody = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            if (scriptBody.Contains("exit 1", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ProcessRunResult { ExitCode = 1, TimedOut = false });
            }

            if (scriptBody.Contains("Write-Output", StringComparison.OrdinalIgnoreCase))
            {
                outputProgress?.Report(new ProcessOutputLine
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Severity = LogSeverity.Info,
                    Text = "detected:1.0.0"
                });
            }

            return Task.FromResult(new ProcessRunResult { ExitCode = 0, TimedOut = false });
        }
    }
}
