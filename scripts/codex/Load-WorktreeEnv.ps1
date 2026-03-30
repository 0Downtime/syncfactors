[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '../..')).ProviderPath
$envFile = Join-Path $repoRoot '.env.worktree'

if (-not (Test-Path $envFile)) {
    throw "Missing $envFile. Run pwsh ./scripts/codex/setup-worktree.ps1 first, or copy ./.env.worktree.example to ./.env.worktree."
}

function Resolve-RepoPath {
    param(
        [AllowEmptyString()]
        [AllowNull()]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    $trimmedPath = $Path
    if ($trimmedPath.StartsWith('./', [StringComparison]::Ordinal)) {
        $trimmedPath = $trimmedPath.Substring(2)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $trimmedPath))
}

Get-Content $envFile | ForEach-Object {
    $line = $_.Trim()
    if ($line.Length -eq 0 -or $line.StartsWith('#', [StringComparison]::Ordinal)) {
        return
    }

    $separatorIndex = $line.IndexOf('=')
    if ($separatorIndex -lt 0) {
        return
    }

    $name = $line.Substring(0, $separatorIndex).Trim()
    $value = $line.Substring($separatorIndex + 1)
    [Environment]::SetEnvironmentVariable($name, $value)
}

if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_RUN_PROFILE)) {
    $env:SYNCFACTORS_RUN_PROFILE = 'mock'
}

if ($null -eq $env:SYNCFACTORS_CONFIG_PATH) {
    $env:SYNCFACTORS_CONFIG_PATH = ''
}

if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_MAPPING_CONFIG_PATH)) {
    $env:SYNCFACTORS_MAPPING_CONFIG_PATH = './config/local.syncfactors.mapping-config.json'
}

if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_SQLITE_PATH)) {
    $env:SYNCFACTORS_SQLITE_PATH = 'state/runtime/syncfactors.db'
}

if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_API_PORT)) {
    $env:SYNCFACTORS_API_PORT = '5087'
}

if ([string]::IsNullOrWhiteSpace($env:MOCK_SF_PORT)) {
    $env:MOCK_SF_PORT = '18080'
}

$env:REPO_ROOT = $repoRoot
$env:SYNCFACTORS_CONFIG_PATH_ABS = Resolve-RepoPath $env:SYNCFACTORS_CONFIG_PATH
$env:SYNCFACTORS_MAPPING_CONFIG_PATH_ABS = Resolve-RepoPath $env:SYNCFACTORS_MAPPING_CONFIG_PATH
$env:SYNCFACTORS_SQLITE_PATH_ABS = Resolve-RepoPath $env:SYNCFACTORS_SQLITE_PATH
$env:SYNCFACTORS_MOCK_CONFIG_PATH_ABS = Join-Path $repoRoot 'config/local.mock-successfactors.real-ad.sync-config.json'
$env:SYNCFACTORS_REAL_CONFIG_PATH_ABS = Join-Path $repoRoot 'config/local.real-successfactors.real-ad.sync-config.json'

switch ($env:SYNCFACTORS_RUN_PROFILE.ToLowerInvariant()) {
    'mock' {
        $profileConfigPath = $env:SYNCFACTORS_MOCK_CONFIG_PATH_ABS
    }
    'real' {
        $profileConfigPath = $env:SYNCFACTORS_REAL_CONFIG_PATH_ABS
    }
    default {
        throw "Unsupported SYNCFACTORS_RUN_PROFILE '$($env:SYNCFACTORS_RUN_PROFILE)'. Expected 'mock' or 'real'."
    }
}

if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_CONFIG_PATH)) {
    $resolvedSyncConfigPath = $profileConfigPath
}
else {
    $resolvedSyncConfigPath = $env:SYNCFACTORS_CONFIG_PATH_ABS
}

$env:SYNCFACTORS_PROFILE_CONFIG_PATH_ABS = $profileConfigPath
$env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS = $resolvedSyncConfigPath
Set-Location $repoRoot
