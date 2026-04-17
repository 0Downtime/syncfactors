[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '../..')).ProviderPath

. (Join-Path $scriptDir 'WorktreeEnv.ps1')

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

function Get-PrimaryWorktreeRoot {
    $primaryWorktreeRoot = & git worktree list --porcelain 2>$null |
        Select-String '^worktree ' |
        Select-Object -First 1 |
        ForEach-Object { $_.Line.Substring(9) }

    if ([string]::IsNullOrWhiteSpace($primaryWorktreeRoot)) {
        return $null
    }

    $resolvedPrimaryRoot = [System.IO.Path]::GetFullPath($primaryWorktreeRoot)
    if ([string]::Equals($resolvedPrimaryRoot, $repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }

    return $resolvedPrimaryRoot
}

function Copy-FromPrimaryWorktreeIfMissing {
    param(
        [Parameter(Mandatory)]
        [string]$PrimaryWorktreeRoot,
        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    $destinationPath = Join-Path $repoRoot $RelativePath
    if (Test-Path $destinationPath) {
        return
    }

    $sourcePath = Join-Path $PrimaryWorktreeRoot $RelativePath
    if (Test-Path $sourcePath) {
        $destinationDirectory = Split-Path -Parent $destinationPath
        if (-not [string]::IsNullOrWhiteSpace($destinationDirectory)) {
            New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
        }

        Copy-Item $sourcePath $DestinationPath
        Write-Host "Created $([System.IO.Path]::GetRelativePath($repoRoot, $destinationPath)) from $sourcePath"
    }
}

function Copy-IgnoredLocalFilesFromPrimaryWorktreeIfMissing {
    param(
        [string]$PrimaryWorktreeRoot
    )

    if ([string]::IsNullOrWhiteSpace($PrimaryWorktreeRoot)) {
        return
    }

    Copy-FromPrimaryWorktreeIfMissing -PrimaryWorktreeRoot $PrimaryWorktreeRoot -RelativePath '.env.worktree'

    $sourceConfigDirectory = Join-Path $PrimaryWorktreeRoot 'config'
    if (-not (Test-Path $sourceConfigDirectory)) {
        return
    }

    Get-ChildItem -Path $sourceConfigDirectory -File |
        Where-Object { $_.Name -like 'local*' -or $_.Name -like '*.variables' } |
        ForEach-Object {
            Copy-FromPrimaryWorktreeIfMissing -PrimaryWorktreeRoot $PrimaryWorktreeRoot -RelativePath (Join-Path 'config' $_.Name)
        }
}

Require-Command -Name 'dotnet' -InstallHint 'Install the .NET 10 SDK before creating a worktree for this repo.'
Require-Command -Name 'pwsh' -InstallHint 'Install PowerShell 7 before creating a worktree for this repo.'

Set-Location $repoRoot

Write-Host 'Preparing SyncFactors worktree defaults (mock SF + real AD)'
Assert-TrackedWorktreeEnvTemplate -RepositoryRoot $repoRoot

New-Item -ItemType Directory -Force -Path `
    (Join-Path $repoRoot 'state/runtime'), `
    (Join-Path $repoRoot 'reports/output'), `
    (Join-Path $repoRoot 'reports/mock-output') | Out-Null

$primaryWorktreeRoot = Get-PrimaryWorktreeRoot
Copy-IgnoredLocalFilesFromPrimaryWorktreeIfMissing -PrimaryWorktreeRoot $primaryWorktreeRoot

Copy-IfMissing -SourcePath (Join-Path $repoRoot 'config/sample.mock-successfactors.real-ad.sync-config.json') -DestinationPath (Join-Path $repoRoot 'config/local.mock-successfactors.real-ad.sync-config.json')
Copy-IfMissing -SourcePath (Join-Path $repoRoot 'config/sample.real-successfactors.real-ad.sync-config.json') -DestinationPath (Join-Path $repoRoot 'config/local.real-successfactors.real-ad.sync-config.json')
Copy-IfMissing -SourcePath (Join-Path $repoRoot 'config/sample.empjob-confirmed.mapping-config.json') -DestinationPath (Join-Path $repoRoot 'config/local.syncfactors.mapping-config.json')
Copy-IfMissing -SourcePath (Join-Path $repoRoot 'config/sample.empjob-confirmed.mapping-config.json') -DestinationPath (Join-Path $repoRoot 'config/local.empjob-confirmed.mapping-config.json')
Copy-IfMissing -SourcePath (Join-Path $repoRoot 'config/sample.codex-run.json') -DestinationPath (Join-Path $repoRoot 'config/local.codex-run.json')
Copy-IfMissing -SourcePath (Join-Path $repoRoot '.env.worktree.example') -DestinationPath (Join-Path $repoRoot '.env.worktree')
Sync-TrackedWorktreeEnvFormats -RepositoryRoot $repoRoot -NoBackup | Out-Null

Write-Host 'Worktree bootstrap complete. Start Mock SF, the .NET API, and the worker from scripts/codex.'
