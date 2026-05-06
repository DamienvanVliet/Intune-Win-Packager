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
