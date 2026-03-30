[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '../..')).ProviderPath

function Require-Command {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$InstallHint
    )

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Missing required command '$Name'. $InstallHint"
    }
}

function Copy-IfMissing {
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,
        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    if (-not (Test-Path $DestinationPath)) {
        Copy-Item $SourcePath $DestinationPath
        Write-Host "Created $([System.IO.Path]::GetRelativePath($repoRoot, $DestinationPath))"
    }
}

function Copy-EnvFromPrimaryWorktreeIfMissing {
    param(
        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    if (Test-Path $DestinationPath) {
        return
    }

    $primaryWorktreeRoot = & git worktree list --porcelain 2>$null |
        Select-String '^worktree ' |
        Select-Object -First 1 |
        ForEach-Object { $_.Line.Substring(9) }

    if ([string]::IsNullOrWhiteSpace($primaryWorktreeRoot)) {
        return
    }

    $resolvedPrimaryRoot = [System.IO.Path]::GetFullPath($primaryWorktreeRoot)
    if ([string]::Equals($resolvedPrimaryRoot, $repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $sourcePath = Join-Path $resolvedPrimaryRoot '.env.worktree'
    if (Test-Path $sourcePath) {
        Copy-Item $sourcePath $DestinationPath
        Write-Host "Created $([System.IO.Path]::GetRelativePath($repoRoot, $DestinationPath)) from $sourcePath"
    }
}

Require-Command -Name 'dotnet' -InstallHint 'Install the .NET 10 SDK before creating a worktree for this repo.'
Require-Command -Name 'pwsh' -InstallHint 'Install PowerShell 7 before creating a worktree for this repo.'

Set-Location $repoRoot

Write-Host 'Preparing SyncFactors.Next worktree defaults (mock SF + real AD)'

New-Item -ItemType Directory -Force -Path `
    (Join-Path $repoRoot 'state/runtime'), `
    (Join-Path $repoRoot 'reports/output'), `
    (Join-Path $repoRoot 'reports/mock-output') | Out-Null

Copy-IfMissing -SourcePath (Join-Path $repoRoot 'config/sample.mock-successfactors.real-ad.sync-config.json') -DestinationPath (Join-Path $repoRoot 'config/local.mock-successfactors.real-ad.sync-config.json')
Copy-IfMissing -SourcePath (Join-Path $repoRoot 'config/sample.real-successfactors.real-ad.sync-config.json') -DestinationPath (Join-Path $repoRoot 'config/local.real-successfactors.real-ad.sync-config.json')
Copy-IfMissing -SourcePath (Join-Path $repoRoot 'config/sample.empjob-confirmed.mapping-config.json') -DestinationPath (Join-Path $repoRoot 'config/local.syncfactors.mapping-config.json')
Copy-EnvFromPrimaryWorktreeIfMissing -DestinationPath (Join-Path $repoRoot '.env.worktree')
Copy-IfMissing -SourcePath (Join-Path $repoRoot '.env.worktree.example') -DestinationPath (Join-Path $repoRoot '.env.worktree')

Write-Host 'Worktree bootstrap complete. Start Mock SF, the .NET API, and the worker from scripts/codex.'
