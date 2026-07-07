param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('status', 'push-main', 'push-tag', 'delete-tag', 'publish-current')]
    [string]$Action,

    [string]$Tag = '',

    [string]$Remote = 'origin',

    [string]$Branch = 'main'
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host "[codex-git] $Message"
}

function Clear-StaleGitLock {
    $lockPath = Join-Path (Get-Location) '.git\index.lock'
    if (Test-Path -LiteralPath $lockPath) {
        Write-Step "Removing stale .git/index.lock"
        Remove-Item -LiteralPath $lockPath -Force
    }
}

function Disable-DeadProxyForGitProcess {
    foreach ($name in 'GIT_HTTP_PROXY', 'GIT_HTTPS_PROXY', 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY') {
        $value = [Environment]::GetEnvironmentVariable($name, 'Process')
        if ($value -eq 'http://127.0.0.1:9') {
            Write-Step "Ignoring dead proxy variable $name for this Git process"
            [Environment]::SetEnvironmentVariable($name, $null, 'Process')
        }
    }
}

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Show-HelpfulPushFailure {
    param([scriptblock]$Operation)

    try {
        & $Operation
    }
    catch {
        Write-Host ''
        Write-Host 'Git publish failed. If the error mentions SEC_E_NO_CREDENTIALS, run the same command from a normal user PowerShell window so Git Credential Manager can access your GitHub credentials.'
        Write-Host 'If the error mentions 127.0.0.1:9, the dead proxy variables were injected by the sandbox and should be cleared for the Git process.'
        throw
    }
}

Clear-StaleGitLock

switch ($Action) {
    'status' {
        $env:GIT_OPTIONAL_LOCKS = '0'
        Invoke-Git --no-optional-locks status --short --branch
    }
    'push-main' {
        Disable-DeadProxyForGitProcess
        Show-HelpfulPushFailure { Invoke-Git push $Remote "HEAD:$Branch" }
    }
    'push-tag' {
        if ([string]::IsNullOrWhiteSpace($Tag)) {
            throw 'Tag is required for push-tag.'
        }

        Disable-DeadProxyForGitProcess
        $head = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
            throw 'Could not resolve HEAD.'
        }

        Show-HelpfulPushFailure { Invoke-Git push $Remote "$head`:refs/tags/$Tag" }
    }
    'delete-tag' {
        if ([string]::IsNullOrWhiteSpace($Tag)) {
            throw 'Tag is required for delete-tag.'
        }

        Disable-DeadProxyForGitProcess
        $localTagOutput = & git tag --list $Tag
        if ($LASTEXITCODE -ne 0) {
            throw "Could not inspect local tag $Tag."
        }

        $localTag = ($localTagOutput -join "`n").Trim()
        if (-not [string]::IsNullOrWhiteSpace($localTag)) {
            Invoke-Git tag -d $Tag
        }

        Show-HelpfulPushFailure { Invoke-Git push $Remote ":refs/tags/$Tag" }
    }
    'publish-current' {
        if ([string]::IsNullOrWhiteSpace($Tag)) {
            throw 'Tag is required for publish-current.'
        }

        Disable-DeadProxyForGitProcess
        Show-HelpfulPushFailure { Invoke-Git push $Remote "HEAD:$Branch" }

        $head = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
            throw 'Could not resolve HEAD.'
        }

        Show-HelpfulPushFailure { Invoke-Git push $Remote "$head`:refs/tags/$Tag" }
    }
}
