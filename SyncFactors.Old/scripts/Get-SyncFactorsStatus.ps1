[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [switch]$AsJson,
    [switch]$IncludeHistory,
    [switch]$IncludeCurrentRun,
    [ValidateRange(1, 1000)]
    [int]$HistoryLimit = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleRoot = Join-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -ChildPath 'src/Modules/SyncFactors'
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force
Import-Module (Join-Path $moduleRoot 'State.psm1') -Force
Import-Module (Join-Path $moduleRoot 'Monitoring.psm1') -Force

$status = Get-SyncFactorsMonitorStatus -ConfigPath $ConfigPath -HistoryLimit $HistoryLimit

if ($AsJson) {
    $status | ConvertTo-Json -Depth 10
    return
}

Write-Host 'SuccessFactors AD Sync Status'
Write-Host "Config: $($status.configPath)"
Write-Host "State path: $($status.paths.statePath)"
Write-Host "Runtime status path: $($status.paths.runtimeStatusPath)"
Write-Host "Report directory: $($status.paths.reportDirectory)"
Write-Host "Last checkpoint: $(if ($status.lastCheckpoint) { $status.lastCheckpoint } else { 'none' })"
Write-Host "Tracked workers: $($status.totalTrackedWorkers)"
Write-Host "Suppressed workers: $($status.suppressedWorkers)"
Write-Host "Pending deletion workers: $($status.pendingDeletionWorkers)"

if ($IncludeCurrentRun -or $status.currentRun.status -eq 'InProgress') {
    Write-Host ''
    Write-Host 'Current run'
    Write-Host "Status: $($status.currentRun.status)"
    Write-Host "Stage: $($status.currentRun.stage)"
    Write-Host "Started: $($status.currentRun.startedAt)"
    Write-Host "Completed: $($status.currentRun.completedAt)"
    Write-Host "Progress: $($status.currentRun.processedWorkers) / $($status.currentRun.totalWorkers)"
    Write-Host "Current worker: $($status.currentRun.currentWorkerId)"
    Write-Host "Last action: $($status.currentRun.lastAction)"
    if ($status.currentRun.errorMessage) {
        Write-Host "Error: $($status.currentRun.errorMessage)"
    }
}

Write-Host ''
if (-not $status.latestRun.path) {
    Write-Host 'Latest report: none found'
} else {
    Write-Host "Latest report: $($status.latestRun.path)"
    Write-Host "Run ID: $($status.latestRun.runId)"
    Write-Host "Status: $($status.latestRun.status)"
    Write-Host "Mode: $($status.latestRun.mode)"
    Write-Host "Started: $($status.latestRun.startedAt)"
    Write-Host "Completed: $($status.latestRun.completedAt)"
    Write-Host "Duration seconds: $($status.latestRun.durationSeconds)"
    Write-Host "Reversible operations: $($status.latestRun.reversibleOperations)"
    Write-Host "Creates: $($status.latestRun.creates)"
    Write-Host "Updates: $($status.latestRun.updates)"
    Write-Host "Enables: $($status.latestRun.enables)"
    Write-Host "Disables: $($status.latestRun.disables)"
    Write-Host "Graveyard moves: $($status.latestRun.graveyardMoves)"
    Write-Host "Deletions: $($status.latestRun.deletions)"
    Write-Host "Quarantined: $($status.latestRun.quarantined)"
    Write-Host "Conflicts: $($status.latestRun.conflicts)"
    Write-Host "Guardrail failures: $($status.latestRun.guardrailFailures)"
    Write-Host "Manual review: $($status.latestRun.manualReview)"
    Write-Host "Unchanged: $($status.latestRun.unchanged)"
}

if ($IncludeHistory) {
    Write-Host ''
    Write-Host "Recent runs (last $HistoryLimit)"
    if (@($status.recentRuns).Count -eq 0) {
        Write-Host 'No sync reports found.'
    } else {
        foreach ($run in @($status.recentRuns)) {
            Write-Host ("{0} {1} {2} creates={3} updates={4} disables={5} deletions={6} conflicts={7} guardrails={8}" -f `
                    $run.status, `
                    $run.mode, `
                    $run.startedAt, `
                    $run.creates, `
                    $run.updates, `
                    $run.disables, `
                    $run.deletions, `
                    $run.conflicts, `
                    $run.guardrailFailures)
        }
    }
}
