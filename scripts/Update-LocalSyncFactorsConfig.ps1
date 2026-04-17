[CmdletBinding()]
param(
    [string]$LocalPath,
    [string]$SamplePath,
    [switch]$AllProfiles,
    [switch]$AllTrackedConfigs,
    [switch]$NoBackup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).ProviderPath

. (Join-Path $scriptDir 'Sync-LocalConfigFormat.ps1')

function Resolve-ConfigPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repoRoot $Path
}

function Write-SyncResult {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Result
    )

    if ($Result.BackupPath) {
        Write-Host "Backed up $(Split-Path -Leaf $Result.LocalConfigPath) -> $(Split-Path -Leaf $Result.BackupPath)"
    }

    if ($Result.Drifted) {
        Write-Host "Updated $(Split-Path -Leaf $Result.LocalConfigPath) from $(Split-Path -Leaf $Result.SampleConfigPath)"
        return
    }

    Write-Host "$(Split-Path -Leaf $Result.LocalConfigPath) already matches $(Split-Path -Leaf $Result.SampleConfigPath)"
}

$trackedPairs = @(Get-TrackedLocalConfigPairs -RepositoryRoot $repoRoot)
$profilePairs = @(
    $trackedPairs |
    Where-Object { $_.LocalPath -like '*local.mock-successfactors.real-ad.sync-config.json' -or $_.LocalPath -like '*local.real-successfactors.real-ad.sync-config.json' }
)

if ($AllProfiles) {
    foreach ($pair in $profilePairs) {
        Write-SyncResult -Result (Sync-ConfigFormat `
            -SampleConfigPath $pair.SamplePath `
            -LocalConfigPath $pair.LocalPath `
            -NoBackup:$NoBackup)
    }

    return
}

$shouldSyncTrackedConfigs = $AllTrackedConfigs -or ([string]::IsNullOrWhiteSpace($LocalPath) -and [string]::IsNullOrWhiteSpace($SamplePath))
if ($shouldSyncTrackedConfigs) {
    foreach ($pair in $trackedPairs) {
        Write-SyncResult -Result (Sync-ConfigFormat `
            -SampleConfigPath $pair.SamplePath `
            -LocalConfigPath $pair.LocalPath `
            -NoBackup:$NoBackup)
    }

    return
}

if ([string]::IsNullOrWhiteSpace($LocalPath) -or [string]::IsNullOrWhiteSpace($SamplePath)) {
    throw "Pass no arguments to sync all tracked local configs, use -AllProfiles for only the mock/real sync configs, or provide both -LocalPath and -SamplePath."
}

$resolvedLocalPath = Resolve-ConfigPath -Path $LocalPath
$resolvedSamplePath = Resolve-ConfigPath -Path $SamplePath
Write-SyncResult -Result (Sync-ConfigFormat `
    -SampleConfigPath $resolvedSamplePath `
    -LocalConfigPath $resolvedLocalPath `
    -NoBackup:$NoBackup)
