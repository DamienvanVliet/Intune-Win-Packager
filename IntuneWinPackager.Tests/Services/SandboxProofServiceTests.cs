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
            UninstallCommand = $"\"{setupPath}\" /uninstall /quiet",
            DetectionRule = BuildFileDetectionRule(),
            PrecheckSummary = "Pre-check: existing File detection rule is configured; install and uninstall commands are complete.",
            PrecheckDetectionRuleAvailable = true,
            PrecheckAdditionalDetectionRuleCount = 1,
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
            var uninstallCommand = input.RootElement.GetProperty("uninstallCommand").GetString();

            Assert.Equal(@"C:\IwpSandboxSource\Example.Setup.exe", sandboxSetupPath);
            Assert.Contains(@"C:\IwpSandboxSource\Example.Setup.exe", installCommand, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"C:\IwpSandboxSource\Example.Setup.exe", uninstallCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(setupPath, installCommand, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(setupPath, uninstallCommand, StringComparison.OrdinalIgnoreCase);
            Assert.True(input.RootElement.GetProperty("precheckDetectionRuleAvailable").GetBoolean());
            Assert.Equal(1, input.RootElement.GetProperty("precheckAdditionalDetectionRuleCount").GetInt32());
            Assert.Contains("existing File detection", input.RootElement.GetProperty("precheckSummary").GetString(), StringComparison.OrdinalIgnoreCase);

            var wsb = await File.ReadAllTextAsync(session.WsbPath);
            Assert.Contains("<SandboxFolder>C:\\IwpSandboxProof</SandboxFolder>", wsb, StringComparison.Ordinal);
            Assert.Contains("<SandboxFolder>C:\\IwpSandboxSource</SandboxFolder>", wsb, StringComparison.Ordinal);
            Assert.Contains("<ReadOnly>true</ReadOnly>", wsb, StringComparison.Ordinal);

            var script = await File.ReadAllTextAsync(session.RunnerScriptPath);
            Assert.Contains("Get-UninstallSnapshot", script, StringComparison.Ordinal);
            Assert.Contains("Get-ExecutableSnapshot", script, StringComparison.Ordinal);
            Assert.Contains("Get-ShortcutSnapshot", script, StringComparison.Ordinal);
            Assert.Contains("Find-ExecutableDetectionTargets", script, StringComparison.Ordinal);
            Assert.Contains("additionalRules", script, StringComparison.Ordinal);
            Assert.Contains("Package Cache", script, StringComparison.Ordinal);
            Assert.Contains("targetName -match", script, StringComparison.Ordinal);
            Assert.Contains("New MSI ProductCode registered after install", script, StringComparison.Ordinal);
            Assert.Contains("Detection candidates", script, StringComparison.Ordinal);
            Assert.Contains("Candidate passed sandbox two-phase validation", script, StringComparison.Ordinal);
            Assert.Contains("Candidate passed sandbox install, detection, and uninstall validation", script, StringComparison.Ordinal);
            Assert.Contains("Complete-CandidateUninstallProof", script, StringComparison.Ordinal);
            Assert.Contains("New-ConfiguredDetectionCandidate", script, StringComparison.Ordinal);
            Assert.Contains("post-uninstall", script, StringComparison.Ordinal);
            Assert.Contains("Uninstall proof", script, StringComparison.Ordinal);
            Assert.Contains("uninstallValidation", script, StringComparison.Ordinal);
            Assert.Contains("Install execution mode", script, StringComparison.Ordinal);
            Assert.Contains("Uninstall execution mode", script, StringComparison.Ordinal);
            Assert.Contains("New-ScheduledTaskPrincipal -UserId 'SYSTEM'", script, StringComparison.Ordinal);
            Assert.Contains("ScheduledTaskSystem", script, StringComparison.Ordinal);
            Assert.Contains("Resolve-ProofUninstallCommand", script, StringComparison.Ordinal);
            Assert.Contains("uninstallResolution", script, StringComparison.Ordinal);
            Assert.Contains("$blockingFailureKind = if ($failureKind -eq 'LaunchValidation')", script, StringComparison.Ordinal);
            Assert.Contains("failureKind", script, StringComparison.Ordinal);
            Assert.Contains("Invoke-LaunchValidation", script, StringComparison.Ordinal);
            Assert.Contains("Measure-WhiteWindowRatio", script, StringComparison.Ordinal);
            Assert.Contains("$brightness -ge 185", script, StringComparison.Ordinal);
            Assert.Contains("$spread -le 70", script, StringComparison.Ordinal);
            Assert.Contains("blank/light window", script, StringComparison.Ordinal);
            Assert.Contains("launch-window.png", script, StringComparison.Ordinal);
            Assert.Contains("Invoke-WeintekSoftwareRenderWorkaround", script, StringComparison.Ordinal);
            Assert.Contains("DisplaySetting.exe", script, StringComparison.Ordinal);
            Assert.Contains("Software render", script, StringComparison.Ordinal);
            Assert.Contains("UIAutomationClient", script, StringComparison.Ordinal);
            Assert.Contains("launchRemediation", script, StringComparison.Ordinal);
            Assert.Contains("Skipping dependency uninstall entry as primary app detection", script, StringComparison.Ordinal);
            Assert.Contains("$startProcessParameters = @", script, StringComparison.Ordinal);
            Assert.Contains("IsNullOrWhiteSpace([string]$target.arguments)", script, StringComparison.Ordinal);
            Assert.Contains("schemaVersion = 2", script, StringComparison.Ordinal);
            Assert.Contains("GreaterThanOrEqual", script, StringComparison.Ordinal);
            Assert.Contains("result.json", script, StringComparison.Ordinal);
            Assert.Contains("/d /s /c call $Command", script, StringComparison.Ordinal);
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
                  "proof": {
                    "success": true,
                    "summary": "Candidate passed sandbox two-phase validation.",
                    "negativePhase": {
                      "success": true,
                      "summary": "Uninstall registry entry was absent before install and present after install.",
                      "details": "HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Notepad++"
                    },
                    "positivePhase": {
                      "success": true,
                      "summary": "Registry comparison passed.",
                      "details": "Actual='8.9.4'"
                    }
                  },
                  "rule": {
                    "ruleType": "Registry",
                    "registry": {
                      "hive": "HKEY_LOCAL_MACHINE",
                      "keyPath": "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Notepad++",
                      "valueName": "DisplayVersion",
                      "check32BitOn64System": false,
                      "operator": "GreaterThanOrEqual",
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
            Assert.Equal(1, result.ProvenCandidateCount);
            Assert.NotNull(result.BestCandidate);
            Assert.True(result.BestCandidate.IsProven);
            Assert.True(result.BestCandidate.ProofAvailable);
            Assert.Equal(IntuneDetectionRuleType.Registry, result.BestCandidate.Rule.RuleType);
            Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Notepad++", result.BestCandidate.Rule.Registry.KeyPath);
            Assert.Equal("DisplayVersion", result.BestCandidate.Rule.Registry.ValueName);
            Assert.Equal(IntuneDetectionOperator.GreaterThanOrEqual, result.BestCandidate.Rule.Registry.Operator);
            Assert.Equal("8.9.4", result.BestCandidate.Rule.Registry.Value);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ReadResultAsync_WhenProofAvailable_SelectsProvenCandidateOverHigherConfidenceFailure()
    {
        var tempRoot = CreateTempDirectory();
        var resultPath = Path.Combine(tempRoot, "result.json");
        await File.WriteAllTextAsync(resultPath, """
            {
              "schemaVersion": 2,
              "candidates": [
                {
                  "type": "Registry",
                  "confidence": "High",
                  "reason": "Registry candidate failed validation.",
                  "proof": {
                    "success": false,
                    "summary": "Candidate failed sandbox validation.",
                    "negativePhase": { "success": true, "summary": "Registry entry was new.", "details": "" },
                    "positivePhase": { "success": false, "summary": "Registry comparison failed.", "details": "" }
                  },
                  "rule": {
                    "ruleType": "Registry",
                    "registry": {
                      "hive": "HKEY_LOCAL_MACHINE",
                      "keyPath": "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Contoso",
                      "valueName": "DisplayVersion",
                      "operator": "GreaterThanOrEqual",
                      "value": "1.0"
                    }
                  }
                },
                {
                  "type": "File",
                  "confidence": "Medium",
                  "reason": "Folder candidate passed validation.",
                  "proof": {
                    "success": true,
                    "summary": "Candidate passed sandbox two-phase validation.",
                    "negativePhase": { "success": true, "summary": "Folder was new.", "details": "" },
                    "positivePhase": { "success": true, "summary": "File detection exists check passed.", "details": "" }
                  },
                  "rule": {
                    "ruleType": "File",
                    "file": {
                      "path": "C:\\Program Files",
                      "fileOrFolderName": "Contoso",
                      "operator": "Exists",
                      "value": ""
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
            Assert.Equal(2, result.CandidateCount);
            Assert.Equal(1, result.ProvenCandidateCount);
            Assert.NotNull(result.BestCandidate);
            Assert.Equal(IntuneDetectionRuleType.File, result.BestCandidate.Rule.RuleType);
            Assert.True(result.BestCandidate.IsProven);
            Assert.Contains("1 proven", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ReadResultAsync_WhenScoredNativeCandidatesExist_SelectsHighestScore()
    {
        var tempRoot = CreateTempDirectory();
        var resultPath = Path.Combine(tempRoot, "result.json");
        await File.WriteAllTextAsync(resultPath, """
            {
              "schemaVersion": 2,
              "candidates": [
                {
                  "type": "Registry",
                  "confidence": "High",
                  "score": 90,
                  "reason": "New uninstall entry with DisplayVersion after install.",
                  "proof": {
                    "success": true,
                    "summary": "Candidate passed sandbox two-phase validation.",
                    "negativePhase": { "success": true, "summary": "Registry entry was new.", "details": "" },
                    "positivePhase": { "success": true, "summary": "Registry comparison passed.", "details": "" }
                  },
                  "rule": {
                    "ruleType": "Registry",
                    "registry": {
                      "hive": "HKEY_LOCAL_MACHINE",
                      "keyPath": "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Contoso",
                      "valueName": "DisplayVersion",
                      "operator": "GreaterThanOrEqual",
                      "value": "1.0"
                    }
                  }
                },
                {
                  "type": "File",
                  "confidence": "High",
                  "score": 94,
                  "reason": "New shortcut target points to the installed application executable.",
                  "proof": {
                    "success": true,
                    "summary": "Candidate passed sandbox two-phase validation.",
                    "negativePhase": { "success": true, "summary": "Shortcut was new.", "details": "" },
                    "positivePhase": { "success": true, "summary": "File comparison passed.", "details": "" }
                  },
                  "rule": {
                    "ruleType": "File",
                    "file": {
                      "path": "C:\\Program Files\\Contoso",
                      "fileOrFolderName": "Contoso.exe",
                      "operator": "GreaterThanOrEqual",
                      "value": "1.0"
                    }
                  },
                  "additionalRules": [
                    {
                      "ruleType": "Registry",
                      "registry": {
                        "hive": "HKEY_LOCAL_MACHINE",
                        "keyPath": "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Contoso",
                        "valueName": "Publisher",
                        "operator": "Equals",
                        "value": "Contoso Ltd"
                      }
                    }
                  ]
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
            Assert.Equal(94, result.BestCandidate.Score);
            Assert.Equal(@"C:\Program Files\Contoso", result.BestCandidate.Rule.File.Path);
            Assert.Equal("Contoso.exe", result.BestCandidate.Rule.File.FileOrFolderName);
            Assert.Single(result.BestCandidate.AdditionalRules);
            Assert.Equal(IntuneDetectionRuleType.Registry, result.BestCandidate.AdditionalRules[0].RuleType);
            Assert.Equal("Publisher", result.BestCandidate.AdditionalRules[0].Registry.ValueName);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ReadResultAsync_WhenMsiProductCodeCandidateExists_ParsesMsiRule()
    {
        var tempRoot = CreateTempDirectory();
        var resultPath = Path.Combine(tempRoot, "result.json");
        await File.WriteAllTextAsync(resultPath, """
            {
              "schemaVersion": 2,
              "candidates": [
                {
                  "type": "MsiProductCode",
                  "confidence": "High",
                  "score": 96,
                  "reason": "New MSI ProductCode registered after install.",
                  "proof": {
                    "success": true,
                    "summary": "Candidate passed sandbox two-phase validation.",
                    "negativePhase": { "success": true, "summary": "MSI entry was new.", "details": "" },
                    "positivePhase": { "success": true, "summary": "MSI version comparison passed.", "details": "" }
                  },
                  "rule": {
                    "ruleType": "MsiProductCode",
                    "msi": {
                      "productCode": "{11111111-2222-3333-4444-555555555555}",
                      "productVersion": "2.0.0",
                      "productVersionOperator": "GreaterThanOrEqual"
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
            Assert.NotNull(result.BestCandidate);
            Assert.Equal(IntuneDetectionRuleType.MsiProductCode, result.BestCandidate.Rule.RuleType);
            Assert.Equal("{11111111-2222-3333-4444-555555555555}", result.BestCandidate.Rule.Msi.ProductCode);
            Assert.Equal("2.0.0", result.BestCandidate.Rule.Msi.ProductVersion);
            Assert.Equal(IntuneDetectionOperator.GreaterThanOrEqual, result.BestCandidate.Rule.Msi.ProductVersionOperator);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ReadResultAsync_WhenProofAvailableAndAllCandidatesFail_ReturnsNoBestCandidate()
    {
        var tempRoot = CreateTempDirectory();
        var resultPath = Path.Combine(tempRoot, "result.json");
        await File.WriteAllTextAsync(resultPath, """
            {
              "schemaVersion": 2,
              "candidates": [
                {
                  "type": "Registry",
                  "confidence": "High",
                  "reason": "Registry candidate failed validation.",
                  "proof": {
                    "success": false,
                    "summary": "Candidate failed sandbox validation.",
                    "negativePhase": { "success": true, "summary": "Registry entry was new.", "details": "" },
                    "positivePhase": { "success": false, "summary": "Registry comparison failed.", "details": "" }
                  },
                  "rule": {
                    "ruleType": "Registry",
                    "registry": {
                      "hive": "HKEY_LOCAL_MACHINE",
                      "keyPath": "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Contoso",
                      "valueName": "DisplayVersion",
                      "operator": "GreaterThanOrEqual",
                      "value": "1.0"
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
            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(0, result.ProvenCandidateCount);
            Assert.Null(result.BestCandidate);
            Assert.Contains("none passed", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ReadResultAsync_WhenInstallReturns1602WithoutEvidence_ReturnsFailedResult()
    {
        var tempRoot = CreateTempDirectory();
        var resultPath = Path.Combine(tempRoot, "result.json");
        await File.WriteAllTextAsync(resultPath, """
            {
              "schemaVersion": 2,
              "install": {
                "exitCode": 1602,
                "timedOut": false,
                "stdout": "",
                "stderr": ""
              },
              "candidates": []
            }
            """);

        var sut = new SandboxProofService();

        try
        {
            var result = await sut.ReadResultAsync(resultPath);

            Assert.True(result.Completed);
            Assert.True(result.Failed);
            Assert.Equal(0, result.CandidateCount);
            Assert.Contains("1602", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("incorrect silent switches", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No install evidence", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ReadResultAsync_WhenLaunchValidationFailsWithProvenCandidates_AllowsDetectionImport()
    {
        var tempRoot = CreateTempDirectory();
        var resultPath = Path.Combine(tempRoot, "result.json");
        await File.WriteAllTextAsync(resultPath, """
            {
              "schemaVersion": 2,
              "failed": true,
              "error": "Installed application launched to a mostly blank/light window.",
              "install": {
                "exitCode": 0,
                "timedOut": false
              },
              "candidates": [
                {
                  "type": "Registry",
                  "confidence": "High",
                  "score": 90,
                  "reason": "New uninstall entry with DisplayVersion after install.",
                  "proof": {
                    "success": true,
                    "summary": "Candidate passed sandbox two-phase validation.",
                    "negativePhase": { "success": true, "summary": "Registry entry was new.", "details": "" },
                    "positivePhase": { "success": true, "summary": "Registry comparison passed.", "details": "" }
                  },
                  "rule": {
                    "ruleType": "Registry",
                    "registry": {
                      "hive": "HKEY_LOCAL_MACHINE",
                      "keyPath": "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{6E1A4B8C-7DB8-404F-BC14-2CA0F9FE8A41}_is1",
                      "valueName": "DisplayVersion",
                      "operator": "GreaterThanOrEqual",
                      "value": "2.23.29"
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
            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.ProvenCandidateCount);
            Assert.NotNull(result.BestCandidate);
            Assert.Equal(IntuneDetectionRuleType.Registry, result.BestCandidate.Rule.RuleType);
            Assert.Contains("launch validation warning", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("proof is still valid", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ReadResultAsync_WhenLaunchValidationWarningIsNonBlocking_PreservesProofCommands()
    {
        var tempRoot = CreateTempDirectory();
        var resultPath = Path.Combine(tempRoot, "result.json");
        await File.WriteAllTextAsync(resultPath, """
            {
              "schemaVersion": 2,
              "failed": false,
              "failureKind": "LaunchValidation",
              "error": "Installed application launched to a mostly blank/light window.",
              "install": {
                "command": "\"Setup.exe\" /quiet",
                "exitCode": 0,
                "timedOut": false
              },
              "uninstall": {
                "command": "\"C:\\Program Files\\Vendor\\unins000.exe\" /VERYSILENT /NORESTART",
                "exitCode": 0,
                "timedOut": false
              },
              "uninstallResolution": {
                "source": "Auto-discovered UninstallString: Vendor App",
                "summary": "Uninstall command discovered from the new uninstall registry entry."
              },
              "uninstallValidation": {
                "success": true
              },
              "candidates": [
                {
                  "type": "File",
                  "confidence": "High",
                  "score": 92,
                  "reason": "New uninstall entry InstallLocation contains an executable install footprint.",
                  "proof": {
                    "success": true,
                    "summary": "Candidate passed sandbox install, detection, and uninstall validation.",
                    "negativePhase": { "success": true, "summary": "Executable target was absent before install and present after install.", "details": "" },
                    "positivePhase": { "success": true, "summary": "Rule set validation passed for 1 rule(s).", "details": "" },
                    "uninstallPhase": { "success": true, "summary": "Uninstall validation cleared 1 detection rule(s).", "details": "" }
                  },
                  "rule": {
                    "ruleType": "File",
                    "file": {
                      "path": "C:\\Program Files\\Vendor",
                      "fileOrFolderName": "Vendor.exe",
                      "operator": "Exists"
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
            Assert.Equal("\"Setup.exe\" /quiet", result.InstallCommand);
            Assert.Equal("\"C:\\Program Files\\Vendor\\unins000.exe\" /VERYSILENT /NORESTART", result.UninstallCommand);
            Assert.Equal("Auto-discovered UninstallString: Vendor App", result.UninstallCommandSource);
            Assert.True(result.UninstallProven);
            Assert.Contains("launch validation warning", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ReadResultAsync_WhenUninstallValidationFailsWithProvenCandidates_ReturnsFailedResult()
    {
        var tempRoot = CreateTempDirectory();
        var resultPath = Path.Combine(tempRoot, "result.json");
        await File.WriteAllTextAsync(resultPath, """
            {
              "schemaVersion": 2,
              "failed": true,
              "failureKind": "Uninstall",
              "error": "Uninstall command completed, but no detection candidate cleared after uninstall.",
              "install": {
                "exitCode": 0,
                "timedOut": false
              },
              "uninstall": {
                "exitCode": 0,
                "timedOut": false
              },
              "uninstallValidation": {
                "success": false,
                "summary": "Uninstall command completed, but no detection candidate cleared after uninstall."
              },
              "candidates": [
                {
                  "type": "Registry",
                  "confidence": "High",
                  "score": 90,
                  "reason": "New uninstall entry with DisplayVersion after install.",
                  "proof": {
                    "success": true,
                    "summary": "Candidate passed sandbox install, detection, and uninstall validation.",
                    "negativePhase": { "success": true, "summary": "Registry entry was new.", "details": "" },
                    "positivePhase": { "success": true, "summary": "Registry comparison passed.", "details": "" },
                    "uninstallPhase": { "success": false, "summary": "Uninstall validation failed for 1 of 1 detection rule(s).", "details": "" }
                  },
                  "rule": {
                    "ruleType": "Registry",
                    "registry": {
                      "hive": "HKEY_LOCAL_MACHINE",
                      "keyPath": "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Example",
                      "valueName": "DisplayVersion",
                      "operator": "GreaterThanOrEqual",
                      "value": "1.0.0"
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
            Assert.True(result.Failed);
            Assert.Equal("Uninstall", result.FailureKind);
            Assert.True(result.InstallProven);
            Assert.False(result.UninstallProven);
            Assert.Equal(1, result.CandidateCount);
            Assert.Equal(1, result.ProvenCandidateCount);
            Assert.Contains("Uninstall command completed", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ReadResultAsync_WhenInstallTimesOutWithoutEvidence_ReturnsFailedResult()
    {
        var tempRoot = CreateTempDirectory();
        var resultPath = Path.Combine(tempRoot, "result.json");
        await File.WriteAllTextAsync(resultPath, """
            {
              "schemaVersion": 2,
              "install": {
                "exitCode": -1,
                "timedOut": true,
                "stdout": "",
                "stderr": ""
              },
              "candidates": []
            }
            """);

        var sut = new SandboxProofService();

        try
        {
            var result = await sut.ReadResultAsync(resultPath);

            Assert.True(result.Completed);
            Assert.True(result.Failed);
            Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Intune", result.Message, StringComparison.OrdinalIgnoreCase);
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
