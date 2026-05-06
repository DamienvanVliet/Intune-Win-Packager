using System.Text.Json;
using System.Diagnostics;
using IntuneWinPackager.Infrastructure.Services;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Tests.Services;

public sealed class SandboxProofServiceTests
{
    [Fact]
    public async Task StartAsync_WhenLaunchDisabled_CreatesProofWorkspaceAndRewritesSetupPath()
    {
        var tempRoot = CreateTempDirectory();
        var setupPath = Path.Combine(tempRoot, "Example.Setup.exe");
        await File.WriteAllTextAsync(setupPath, "not a real exe");

        var sut = new SandboxProofService();

        var session = await sut.StartAsync(new SandboxProofRequest
        {
            InstallerType = InstallerType.Exe,
            SourceFolder = tempRoot,
            SetupFilePath = setupPath,
            InstallCommand = $"\"{setupPath}\" /quiet",
            DetectionRule = BuildFileDetectionRule(),
            LaunchSandbox = false
        });

        try
        {
            Assert.True(session.Success);
            Assert.False(session.Launched);
            Assert.True(Directory.Exists(session.RunDirectory));
            Assert.True(File.Exists(session.WsbPath));
            Assert.True(File.Exists(session.InputPath));
            Assert.True(File.Exists(session.RunnerScriptPath));

            var input = await ReadInputAsync(session.InputPath);
            var sandboxSetupPath = input.RootElement.GetProperty("sandboxSetupFilePath").GetString();
            var installCommand = input.RootElement.GetProperty("installCommand").GetString();

            Assert.Equal(@"C:\IwpSandboxSource\Example.Setup.exe", sandboxSetupPath);
            Assert.Contains(@"C:\IwpSandboxSource\Example.Setup.exe", installCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(setupPath, installCommand, StringComparison.OrdinalIgnoreCase);

            var wsb = await File.ReadAllTextAsync(session.WsbPath);
            Assert.Contains("<SandboxFolder>C:\\IwpSandboxProof</SandboxFolder>", wsb, StringComparison.Ordinal);
            Assert.Contains("<SandboxFolder>C:\\IwpSandboxSource</SandboxFolder>", wsb, StringComparison.Ordinal);
            Assert.Contains("<ReadOnly>true</ReadOnly>", wsb, StringComparison.Ordinal);

            var script = await File.ReadAllTextAsync(session.RunnerScriptPath);
            Assert.Contains("Get-UninstallSnapshot", script, StringComparison.Ordinal);
            Assert.Contains("Detection candidates", script, StringComparison.Ordinal);
            Assert.Contains("result.json", script, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(session.RunDirectory);
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task StartAsync_WhenSetupIsOutsideSourceFolder_CopiesInstallerIntoRunInput()
    {
        var tempRoot = CreateTempDirectory();
        var sourceFolder = Path.Combine(tempRoot, "source");
        var externalFolder = Path.Combine(tempRoot, "external");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(externalFolder);

        var setupPath = Path.Combine(externalFolder, "OutsideSource.exe");
        await File.WriteAllTextAsync(setupPath, "not a real exe");

        var sut = new SandboxProofService();

        var session = await sut.StartAsync(new SandboxProofRequest
        {
            InstallerType = InstallerType.Exe,
            SourceFolder = sourceFolder,
            SetupFilePath = setupPath,
            InstallCommand = $"\"{setupPath}\" /S",
            DetectionRule = BuildFileDetectionRule(),
            LaunchSandbox = false
        });

        try
        {
            Assert.True(session.Success);

            var copiedSetup = Path.Combine(session.RunDirectory, "input", "OutsideSource.exe");
            Assert.True(File.Exists(copiedSetup));

            var input = await ReadInputAsync(session.InputPath);
            var sandboxSetupPath = input.RootElement.GetProperty("sandboxSetupFilePath").GetString();
            var installCommand = input.RootElement.GetProperty("installCommand").GetString();

            Assert.Equal(@"C:\IwpSandboxProof\input\OutsideSource.exe", sandboxSetupPath);
            Assert.Contains(@"C:\IwpSandboxProof\input\OutsideSource.exe", installCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(setupPath, installCommand, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(session.RunDirectory);
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task StartAsync_WhenSetupMissing_ReturnsFailure()
    {
        var sut = new SandboxProofService();

        var session = await sut.StartAsync(new SandboxProofRequest
        {
            InstallerType = InstallerType.Exe,
            SetupFilePath = Path.Combine(Path.GetTempPath(), "does-not-exist.exe"),
            LaunchSandbox = false
        });

        Assert.False(session.Success);
        Assert.Contains("existing setup file", session.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrWhiteSpace(session.RunDirectory));
    }

    [Fact]
    public async Task StartAsync_GeneratedRunnerScript_IsPowerShellParseable()
    {
        var tempRoot = CreateTempDirectory();
        var setupPath = Path.Combine(tempRoot, "Parseable.exe");
        await File.WriteAllTextAsync(setupPath, "not a real exe");

        var sut = new SandboxProofService();

        var session = await sut.StartAsync(new SandboxProofRequest
        {
            InstallerType = InstallerType.Exe,
            SourceFolder = tempRoot,
            SetupFilePath = setupPath,
            InstallCommand = "Parseable.exe /quiet",
            DetectionRule = BuildFileDetectionRule(),
            LaunchSandbox = false
        });

        try
        {
            Assert.True(session.Success);

            var parseCommand =
                "$tokens=$null; $errors=$null; " +
                $"[System.Management.Automation.Language.Parser]::ParseFile({ToPowerShellSingleQuoted(session.RunnerScriptPath)}, [ref]$tokens, [ref]$errors) | Out-Null; " +
                "if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }";

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(parseCommand);

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.True(process.ExitCode == 0, $"PowerShell parse failed. STDOUT: {stdout} STDERR: {stderr}");
        }
        finally
        {
            TryDeleteDirectory(session.RunDirectory);
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ReadResultAsync_WhenNestedCandidateExists_ReturnsBestDetectionRule()
    {
        var tempRoot = CreateTempDirectory();
        var resultPath = Path.Combine(tempRoot, "result.json");
        await File.WriteAllTextAsync(resultPath, """
            {
              "schemaVersion": 1,
              "candidates": [
                {
                  "type": "File",
                  "confidence": "Medium",
                  "reason": "Folder appeared.",
                  "rule": {
                    "ruleType": "File",
                    "file": {
                      "path": "C:\\Program Files",
                      "fileOrFolderName": "Notepad++",
                      "check32BitOn64System": false,
                      "operator": "Exists",
                      "value": ""
                    }
                  }
                },
                {
                  "type": "Registry",
                  "confidence": "High",
                  "reason": "New uninstall entry with DisplayVersion after install.",
                  "rule": {
                    "ruleType": "Registry",
                    "registry": {
                      "hive": "HKEY_LOCAL_MACHINE",
                      "keyPath": "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Notepad++",
                      "valueName": "DisplayVersion",
                      "check32BitOn64System": false,
                      "operator": "Equals",
                      "value": "8.9.4"
                    }
                  }
                }
              ]
            }
            """);

        var sut = new SandboxProofService();

        try
        {
            var result = await sut.ReadResultAsync(resultPath);

            Assert.True(result.Completed);
            Assert.False(result.Failed);
            Assert.Equal(2, result.CandidateCount);
            Assert.NotNull(result.BestCandidate);
            Assert.Equal(IntuneDetectionRuleType.Registry, result.BestCandidate.Rule.RuleType);
            Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Notepad++", result.BestCandidate.Rule.Registry.KeyPath);
            Assert.Equal("DisplayVersion", result.BestCandidate.Rule.Registry.ValueName);
            Assert.Equal(IntuneDetectionOperator.Equals, result.BestCandidate.Rule.Registry.Operator);
            Assert.Equal("8.9.4", result.BestCandidate.Rule.Registry.Value);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ReadResultAsync_WhenLegacyFlattenedCandidateExists_StillReturnsDetectionRule()
    {
        var tempRoot = CreateTempDirectory();
        var resultPath = Path.Combine(tempRoot, "result.json");
        await File.WriteAllTextAsync(resultPath, """
            {
              "schemaVersion": 1,
              "candidates": [
                {
                  "type": "File",
                  "confidence": "High",
                  "reason": "New uninstall entry points to an existing install footprint.",
                  "rule": {
                    "ruleType": "File",
                    "path": "C:\\Program Files\\Notepad++",
                    "fileOrFolderName": "notepad++.exe",
                    "check32BitOn64System": false,
                    "operator": "GreaterThanOrEqual",
                    "value": "8.9.4"
                  }
                }
              ]
            }
            """);

        var sut = new SandboxProofService();

        try
        {
            var result = await sut.ReadResultAsync(resultPath);

            Assert.True(result.Completed);
            Assert.NotNull(result.BestCandidate);
            Assert.Equal(IntuneDetectionRuleType.File, result.BestCandidate.Rule.RuleType);
            Assert.Equal(@"C:\Program Files\Notepad++", result.BestCandidate.Rule.File.Path);
            Assert.Equal("notepad++.exe", result.BestCandidate.Rule.File.FileOrFolderName);
            Assert.Equal(IntuneDetectionOperator.GreaterThanOrEqual, result.BestCandidate.Rule.File.Operator);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static IntuneDetectionRule BuildFileDetectionRule()
    {
        return new IntuneDetectionRule
        {
            RuleType = IntuneDetectionRuleType.File,
            File = new FileDetectionRule
            {
                Path = @"%ProgramFiles%\Example",
                FileOrFolderName = "Example.exe",
                Operator = IntuneDetectionOperator.Exists
            }
        };
    }

    private static async Task<JsonDocument> ReadInputAsync(string inputPath)
    {
        var json = await File.ReadAllTextAsync(inputPath);
        return JsonDocument.Parse(json);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "iwp-sandbox-proof-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ToPowerShellSingleQuoted(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup for local test artifacts.
        }
    }
}
