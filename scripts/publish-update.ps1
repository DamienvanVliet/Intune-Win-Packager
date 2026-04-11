[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ReleaseTitle = "",

    [string]$ReleaseNotes = "",

    [switch]$SkipBuild,

    [switch]$SkipGitPush
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Ensure-Command {
    param(
        [string]$Name,
        [string]$InstallHint
    )

    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    if ($Name -eq "gh") {
        $fallback = "C:\Program Files\GitHub CLI\gh.exe"
        if (Test-Path -LiteralPath $fallback) {
            return $fallback
        }
    }

    throw "$Name was not found. $InstallHint"
}

function Normalize-Version {
    param([string]$InputVersion)

    $trimmed = $InputVersion.Trim()
    if ($trimmed.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $trimmed.Substring(1)
    }

    return $trimmed
}

function Update-AppVersionInCsproj {
    param(
        [string]$CsprojPath,
        [string]$NewVersion
    )

    [xml]$xml = Get-Content $CsprojPath
    $propertyGroup = $xml.SelectSingleNode("/Project/PropertyGroup[last()]")

    if (-not $propertyGroup) {
        throw "Could not find PropertyGroup in $CsprojPath"
    }

    $versionNode = $propertyGroup.SelectSingleNode("Version")
    if (-not $versionNode) {
        $versionNode = $xml.CreateElement("Version")
        $propertyGroup.AppendChild($versionNode) | Out-Null
    }
    $versionNode.InnerText = $NewVersion

    $fileVersionNode = $propertyGroup.SelectSingleNode("FileVersion")
    if (-not $fileVersionNode) {
        $fileVersionNode = $xml.CreateElement("FileVersion")
        $propertyGroup.AppendChild($fileVersionNode) | Out-Null
    }
    $fileVersionNode.InnerText = "$NewVersion.0"

    $assemblyVersionNode = $propertyGroup.SelectSingleNode("AssemblyVersion")
    if (-not $assemblyVersionNode) {
        $assemblyVersionNode = $xml.CreateElement("AssemblyVersion")
        $propertyGroup.AppendChild($assemblyVersionNode) | Out-Null
    }
    $assemblyVersionNode.InnerText = "$NewVersion.0"

    $informationalNode = $propertyGroup.SelectSingleNode("InformationalVersion")
    if (-not $informationalNode) {
        $informationalNode = $xml.CreateElement("InformationalVersion")
        $propertyGroup.AppendChild($informationalNode) | Out-Null
    }
    $informationalNode.InnerText = $NewVersion

    $xml.Save($CsprojPath)
}

function Ensure-ChangelogEntry {
    param(
        [string]$ChangelogPath,
        [string]$Version,
        [string]$Notes
    )

    if (-not (Test-Path $ChangelogPath)) {
        Set-Content -Path $ChangelogPath -Value "# Changelog`r`n" -Encoding UTF8
    }

    $existing = Get-Content $ChangelogPath -Raw
    if ($existing -match "(?m)^## \[$([Regex]::Escape($Version))\]") {
        return
    }

    $today = Get-Date -Format "yyyy-MM-dd"
    $notesLines = @()

    if ([string]::IsNullOrWhiteSpace($Notes)) {
        $notesLines += "- Release published."
    }
    else {
        foreach ($line in ($Notes -split "(`r`n|`n)")) {
            $trim = $line.Trim()
            if ([string]::IsNullOrWhiteSpace($trim)) {
                continue
            }

            if ($trim.StartsWith("-")) {
                $notesLines += $trim
            }
            else {
                $notesLines += "- $trim"
            }
        }
    }

    $newEntry = @()
    $newEntry += "## [$Version] - $today"
    $newEntry += ""
    $newEntry += "### Added"
    $newEntry += $notesLines
    $newEntry += ""

    if ($existing.StartsWith("# Changelog")) {
        $insertIndex = $existing.IndexOf("`n")
        $prefix = $existing.Substring(0, $insertIndex + 1)
        $suffix = $existing.Substring($insertIndex + 1).TrimStart("`r", "`n")
        $result = $prefix + "`r`n" + ($newEntry -join "`r`n") + "`r`n" + $suffix
    }
    else {
        $result = "# Changelog`r`n`r`n" + ($newEntry -join "`r`n") + "`r`n" + $existing
    }

    [System.IO.File]::WriteAllText($ChangelogPath, $result, [System.Text.UTF8Encoding]::new($false))
}

$normalizedVersion = Normalize-Version -InputVersion $Version
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$appCsproj = Join-Path $repoRoot "IntuneWinPackager.App\IntuneWinPackager.App.csproj"
$changelogPath = Join-Path $repoRoot "CHANGELOG.md"
$installerDir = Join-Path $repoRoot "artifacts\installer"

Ensure-Command -Name git -InstallHint "Install Git first."
$ghPath = Ensure-Command -Name gh -InstallHint "Install GitHub CLI: winget install --id GitHub.cli --source winget"

Write-Step "Setting app version to $normalizedVersion"
Update-AppVersionInCsproj -CsprojPath $appCsproj -NewVersion $normalizedVersion

Write-Step "Ensuring changelog entry exists"
Ensure-ChangelogEntry -ChangelogPath $changelogPath -Version $normalizedVersion -Notes $ReleaseNotes

if (-not $SkipBuild) {
    Write-Step "Building installer"
    & powershell -ExecutionPolicy Bypass -File (Join-Path $repoRoot "scripts\build-installer.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "Installer build failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path $installerDir)) {
    throw "Installer output directory not found: $installerDir"
}

$installerPath = Join-Path $installerDir "IntuneWinPackager-Setup-$normalizedVersion.exe"
if (-not (Test-Path $installerPath)) {
    $latestInstaller = Get-ChildItem -Path $installerDir -Filter "IntuneWinPackager-Setup-*.exe" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $latestInstaller) {
        throw "No installer was found in $installerDir"
    }

    Copy-Item -LiteralPath $latestInstaller.FullName -Destination $installerPath -Force
}

$tagName = "v$normalizedVersion"
$finalTitle = if ([string]::IsNullOrWhiteSpace($ReleaseTitle)) { $tagName } else { $ReleaseTitle }
$notesFile = Join-Path $repoRoot "artifacts\release-notes-$normalizedVersion.md"
New-Item -ItemType Directory -Path (Split-Path $notesFile -Parent) -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    $ReleaseNotes = "See CHANGELOG.md for details."
}

[System.IO.File]::WriteAllText($notesFile, $ReleaseNotes, [System.Text.UTF8Encoding]::new($false))

if (-not $SkipGitPush) {
    Write-Step "Committing changelog/version changes"
    Push-Location $repoRoot
    try {
        git add $appCsproj $changelogPath
        if ((git diff --cached --name-only)) {
            git commit -m "chore(release): publish $tagName"
        }

        Write-Step "Pushing branch and tags"
        git push
    }
    finally {
        Pop-Location
    }
}

Write-Step "Creating or updating GitHub release $tagName"
Push-Location $repoRoot
try {
    $null = & $ghPath auth status 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI is not authenticated. Run: gh auth login"
    }

    $hasExistingRelease = $false
    try {
        & $ghPath release view $tagName 1>$null 2>$null
        $hasExistingRelease = ($LASTEXITCODE -eq 0)
    }
    catch {
        $hasExistingRelease = $false
    }

    if ($hasExistingRelease) {
        & $ghPath release upload $tagName $installerPath --clobber | Out-Null
        & $ghPath release edit $tagName --title $finalTitle --notes-file $notesFile | Out-Null
    }
    else {
        & $ghPath release create $tagName $installerPath --title $finalTitle --notes-file $notesFile | Out-Null
    }
}
catch {
    throw "Release publish failed: $($_.Exception.Message)"
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Release published successfully:" -ForegroundColor Green
Write-Host "Tag: $tagName"
Write-Host "Installer: $installerPath"
