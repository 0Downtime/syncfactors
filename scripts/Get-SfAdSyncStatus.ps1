[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleRoot = Join-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -ChildPath 'src/Modules/SfAdSync'
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force
Import-Module (Join-Path $moduleRoot 'State.psm1') -Force

function Get-LatestReport {
    param(
        [Parameter(Mandatory)]
        [string]$Directory
    )

    if (-not (Test-Path -Path $Directory -PathType Container)) {
        return $null
    }

    return Get-ChildItem -Path $Directory -Filter 'sf-ad-sync-*.json' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Get-CollectionCount {
    param($Value)

    if ($null -eq $Value) {
        return 0
    }

    return @($Value).Count
}

function New-EmptyState {
    return [pscustomobject]@{
        checkpoint = $null
        workers = @{}
    }
}

function Get-WorkerEntries {
    param($Workers)

    if ($null -eq $Workers) {
        return @()
    }

    if ($Workers -is [System.Collections.IDictionary]) {
        return @(
            foreach ($key in $Workers.Keys) {
                [pscustomobject]@{
                    Name = $key
                    Value = $Workers[$key]
                }
            }
        )
    }

    return @($Workers.PSObject.Properties | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            Value = $_.Value
        }
    })
}

$config = Get-SfAdSyncConfig -Path $ConfigPath
$state = if ($config.state.path) { Get-SfAdSyncState -Path $config.state.path } else { New-EmptyState }
$latestReportFile = Get-LatestReport -Directory $config.reporting.outputDirectory
$latestReport = if ($latestReportFile) { Get-Content -Path $latestReportFile.FullName -Raw | ConvertFrom-Json -Depth 20 } else { $null }
$workerProperties = @(Get-WorkerEntries -Workers $state.workers)
$suppressedWorkers = @($workerProperties | Where-Object { $_.Value.suppressed })
$pendingDeletionWorkers = @(
    $suppressedWorkers | Where-Object {
        $_.Value.deleteAfter -and ((Get-Date $_.Value.deleteAfter) -le (Get-Date))
    }
)

$status = [pscustomobject]@{
    configPath = (Resolve-Path -Path $ConfigPath).Path
    lastCheckpoint = $state.checkpoint
    totalTrackedWorkers = $workerProperties.Count
    suppressedWorkers = $suppressedWorkers.Count
    pendingDeletionWorkers = $pendingDeletionWorkers.Count
    latestReport = [pscustomobject]@{
        path = if ($latestReportFile) { $latestReportFile.FullName } else { $null }
        startedAt = if ($latestReport) { $latestReport.startedAt } else { $null }
        completedAt = if ($latestReport) { $latestReport.completedAt } else { $null }
        creates = if ($latestReport) { Get-CollectionCount -Value $latestReport.creates } else { 0 }
        updates = if ($latestReport) { Get-CollectionCount -Value $latestReport.updates } else { 0 }
        enables = if ($latestReport) { Get-CollectionCount -Value $latestReport.enables } else { 0 }
        disables = if ($latestReport) { Get-CollectionCount -Value $latestReport.disables } else { 0 }
        graveyardMoves = if ($latestReport) { Get-CollectionCount -Value $latestReport.graveyardMoves } else { 0 }
        deletions = if ($latestReport) { Get-CollectionCount -Value $latestReport.deletions } else { 0 }
        quarantined = if ($latestReport) { Get-CollectionCount -Value $latestReport.quarantined } else { 0 }
        manualReview = if ($latestReport) { Get-CollectionCount -Value $latestReport.manualReview } else { 0 }
        unchanged = if ($latestReport) { Get-CollectionCount -Value $latestReport.unchanged } else { 0 }
    }
}

if ($AsJson) {
    $status | ConvertTo-Json -Depth 10
    return
}

Write-Host "SuccessFactors AD Sync Status"
Write-Host "Config: $($status.configPath)"
Write-Host "Last checkpoint: $(if ($status.lastCheckpoint) { $status.lastCheckpoint } else { 'none' })"
Write-Host "Tracked workers: $($status.totalTrackedWorkers)"
Write-Host "Suppressed workers: $($status.suppressedWorkers)"
Write-Host "Pending deletion workers: $($status.pendingDeletionWorkers)"
Write-Host ''

if (-not $latestReportFile) {
    Write-Host 'Latest report: none found'
    return
}

Write-Host "Latest report: $($status.latestReport.path)"
Write-Host "Started: $($status.latestReport.startedAt)"
Write-Host "Completed: $($status.latestReport.completedAt)"
Write-Host "Creates: $($status.latestReport.creates)"
Write-Host "Updates: $($status.latestReport.updates)"
Write-Host "Enables: $($status.latestReport.enables)"
Write-Host "Disables: $($status.latestReport.disables)"
Write-Host "Graveyard moves: $($status.latestReport.graveyardMoves)"
Write-Host "Deletions: $($status.latestReport.deletions)"
Write-Host "Quarantined: $($status.latestReport.quarantined)"
Write-Host "Manual review: $($status.latestReport.manualReview)"
Write-Host "Unchanged: $($status.latestReport.unchanged)"
