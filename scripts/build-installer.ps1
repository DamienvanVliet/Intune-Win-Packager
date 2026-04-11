[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",

    [switch]$SkipInnoSetupInstall
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-IsccPath {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $candidates = @(
        "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    return $null
}

function Install-InnoSetup {
    Write-Step "Inno Setup is not installed. Installing with winget..."
    & winget install --id JRSoftware.InnoSetup --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity
    if ($LASTEXITCODE -ne 0) {
        # winget may return non-zero when package is already installed/no upgrade is available.
        $isccAfterInstall = Get-IsccPath
        if (-not $isccAfterInstall) {
            throw "Failed to install Inno Setup with winget. Exit code: $LASTEXITCODE"
        }

        Write-Warning "winget returned exit code $LASTEXITCODE, but Inno Setup is available. Continuing..."
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$projectPath = Join-Path $repoRoot "IntuneWinPackager.App\IntuneWinPackager.App.csproj"
$issPath = Join-Path $repoRoot "installer\IntuneWinPackager.iss"
$publishDir = Join-Path $repoRoot ("artifacts\publish\" + $Runtime)
$installerDir = Join-Path $repoRoot "artifacts\installer"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK not found. Install .NET 8 SDK first."
}

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file not found: $projectPath"
}

if (-not (Test-Path -LiteralPath $issPath)) {
    throw "Inno Setup script not found: $issPath"
}

Write-Step "Restoring packages..."
& dotnet restore $projectPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed. Exit code: $LASTEXITCODE"
}

Write-Step "Publishing self-contained app ($Runtime, $Configuration)..."
& dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    /p:PublishSingleFile=false `
    /p:PublishReadyToRun=true `
    /p:DebugSymbols=false `
    /p:DebugType=None
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed. Exit code: $LASTEXITCODE"
}

$appExePath = Join-Path $publishDir "IntuneWinPackager.App.exe"
if (-not (Test-Path -LiteralPath $appExePath)) {
    throw "Published executable not found: $appExePath"
}

$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($appExePath)
$appVersion = if ([string]::IsNullOrWhiteSpace($versionInfo.ProductVersion)) {
    "1.0.0"
}
else {
    $versionInfo.ProductVersion.Split('+')[0]
}

$isccPath = Get-IsccPath
if (-not $isccPath) {
    if ($SkipInnoSetupInstall) {
        throw "Inno Setup was not found. Install it first or run without -SkipInnoSetupInstall."
    }

    Install-InnoSetup
    $isccPath = Get-IsccPath

    if (-not $isccPath) {
        throw "Inno Setup still not found after installation."
    }
}

New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

Write-Step "Building installer with Inno Setup..."
& $isccPath `
    $issPath `
    "/DAppVersion=$appVersion" `
    "/DSourceDir=$publishDir" `
    "/DOutputDir=$installerDir"
if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed. Exit code: $LASTEXITCODE"
}

$latestInstaller = Get-ChildItem -Path $installerDir -Filter "IntuneWinPackager-Setup-*.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($latestInstaller) {
    Write-Host ""
    Write-Host "Installer ready:" -ForegroundColor Green
    Write-Host $latestInstaller.FullName
}
else {
    Write-Warning "Build completed but no installer executable was found in $installerDir"
}
