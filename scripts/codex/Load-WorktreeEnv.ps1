[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '../..')).ProviderPath
$envFile = Join-Path $repoRoot '.env.worktree'
$envExampleFile = Join-Path $repoRoot '.env.worktree.example'

. (Join-Path $scriptDir 'WorktreeEnv.ps1')

if (-not [OperatingSystem]::IsWindows() -and -not (Test-Path $envFile)) {
    throw "Missing $envFile. Run pwsh ./scripts/codex/setup-worktree.ps1 first, or copy ./.env.worktree.example to ./.env.worktree."
}

if (-not (Test-Path $envFile) -and -not (Test-Path $envExampleFile)) {
    throw "Missing both $envFile and $envExampleFile. At least one worktree env file is required."
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

$exampleValues = Read-WorktreeEnvFile -Path $envExampleFile
$fileValues = Read-WorktreeEnvFile -Path $envFile
$variableNames = [System.Collections.Generic.List[string]]::new()
$variableSources = [ordered]@{}

foreach ($name in $exampleValues.Keys) {
    if (-not $variableNames.Contains([string]$name)) {
        $variableNames.Add([string]$name)
    }
}

foreach ($name in $fileValues.Keys) {
    if (-not $variableNames.Contains([string]$name)) {
        $variableNames.Add([string]$name)
    }
}

foreach ($name in $variableNames) {
    $value = $null
    $source = $null

    if ([OperatingSystem]::IsWindows()) {
        $credentialValue = Get-SyncFactorsCredentialValue -RepoRoot $repoRoot -VariableName $name
        if ($credentialValue.Found) {
            $value = $credentialValue.Value
            $source = 'Windows Credential Manager'
        }
    }

    if ($null -eq $value -and $fileValues.Contains($name)) {
        $value = [string]$fileValues[$name]
        $source = '.env.worktree'
    }

    if ($null -eq $value -and $exampleValues.Contains($name)) {
        $value = [string]$exampleValues[$name]
        $source = '.env.worktree.example'
    }

    if ($null -ne $value) {
        [Environment]::SetEnvironmentVariable($name, $value)
        $variableSources[$name] = $source
    }
}

if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_RUN_PROFILE)) {
    $env:SYNCFACTORS_RUN_PROFILE = 'mock'
    $variableSources['SYNCFACTORS_RUN_PROFILE'] = 'built-in default'
}

if ($null -eq $env:SYNCFACTORS_CONFIG_PATH) {
    $env:SYNCFACTORS_CONFIG_PATH = ''
    $variableSources['SYNCFACTORS_CONFIG_PATH'] = 'built-in default'
}

if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_MAPPING_CONFIG_PATH)) {
    $env:SYNCFACTORS_MAPPING_CONFIG_PATH = './config/local.syncfactors.mapping-config.json'
    $variableSources['SYNCFACTORS_MAPPING_CONFIG_PATH'] = 'built-in default'
}

if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_SQLITE_PATH)) {
    $env:SYNCFACTORS_SQLITE_PATH = 'state/runtime/syncfactors.db'
    $variableSources['SYNCFACTORS_SQLITE_PATH'] = 'built-in default'
}

if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_API_PORT)) {
    $env:SYNCFACTORS_API_PORT = '5087'
    $variableSources['SYNCFACTORS_API_PORT'] = 'built-in default'
}

if ([string]::IsNullOrWhiteSpace($env:MOCK_SF_PORT)) {
    $env:MOCK_SF_PORT = '18080'
    $variableSources['MOCK_SF_PORT'] = 'built-in default'
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

$keychainService = if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_KEYCHAIN_SERVICE)) { 'syncfactors' } else { $env:SYNCFACTORS_KEYCHAIN_SERVICE }
$secretNames = @(
    'SF_AD_SYNC_SF_USERNAME',
    'SF_AD_SYNC_SF_PASSWORD',
    'SF_AD_SYNC_SF_CLIENT_ID',
    'SF_AD_SYNC_SF_CLIENT_SECRET',
    'SF_AD_SYNC_AD_SERVER',
    'SF_AD_SYNC_AD_USERNAME',
    'SF_AD_SYNC_AD_BIND_PASSWORD',
    'SF_AD_SYNC_AD_DEFAULT_PASSWORD'
)

if ($IsMacOS) {
    foreach ($secretName in $secretNames) {
        if (-not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($secretName))) {
            continue
        }

        try {
            $secretValue = & security find-generic-password -s $keychainService -a $secretName -w 2>$null
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($secretValue)) {
                [Environment]::SetEnvironmentVariable($secretName, $secretValue)
            }
        }
        catch {
            continue
        }
    }
}

Write-Host 'Worktree environment sources:' -ForegroundColor Cyan
foreach ($entry in $variableSources.GetEnumerator()) {
    Write-Host ("  {0}: {1}" -f $entry.Key, $entry.Value)
}

Set-Location $repoRoot
