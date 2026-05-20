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
    public async Task TestAsync_ScriptDetection_ReturnsValidNotDetected_WhenExitOneHasNoStdErr()
    {
        var runner = new FakeProcessRunner(
            new ProcessRunResult
            {
                ExitCode = 1,
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
                    ScriptBody = "if ($false) { Write-Output 'detected'; exit 0 }; exit 1"
                }
            });

        Assert.True(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.HasStdErr);
        Assert.Contains("valid Intune not-detected signal", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAsync_ScriptDetection_WritesUtf8BomAndUsesNonInteractivePowerShell()
    {
        var runner = new CapturingProcessRunner(new ProcessRunResult { ExitCode = 1, TimedOut = false });
        var sut = new DetectionTestService(runner);

        var result = await sut.TestAsync(
            InstallerType.Exe,
            new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.Script,
                Script = new ScriptDetectionRule
                {
                    ScriptBody = "if ($false) { Write-Output 'detected'; exit 0 }; exit 1",
                    RunAs32BitOn64System = true
                }
            });

        Assert.True(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("-NonInteractive", runner.LastRequest.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.True(runner.LastScriptBytes.Length >= 3);
        Assert.Equal((byte)0xEF, runner.LastScriptBytes[0]);
        Assert.Equal((byte)0xBB, runner.LastScriptBytes[1]);
        Assert.Equal((byte)0xBF, runner.LastScriptBytes[2]);
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
    public async Task TestAsync_FileDetection_PerformsRealFileSystemCheck()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var probeFile = Path.Combine(tempRoot, "ContosoAgent.exe");
        await File.WriteAllTextAsync(probeFile, "probe");

        try
        {
            var sut = new DetectionTestService(new FakeProcessRunner(new ProcessRunResult(), []));
            var result = await sut.TestAsync(
                InstallerType.Exe,
                new IntuneDetectionRule
                {
                    RuleType = IntuneDetectionRuleType.File,
                    File = new FileDetectionRule
                    {
                        Path = tempRoot,
                        FileOrFolderName = "ContosoAgent.exe",
                        Operator = IntuneDetectionOperator.Exists
                    }
                });

            Assert.True(result.Success, result.Summary);
            Assert.Contains(probeFile, result.Details, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TestAsync_FileDetection_Fails_WhenTargetDoesNotExist()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var sut = new DetectionTestService(new FakeProcessRunner(new ProcessRunResult(), []));
            var result = await sut.TestAsync(
                InstallerType.Exe,
                new IntuneDetectionRule
                {
                    RuleType = IntuneDetectionRuleType.File,
                    File = new FileDetectionRule
                    {
                        Path = tempRoot,
                        FileOrFolderName = "MissingAgent.exe",
                        Operator = IntuneDetectionOperator.Exists
                    }
                });

            Assert.False(result.Success);
            Assert.Contains("not found", result.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
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

    [Fact]
    public async Task ProveAsync_PassiveMode_PassesValidation_WhenConfiguredRuleReportsNotDetected()
    {
        var sut = new DetectionTestService(new FakeProcessRunner(
            new ProcessRunResult
            {
                ExitCode = 1,
                TimedOut = false
            },
            []));

        var proof = await sut.ProveAsync(new DetectionProofRequest
        {
            InstallerType = InstallerType.Exe,
            Mode = DetectionProofMode.PassiveRuleControl,
            RequirePositiveDetection = false,
            DetectionRule = new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.Script,
                Script = new ScriptDetectionRule
                {
                    ScriptBody = "if ($false) { Write-Output 'detected'; exit 0 }; exit 1"
                }
            }
        });

        Assert.True(proof.Success);
        Assert.True(proof.NegativePhase.Success);
        Assert.True(proof.PositivePhase.Success);
        Assert.Contains("Intune-compatible", proof.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProveAsync_PassiveMode_Fails_WhenPositiveDetectionIsRequiredAndRuleReportsNotDetected()
    {
        var sut = new DetectionTestService(new FakeProcessRunner(
            new ProcessRunResult
            {
                ExitCode = 1,
                TimedOut = false
            },
            []));

        var proof = await sut.ProveAsync(new DetectionProofRequest
        {
            InstallerType = InstallerType.Exe,
            Mode = DetectionProofMode.PassiveRuleControl,
            RequirePositiveDetection = true,
            DetectionRule = new IntuneDetectionRule
            {
                RuleType = IntuneDetectionRuleType.Script,
                Script = new ScriptDetectionRule
                {
                    ScriptBody = "if ($false) { Write-Output 'detected'; exit 0 }; exit 1"
                }
            }
        });

        Assert.False(proof.Success);
        Assert.True(proof.NegativePhase.Success);
        Assert.False(proof.PositivePhase.Success);
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

    private sealed class CapturingProcessRunner : IProcessRunner
    {
        private static readonly Regex FileArgumentRegex = new("-File\\s+(?:\"(?<pathq>[^\"]+)\"|(?<pathu>[^\\s]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly ProcessRunResult _result;

        public CapturingProcessRunner(ProcessRunResult result)
        {
            _result = result;
        }

        public ProcessRunRequest LastRequest { get; private set; } = new();

        public byte[] LastScriptBytes { get; private set; } = [];

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            IProgress<ProcessOutputLine>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            var match = FileArgumentRegex.Match(request.Arguments ?? string.Empty);
            if (match.Success)
            {
                var path = match.Groups["pathq"].Success
                    ? match.Groups["pathq"].Value
                    : match.Groups["pathu"].Value;
                LastScriptBytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
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
