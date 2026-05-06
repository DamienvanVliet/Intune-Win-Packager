using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Infrastructure.Support;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Infrastructure.Services;

public sealed class SandboxProofService : ISandboxProofService
{
    private const string SandboxProofRoot = @"C:\IwpSandboxProof";
    private const string SandboxSourceRoot = @"C:\IwpSandboxSource";
    private static readonly Regex UnsafeFileNameCharacters = new(@"[^a-zA-Z0-9._-]+", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<SandboxProofSession> StartAsync(
        SandboxProofRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return Failed("Sandbox proof requires a request.");
        }

        if (string.IsNullOrWhiteSpace(request.SetupFilePath) || !File.Exists(request.SetupFilePath))
        {
            return Failed("Sandbox proof requires an existing setup file.");
        }

        DataPathProvider.EnsureBaseDirectory();
        Directory.CreateDirectory(DataPathProvider.SandboxProofRunsDirectory);

        var setupPath = Path.GetFullPath(request.SetupFilePath);
        var sourceFolder = ResolveSourceFolder(request.SourceFolder, setupPath);
        var runDirectory = BuildRunDirectory(setupPath);
        var inputDirectory = Path.Combine(runDirectory, "input");
        var logsDirectory = Path.Combine(runDirectory, "logs");

        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(logsDirectory);

        var setupSandboxPath = TryMapSetupIntoSource(sourceFolder, setupPath, out var relativeSetupPath)
            ? CombineSandboxPath(SandboxSourceRoot, relativeSetupPath)
            : CopySetupIntoRunInput(setupPath, inputDirectory);

        var sandboxWorkingDirectory = Path.GetDirectoryName(setupSandboxPath) ?? SandboxSourceRoot;
        var sandboxInstallCommand = RewriteCommandForSandbox(
            string.IsNullOrWhiteSpace(request.InstallCommand) ? BuildDefaultInstallCommand(request.InstallerType, setupSandboxPath) : request.InstallCommand,
            setupPath,
            setupSandboxPath,
            sourceFolder,
            SandboxSourceRoot);
        var sandboxUninstallCommand = RewriteCommandForSandbox(
            request.UninstallCommand,
            setupPath,
            setupSandboxPath,
            sourceFolder,
            SandboxSourceRoot);

        var proofInput = new SandboxProofInput
        {
            InstallerType = request.InstallerType,
            HostSetupFilePath = setupPath,
            HostSourceFolder = sourceFolder,
            SandboxSetupFilePath = setupSandboxPath,
            SandboxWorkingDirectory = sandboxWorkingDirectory,
            InstallCommand = sandboxInstallCommand,
            UninstallCommand = sandboxUninstallCommand,
            DetectionRule = request.DetectionRule,
            TimeoutMinutes = Math.Clamp(request.TimeoutMinutes, 5, 240),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var inputPath = Path.Combine(runDirectory, "proof-input.json");
        var scriptPath = Path.Combine(runDirectory, "run-proof.ps1");
        var wsbPath = Path.Combine(runDirectory, "SandboxProof.wsb");
        var reportPath = Path.Combine(runDirectory, "report.txt");
        var resultPath = Path.Combine(runDirectory, "result.json");

        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(proofInput, JsonOptions), Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(scriptPath, BuildRunnerScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
        await File.WriteAllTextAsync(wsbPath, BuildWsbConfiguration(runDirectory, sourceFolder), Encoding.UTF8, cancellationToken);

        if (!request.LaunchSandbox)
        {
            return new SandboxProofSession
            {
                Success = true,
                Launched = false,
                Message = "Sandbox proof workspace created.",
                RunDirectory = runDirectory,
                WsbPath = wsbPath,
                InputPath = inputPath,
                RunnerScriptPath = scriptPath,
                ReportPath = reportPath,
                ResultPath = resultPath
            };
        }

        if (!OperatingSystem.IsWindows())
        {
            return SessionFailure("Windows Sandbox proof is only supported on Windows.", runDirectory, wsbPath, inputPath, scriptPath, reportPath, resultPath);
        }

        if (!IsWindowsSandboxAvailable())
        {
            return SessionFailure(
                "Windows Sandbox does not appear to be enabled. Enable the Windows Sandbox optional feature and try again.",
                runDirectory,
                wsbPath,
                inputPath,
                scriptPath,
                reportPath,
                resultPath);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = wsbPath,
                UseShellExecute = true,
                WorkingDirectory = runDirectory
            });

            return new SandboxProofSession
            {
                Success = true,
                Launched = true,
                Message = "Windows Sandbox proof launched. Wait for the sandbox run to finish, then open the report from the run folder.",
                RunDirectory = runDirectory,
                WsbPath = wsbPath,
                InputPath = inputPath,
                RunnerScriptPath = scriptPath,
                ReportPath = reportPath,
                ResultPath = resultPath
            };
        }
        catch (Exception ex)
        {
            return SessionFailure($"Windows Sandbox could not be launched: {ex.Message}", runDirectory, wsbPath, inputPath, scriptPath, reportPath, resultPath);
        }
    }

    private static SandboxProofSession Failed(string message)
    {
        return new SandboxProofSession
        {
            Success = false,
            Message = message
        };
    }

    private static SandboxProofSession SessionFailure(
        string message,
        string runDirectory,
        string wsbPath,
        string inputPath,
        string scriptPath,
        string reportPath,
        string resultPath)
    {
        return new SandboxProofSession
        {
            Success = false,
            Launched = false,
            Message = message,
            RunDirectory = runDirectory,
            WsbPath = wsbPath,
            InputPath = inputPath,
            RunnerScriptPath = scriptPath,
            ReportPath = reportPath,
            ResultPath = resultPath
        };
    }

    private static string ResolveSourceFolder(string sourceFolder, string setupPath)
    {
        if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder))
        {
            return Path.GetFullPath(sourceFolder);
        }

        return Path.GetDirectoryName(setupPath) ?? Environment.CurrentDirectory;
    }

    private static string BuildRunDirectory(string setupPath)
    {
        var packageName = Path.GetFileNameWithoutExtension(setupPath);
        if (string.IsNullOrWhiteSpace(packageName))
        {
            packageName = "package";
        }

        packageName = UnsafeFileNameCharacters.Replace(packageName, "-").Trim('-', '.');
        if (string.IsNullOrWhiteSpace(packageName))
        {
            packageName = "package";
        }

        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(DataPathProvider.SandboxProofRunsDirectory, $"{timestamp}-{packageName}");
    }

    private static bool TryMapSetupIntoSource(string sourceFolder, string setupPath, out string relativeSetupPath)
    {
        relativeSetupPath = string.Empty;

        try
        {
            var relative = Path.GetRelativePath(sourceFolder, setupPath);
            if (string.IsNullOrWhiteSpace(relative) ||
                relative.StartsWith("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                return false;
            }

            relativeSetupPath = relative;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CopySetupIntoRunInput(string setupPath, string inputDirectory)
    {
        var target = Path.Combine(inputDirectory, Path.GetFileName(setupPath));
        File.Copy(setupPath, target, overwrite: true);
        return CombineSandboxPath(SandboxProofRoot, "input", Path.GetFileName(setupPath));
    }

    private static string CombineSandboxPath(params string[] parts)
    {
        return string.Join("\\", parts.Select(part => part.Trim('\\')));
    }

    private static string BuildDefaultInstallCommand(InstallerType installerType, string setupSandboxPath)
    {
        return installerType == InstallerType.Msi
            ? $"msiexec /i {QuoteCommandValue(setupSandboxPath)} /qn /norestart"
            : QuoteCommandValue(setupSandboxPath);
    }

    private static string RewriteCommandForSandbox(
        string command,
        string setupHostPath,
        string setupSandboxPath,
        string sourceHostFolder,
        string sourceSandboxFolder)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var rewritten = command;
        rewritten = ReplacePath(rewritten, setupHostPath, setupSandboxPath);
        rewritten = ReplacePath(rewritten, sourceHostFolder, sourceSandboxFolder);
        return rewritten;
    }

    private static string ReplacePath(string input, string hostPath, string sandboxPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return input;
        }

        var normalizedHost = Path.GetFullPath(hostPath).TrimEnd('\\');
        return input
            .Replace($"\"{normalizedHost}\"", QuoteCommandValue(sandboxPath), StringComparison.OrdinalIgnoreCase)
            .Replace(normalizedHost, sandboxPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteCommandValue(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static bool IsWindowsSandboxAvailable()
    {
        var sandboxExe = Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe");
        return File.Exists(sandboxExe);
    }

    private static string BuildWsbConfiguration(string runDirectory, string sourceFolder)
    {
        var command = @"powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ""C:\IwpSandboxProof\run-proof.ps1""";

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            Encoding = Encoding.UTF8
        };

        var builder = new StringBuilder();
        using var writer = XmlWriter.Create(builder, settings);
        writer.WriteStartElement("Configuration");
        writer.WriteElementString("Networking", "Enable");
        writer.WriteStartElement("MappedFolders");
        WriteMappedFolder(writer, runDirectory, SandboxProofRoot, readOnly: false);
        WriteMappedFolder(writer, sourceFolder, SandboxSourceRoot, readOnly: true);
        writer.WriteEndElement();
        writer.WriteStartElement("LogonCommand");
        writer.WriteElementString("Command", command);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.Flush();

        return builder.ToString();
    }

    private static void WriteMappedFolder(XmlWriter writer, string hostFolder, string sandboxFolder, bool readOnly)
    {
        writer.WriteStartElement("MappedFolder");
        writer.WriteElementString("HostFolder", hostFolder);
        writer.WriteElementString("SandboxFolder", sandboxFolder);
        writer.WriteElementString("ReadOnly", readOnly ? "true" : "false");
        writer.WriteEndElement();
    }

    private static string BuildRunnerScript()
    {
        return """
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$ProofRoot = 'C:\IwpSandboxProof'
$InputPath = Join-Path $ProofRoot 'proof-input.json'
$ReportPath = Join-Path $ProofRoot 'report.txt'
$ResultPath = Join-Path $ProofRoot 'result.json'
$LogsPath = Join-Path $ProofRoot 'logs'
$TranscriptPath = Join-Path $LogsPath 'transcript.txt'
$DetectionScriptPath = Join-Path $ProofRoot 'detection-script.ps1'
$CompletedMarkerPath = Join-Path $ProofRoot 'completed.marker'

New-Item -ItemType Directory -Path $LogsPath -Force | Out-Null

function Write-ProofLog {
    param([string]$Message)
    $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -LiteralPath (Join-Path $LogsPath 'proof.log') -Value $line
}

function ConvertTo-PlainObject {
    param($Value)
    if ($null -eq $Value) { return $null }
    return $Value
}

function Get-UninstallSnapshot {
    $roots = @(
        @{ Hive = 'HKLM'; Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'; KeyPrefix = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' },
        @{ Hive = 'HKLM32'; Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'; KeyPrefix = 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' },
        @{ Hive = 'HKCU'; Path = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'; KeyPrefix = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' }
    )

    $items = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root.Path)) { continue }
        foreach ($key in Get-ChildItem -LiteralPath $root.Path -ErrorAction SilentlyContinue) {
            try {
                $props = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction Stop
                $keyPath = '{0}\{1}' -f $root.KeyPrefix, $key.PSChildName
                $items += [pscustomobject]@{
                    id = '{0}|{1}' -f $root.Hive, $key.PSChildName
                    hive = $root.Hive
                    keyPath = $keyPath
                    keyName = $key.PSChildName
                    displayName = [string]$props.DisplayName
                    displayVersion = [string]$props.DisplayVersion
                    publisher = [string]$props.Publisher
                    installLocation = [string]$props.InstallLocation
                    displayIcon = [string]$props.DisplayIcon
                    uninstallString = [string]$props.UninstallString
                    quietUninstallString = [string]$props.QuietUninstallString
                    systemComponent = [string]$props.SystemComponent
                }
            }
            catch {
                Write-ProofLog "Failed to read uninstall key $($key.Name): $($_.Exception.Message)"
            }
        }
    }

    return @($items)
}

function Get-ProgramDirectorySnapshot {
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    $items = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            $items += [pscustomobject]@{
                id = $directory.FullName.ToLowerInvariant()
                root = $root
                name = $directory.Name
                fullName = $directory.FullName
                lastWriteTimeUtc = $directory.LastWriteTimeUtc.ToString('o')
            }
        }
    }

    return @($items)
}

function Get-ServiceSnapshot {
    try {
        return @(Get-Service | ForEach-Object {
            [pscustomobject]@{
                id = $_.Name
                name = $_.Name
                displayName = $_.DisplayName
                status = [string]$_.Status
            }
        })
    }
    catch {
        Write-ProofLog "Service snapshot failed: $($_.Exception.Message)"
        return @()
    }
}

function Get-TaskSnapshot {
    try {
        return @(Get-ScheduledTask -ErrorAction SilentlyContinue | ForEach-Object {
            [pscustomobject]@{
                id = '{0}{1}' -f $_.TaskPath, $_.TaskName
                taskPath = $_.TaskPath
                taskName = $_.TaskName
                state = [string]$_.State
            }
        })
    }
    catch {
        Write-ProofLog "Scheduled task snapshot failed: $($_.Exception.Message)"
        return @()
    }
}

function Get-ShortcutSnapshot {
    $roots = @(
        "$env:ProgramData\Microsoft\Windows\Start Menu\Programs",
        "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
    )

    $items = @()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($shortcut in Get-ChildItem -LiteralPath $root -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue) {
            $items += [pscustomobject]@{
                id = $shortcut.FullName.ToLowerInvariant()
                name = $shortcut.Name
                fullName = $shortcut.FullName
                lastWriteTimeUtc = $shortcut.LastWriteTimeUtc.ToString('o')
            }
        }
    }

    return @($items)
}

function Get-Snapshot {
    param([string]$Name)
    Write-ProofLog "Capturing $Name snapshot"
    return [pscustomobject]@{
        name = $Name
        capturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        uninstallEntries = @(Get-UninstallSnapshot)
        programDirectories = @(Get-ProgramDirectorySnapshot)
        services = @(Get-ServiceSnapshot)
        scheduledTasks = @(Get-TaskSnapshot)
        shortcuts = @(Get-ShortcutSnapshot)
    }
}

function Compare-ById {
    param($Before, $After)
    $beforeIds = @{}
    foreach ($item in @($Before)) {
        if ($null -ne $item.id) { $beforeIds[[string]$item.id] = $true }
    }

    $newItems = @()
    foreach ($item in @($After)) {
        if ($null -eq $item.id -or -not $beforeIds.ContainsKey([string]$item.id)) {
            $newItems += $item
        }
    }

    return @($newItems)
}

function Compare-ProofValue {
    param([string]$Actual, [string]$Expected, [string]$Operator)
    $Actual = ([string]$Actual).Trim()
    $Expected = ([string]$Expected).Trim()
    if ($Operator -eq 'Exists') { return -not [string]::IsNullOrWhiteSpace($Actual) }
    if ($Operator -eq 'Equals') { return $Actual -ieq $Expected }
    if ($Operator -eq 'NotEquals') { return $Actual -ine $Expected }

    $actualVersion = $null
    $expectedVersion = $null
    if ([version]::TryParse($Actual, [ref]$actualVersion) -and [version]::TryParse($Expected, [ref]$expectedVersion)) {
        $compare = $actualVersion.CompareTo($expectedVersion)
    }
    else {
        $actualNumber = 0.0
        $expectedNumber = 0.0
        if ([double]::TryParse($Actual, [System.Globalization.NumberStyles]::Any, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$actualNumber) -and
            [double]::TryParse($Expected, [System.Globalization.NumberStyles]::Any, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$expectedNumber)) {
            $compare = $actualNumber.CompareTo($expectedNumber)
        }
        else {
            $compare = [string]::Compare($Actual, $Expected, $true, [System.Globalization.CultureInfo]::InvariantCulture)
        }
    }

    switch ($Operator) {
        'GreaterThan' { return $compare -gt 0 }
        'GreaterThanOrEqual' { return $compare -ge 0 }
        'LessThan' { return $compare -lt 0 }
        'LessThanOrEqual' { return $compare -le 0 }
        default { return $false }
    }
}

function Test-ProofRegistryDetection {
    param($Rule)
    if ($null -eq $Rule -or [string]::IsNullOrWhiteSpace($Rule.keyPath)) {
        return [pscustomobject]@{ success = $false; summary = 'Registry detection missing key path.'; details = '' }
    }

    $hive = [string]$Rule.hive
    $providerRoot = switch -Regex ($hive) {
        'HKCU|HKEY_CURRENT_USER' { 'HKCU:'; break }
        default { 'HKLM:' }
    }

    $keyPath = [string]$Rule.keyPath
    if ($Rule.check32BitOn64System -and $providerRoot -eq 'HKLM:' -and $keyPath -like 'SOFTWARE\*' -and $keyPath -notlike 'SOFTWARE\WOW6432Node\*') {
        $keyPath = $keyPath -replace '^SOFTWARE\\', 'SOFTWARE\WOW6432Node\'
    }

    $fullPath = Join-Path $providerRoot $keyPath
    $exists = Test-Path -LiteralPath $fullPath
    if ($Rule.operator -eq 'Exists') {
        if (-not $exists) { return [pscustomobject]@{ success = $false; summary = 'Registry key not found.'; details = $fullPath } }
        if ([string]::IsNullOrWhiteSpace($Rule.valueName)) {
            return [pscustomobject]@{ success = $true; summary = 'Registry key exists.'; details = $fullPath }
        }

        $props = Get-ItemProperty -LiteralPath $fullPath -ErrorAction SilentlyContinue
        $valueProperty = $props.PSObject.Properties[[string]$Rule.valueName]
        $value = if ($null -ne $valueProperty) { $valueProperty.Value } else { $null }
        return [pscustomobject]@{
            success = $null -ne $value
            summary = if ($null -ne $value) { 'Registry value exists.' } else { 'Registry value not found.' }
            details = "$fullPath :: $($Rule.valueName)"
        }
    }

    if (-not $exists -or [string]::IsNullOrWhiteSpace($Rule.valueName)) {
        return [pscustomobject]@{ success = $false; summary = 'Registry comparison target not found.'; details = $fullPath }
    }

    $props = Get-ItemProperty -LiteralPath $fullPath -ErrorAction SilentlyContinue
    $valueProperty = $props.PSObject.Properties[[string]$Rule.valueName]
    $actual = if ($null -ne $valueProperty) { [string]$valueProperty.Value } else { '' }
    $ok = Compare-ProofValue -Actual $actual -Expected ([string]$Rule.value) -Operator ([string]$Rule.operator)
    return [pscustomobject]@{
        success = $ok
        summary = if ($ok) { 'Registry comparison passed.' } else { 'Registry comparison failed.' }
        details = "Actual='$actual', Expected='$($Rule.value)', Operator='$($Rule.operator)'"
    }
}

function Test-ProofFileDetection {
    param($Rule)
    if ($null -eq $Rule -or [string]::IsNullOrWhiteSpace($Rule.path) -or [string]::IsNullOrWhiteSpace($Rule.fileOrFolderName)) {
        return [pscustomobject]@{ success = $false; summary = 'File detection missing path or name.'; details = '' }
    }

    $target = Join-Path ([Environment]::ExpandEnvironmentVariables([string]$Rule.path)) ([string]$Rule.fileOrFolderName)
    $fileExists = Test-Path -LiteralPath $target -PathType Leaf
    $folderExists = Test-Path -LiteralPath $target -PathType Container
    if ($Rule.operator -eq 'Exists') {
        return [pscustomobject]@{
            success = ($fileExists -or $folderExists)
            summary = if ($fileExists -or $folderExists) { 'File detection exists check passed.' } else { 'File detection target missing.' }
            details = $target
        }
    }

    if (-not ($fileExists -or $folderExists)) {
        return [pscustomobject]@{ success = $false; summary = 'File comparison target missing.'; details = $target }
    }

    $actual = ''
    if ($fileExists) {
        $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($target)
        $actual = [string]$versionInfo.ProductVersion
        if ([string]::IsNullOrWhiteSpace($actual)) {
            $actual = [string]$versionInfo.FileVersion
        }
    }
    if ([string]::IsNullOrWhiteSpace($actual)) {
        $actual = if ($fileExists) { [string](Get-Item -LiteralPath $target).Length } else { [string](Get-Item -LiteralPath $target).LastWriteTimeUtc.Ticks }
    }

    $ok = Compare-ProofValue -Actual $actual -Expected ([string]$Rule.value) -Operator ([string]$Rule.operator)
    return [pscustomobject]@{
        success = $ok
        summary = if ($ok) { 'File comparison passed.' } else { 'File comparison failed.' }
        details = "Target='$target', Actual='$actual', Expected='$($Rule.value)', Operator='$($Rule.operator)'"
    }
}

function Test-ProofMsiDetection {
    param($Rule)
    $productCode = ([string]$Rule.productCode).Trim()
    if ([string]::IsNullOrWhiteSpace($productCode)) {
        return [pscustomobject]@{ success = $false; summary = 'MSI detection missing ProductCode.'; details = '' }
    }

    $roots = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$productCode",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$productCode",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$productCode"
    )

    foreach ($path in $roots) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        if ([string]::IsNullOrWhiteSpace($Rule.productVersion)) {
            return [pscustomobject]@{ success = $true; summary = 'MSI ProductCode exists.'; details = $path }
        }

        $props = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
        $actual = [string]$props.DisplayVersion
        $ok = Compare-ProofValue -Actual $actual -Expected ([string]$Rule.productVersion) -Operator ([string]$Rule.productVersionOperator)
        return [pscustomobject]@{
            success = $ok
            summary = if ($ok) { 'MSI version comparison passed.' } else { 'MSI version comparison failed.' }
            details = "Path='$path', Actual='$actual', Expected='$($Rule.productVersion)'"
        }
    }

    return [pscustomobject]@{ success = $false; summary = 'MSI ProductCode not found.'; details = $productCode }
}

function Test-ProofScriptDetection {
    param($Rule)
    if ($null -eq $Rule -or [string]::IsNullOrWhiteSpace($Rule.scriptBody)) {
        return [pscustomobject]@{ success = $false; summary = 'Script detection body is empty.'; details = '' }
    }

    Set-Content -LiteralPath $DetectionScriptPath -Value ([string]$Rule.scriptBody) -Encoding UTF8
    $stdout = Join-Path $LogsPath 'detection-stdout.txt'
    $stderr = Join-Path $LogsPath 'detection-stderr.txt'
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File', $DetectionScriptPath) -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $stdOutText = if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -Raw } else { '' }
    $stdErrText = if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -Raw } else { '' }
    $ok = $process.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($stdOutText) -and [string]::IsNullOrWhiteSpace($stdErrText)
    return [pscustomobject]@{
        success = $ok
        summary = if ($ok) { 'Script detection passed Intune signal checks.' } else { 'Script detection failed Intune signal checks.' }
        details = "ExitCode=$($process.ExitCode), HasStdOut=$(-not [string]::IsNullOrWhiteSpace($stdOutText)), HasStdErr=$(-not [string]::IsNullOrWhiteSpace($stdErrText))"
    }
}

function Test-ProofDetection {
    param($Rule)
    if ($null -eq $Rule -or $Rule.ruleType -eq 'None') {
        return [pscustomobject]@{ success = $false; summary = 'No detection rule configured.'; details = '' }
    }

    switch ([string]$Rule.ruleType) {
        'MsiProductCode' { return Test-ProofMsiDetection -Rule $Rule.msi }
        'File' { return Test-ProofFileDetection -Rule $Rule.file }
        'Registry' { return Test-ProofRegistryDetection -Rule $Rule.registry }
        'Script' { return Test-ProofScriptDetection -Rule $Rule.script }
        default { return [pscustomobject]@{ success = $false; summary = "Unsupported detection rule type '$($Rule.ruleType)'."; details = '' } }
    }
}

function Invoke-ProofCommand {
    param([string]$Command, [string]$WorkingDirectory, [int]$TimeoutMinutes)
    if ([string]::IsNullOrWhiteSpace($Command)) {
        return [pscustomobject]@{ exitCode = -1; timedOut = $false; stdout = ''; stderr = 'No command was provided.' }
    }

    if ([string]::IsNullOrWhiteSpace($WorkingDirectory) -or -not (Test-Path -LiteralPath $WorkingDirectory)) {
        $WorkingDirectory = $ProofRoot
    }

    $stdout = Join-Path $LogsPath 'install-stdout.txt'
    $stderr = Join-Path $LogsPath 'install-stderr.txt'
    Write-ProofLog "Running command: $Command"
    Write-ProofLog "Working directory: $WorkingDirectory"

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'cmd.exe'
    $psi.Arguments = "/d /c $Command"
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    [void]$process.Start()

    $stdOutTask = $process.StandardOutput.ReadToEndAsync()
    $stdErrTask = $process.StandardError.ReadToEndAsync()
    $timeoutMs = [Math]::Max(5, $TimeoutMinutes) * 60 * 1000
    $exited = $process.WaitForExit($timeoutMs)
    if (-not $exited) {
        try { $process.Kill() } catch {}
    }

    $stdoutText = $stdOutTask.GetAwaiter().GetResult()
    $stderrText = $stdErrTask.GetAwaiter().GetResult()
    Set-Content -LiteralPath $stdout -Value $stdoutText -Encoding UTF8
    Set-Content -LiteralPath $stderr -Value $stderrText -Encoding UTF8

    return [pscustomobject]@{
        exitCode = if ($exited) { $process.ExitCode } else { -1 }
        timedOut = -not $exited
        stdout = $stdoutText
        stderr = $stderrText
    }
}

function Resolve-PathCandidate {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    $trimmed = $Value.Trim()
    if ($trimmed -match '^\s*"([^"]+)"') { return $matches[1] }
    if ($trimmed -match '([A-Za-z]:\\[^\s,]+\.exe)') { return $matches[1] }
    if ($trimmed -match '([A-Za-z]:\\[^\s,]+\.msi)') { return $matches[1] }
    return ''
}

function Split-FileDetectionTarget {
    param([string]$Target)
    if ([string]::IsNullOrWhiteSpace($Target) -or -not (Test-Path -LiteralPath $Target)) { return $null }
    $item = Get-Item -LiteralPath $Target -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    return [pscustomobject]@{
        path = if ($item.PSIsContainer) { $item.Parent.FullName } else { $item.DirectoryName }
        name = $item.Name
        isFile = -not $item.PSIsContainer
        version = if (-not $item.PSIsContainer) {
            $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($item.FullName)
            $version = [string]$versionInfo.ProductVersion
            if ([string]::IsNullOrWhiteSpace($version)) { $version = [string]$versionInfo.FileVersion }
            $version
        } else { '' }
    }
}

function New-Candidate {
    param([string]$Type, [string]$Confidence, [string]$Reason, $Rule, $Evidence)
    return [pscustomobject]@{
        type = $Type
        confidence = $Confidence
        reason = $Reason
        rule = $Rule
        evidence = $Evidence
    }
}

function Get-DetectionCandidates {
    param($NewUninstallEntries, $NewProgramDirectories)
    $candidates = @()

    foreach ($entry in @($NewUninstallEntries)) {
        if (-not [string]::IsNullOrWhiteSpace($entry.displayName) -and -not [string]::IsNullOrWhiteSpace($entry.displayVersion)) {
            $candidates += New-Candidate -Type 'Registry' -Confidence 'High' -Reason 'New uninstall entry with DisplayVersion after install.' -Evidence $entry -Rule ([pscustomobject]@{
                ruleType = 'Registry'
                hive = if ($entry.hive -eq 'HKCU') { 'HKEY_CURRENT_USER' } else { 'HKEY_LOCAL_MACHINE' }
                keyPath = $entry.keyPath
                valueName = 'DisplayVersion'
                check32BitOn64System = $false
                operator = 'Equals'
                value = $entry.displayVersion
            })
        }

        $targets = @($entry.displayIcon, $entry.uninstallString, $entry.quietUninstallString, $entry.installLocation)
        foreach ($targetValue in $targets) {
            $candidatePath = Resolve-PathCandidate -Value ([string]$targetValue)
            if ([string]::IsNullOrWhiteSpace($candidatePath) -and (Test-Path -LiteralPath ([string]$targetValue) -ErrorAction SilentlyContinue)) {
                $candidatePath = [string]$targetValue
            }

            $fileTarget = Split-FileDetectionTarget -Target $candidatePath
            if ($null -eq $fileTarget) { continue }

            $rule = [pscustomobject]@{
                ruleType = 'File'
                path = $fileTarget.path
                fileOrFolderName = $fileTarget.name
                check32BitOn64System = $false
                operator = if (-not [string]::IsNullOrWhiteSpace($fileTarget.version)) { 'GreaterThanOrEqual' } else { 'Exists' }
                value = if (-not [string]::IsNullOrWhiteSpace($fileTarget.version)) { $fileTarget.version } else { '' }
            }
            $candidates += New-Candidate -Type 'File' -Confidence 'High' -Reason 'New uninstall entry points to an existing install footprint.' -Evidence $entry -Rule $rule
            break
        }
    }

    foreach ($directory in @($NewProgramDirectories | Select-Object -First 10)) {
        $candidates += New-Candidate -Type 'File' -Confidence 'Medium' -Reason 'New top-level install directory appeared after install.' -Evidence $directory -Rule ([pscustomobject]@{
            ruleType = 'File'
            path = $directory.root
            fileOrFolderName = $directory.name
            check32BitOn64System = $false
            operator = 'Exists'
            value = ''
        })
    }

    return @($candidates)
}

function Write-Report {
    param($Result)
    $lines = @()
    $lines += 'Intune Win Packager - Windows Sandbox Proof'
    $lines += '================================================'
    $lines += ''
    $lines += "Installer type: $($Result.request.installerType)"
    $lines += "Setup path: $($Result.request.sandboxSetupFilePath)"
    $lines += "Install command: $($Result.request.installCommand)"
    $lines += "Install exit code: $($Result.install.exitCode)"
    $lines += "Install timed out: $($Result.install.timedOut)"
    $lines += ''
    $lines += "Pre-install detection: $($Result.preInstallDetection.success) - $($Result.preInstallDetection.summary)"
    $lines += "Post-install detection: $($Result.postInstallDetection.success) - $($Result.postInstallDetection.summary)"
    $lines += ''
    $lines += "New uninstall entries: $(@($Result.diff.newUninstallEntries).Count)"
    foreach ($entry in @($Result.diff.newUninstallEntries | Select-Object -First 12)) {
        $lines += "- $($entry.displayName) $($entry.displayVersion) [$($entry.hive)\$($entry.keyPath)]"
    }
    $lines += ''
    $lines += "New install directories: $(@($Result.diff.newProgramDirectories).Count)"
    foreach ($directory in @($Result.diff.newProgramDirectories | Select-Object -First 12)) {
        $lines += "- $($directory.fullName)"
    }
    $lines += ''
    $lines += "Detection candidates: $(@($Result.candidates).Count)"
    foreach ($candidate in @($Result.candidates | Select-Object -First 12)) {
        $lines += "- [$($candidate.confidence)] $($candidate.type): $($candidate.reason)"
        $lines += "  Rule: $($candidate.rule | ConvertTo-Json -Compress -Depth 8)"
    }
    $lines += ''
    $lines += 'Full machine-readable evidence is available in result.json.'
    Set-Content -LiteralPath $ReportPath -Value $lines -Encoding UTF8
}

try {
    Start-Transcript -LiteralPath $TranscriptPath -Force | Out-Null
} catch {}

try {
    Write-ProofLog 'Starting sandbox proof run.'
    $inputData = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json

    $baseline = Get-Snapshot -Name 'baseline'
    $preDetection = Test-ProofDetection -Rule $inputData.detectionRule

    $installResult = Invoke-ProofCommand -Command ([string]$inputData.installCommand) -WorkingDirectory ([string]$inputData.sandboxWorkingDirectory) -TimeoutMinutes ([int]$inputData.timeoutMinutes)
    Start-Sleep -Seconds 3

    $postInstall = Get-Snapshot -Name 'post-install'
    $postDetection = Test-ProofDetection -Rule $inputData.detectionRule

    $diff = [pscustomobject]@{
        newUninstallEntries = @(Compare-ById -Before $baseline.uninstallEntries -After $postInstall.uninstallEntries)
        newProgramDirectories = @(Compare-ById -Before $baseline.programDirectories -After $postInstall.programDirectories)
        newServices = @(Compare-ById -Before $baseline.services -After $postInstall.services)
        newScheduledTasks = @(Compare-ById -Before $baseline.scheduledTasks -After $postInstall.scheduledTasks)
        newShortcuts = @(Compare-ById -Before $baseline.shortcuts -After $postInstall.shortcuts)
    }

    $candidates = @(Get-DetectionCandidates -NewUninstallEntries $diff.newUninstallEntries -NewProgramDirectories $diff.newProgramDirectories)

    $result = [pscustomobject]@{
        schemaVersion = 1
        completedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        request = $inputData
        install = $installResult
        preInstallDetection = $preDetection
        postInstallDetection = $postDetection
        diff = $diff
        candidates = $candidates
        snapshots = [pscustomobject]@{
            baseline = $baseline
            postInstall = $postInstall
        }
    }

    $result | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    Write-Report -Result $result
    Set-Content -LiteralPath $CompletedMarkerPath -Value (Get-Date).ToUniversalTime().ToString('o') -Encoding UTF8
    Write-ProofLog 'Sandbox proof run completed.'
}
catch {
    $failure = [pscustomobject]@{
        schemaVersion = 1
        completedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        failed = $true
        error = $_.Exception.Message
    }
    $failure | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    Set-Content -LiteralPath $ReportPath -Value @('Sandbox proof failed.', $_.Exception.Message) -Encoding UTF8
    Write-ProofLog "Sandbox proof failed: $($_.Exception.Message)"
}
finally {
    try { Stop-Transcript | Out-Null } catch {}
}
""";
    }

    private sealed record SandboxProofInput
    {
        public InstallerType InstallerType { get; init; }

        public string HostSetupFilePath { get; init; } = string.Empty;

        public string HostSourceFolder { get; init; } = string.Empty;

        public string SandboxSetupFilePath { get; init; } = string.Empty;

        public string SandboxWorkingDirectory { get; init; } = string.Empty;

        public string InstallCommand { get; init; } = string.Empty;

        public string UninstallCommand { get; init; } = string.Empty;

        public IntuneDetectionRule DetectionRule { get; init; } = new();

        public int TimeoutMinutes { get; init; }

        public DateTimeOffset CreatedAtUtc { get; init; }
    }
}
