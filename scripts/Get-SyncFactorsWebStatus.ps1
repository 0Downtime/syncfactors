[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [ValidateRange(1, 1000)]
    [int]$HistoryLimit = 25,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Path $PSScriptRoot -Parent
$moduleRoot = Join-Path -Path $projectRoot -ChildPath 'src/Modules/SyncFactors'
$configModule = Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking -PassThru
$stateModule = Import-Module (Join-Path $moduleRoot 'State.psm1') -Force -DisableNameChecking -PassThru
$monitoringModule = Import-Module (Join-Path $moduleRoot 'Monitoring.psm1') -Force -DisableNameChecking -PassThru

$getSyncFactorsConfig = $configModule.ExportedFunctions['Get-SyncFactorsConfig']
$getSyncFactorsSuccessFactorsAuthSummary = $configModule.ExportedFunctions['Get-SyncFactorsSuccessFactorsAuthSummary']
$getSyncFactorsState = $stateModule.ExportedFunctions['Get-SyncFactorsState']
$getSyncFactorsRuntimeStatusSnapshot = $monitoringModule.ExportedFunctions['Get-SyncFactorsRuntimeStatusSnapshot']
$newSyncFactorsIdleRuntimeStatus = $monitoringModule.ExportedFunctions['New-SyncFactorsIdleRuntimeStatus']
$getSyncFactorsMonitorStatus = $monitoringModule.ExportedFunctions['Get-SyncFactorsMonitorStatus']
$getSyncFactorsRuntimeStatusPath = $monitoringModule.ExportedFunctions['Get-SyncFactorsRuntimeStatusPath']

function New-SyncFactorsWebEmptyRunSummary {
    return [pscustomobject]@{
        runId = $null
        path = $null
        artifactType = 'SyncReport'
        workerScope = $null
        configPath = $null
        mappingConfigPath = $null
        mode = $null
        dryRun = $false
        status = $null
        startedAt = $null
        completedAt = $null
        durationSeconds = $null
        reversibleOperations = 0
        creates = 0
        updates = 0
        enables = 0
        disables = 0
        graveyardMoves = 0
        deletions = 0
        quarantined = 0
        conflicts = 0
        guardrailFailures = 0
        manualReview = 0
        unchanged = 0
        reviewSummary = $null
    }
}

function Get-SyncFactorsWebCollectionCount {
    param($Value)

    if ($null -eq $Value) {
        return 0
    }

    return @($Value).Count
}

function Get-SyncFactorsWebDurationSeconds {
    param(
        $StartedAt,
        $CompletedAt
    )

    if ([string]::IsNullOrWhiteSpace("$StartedAt") -or [string]::IsNullOrWhiteSpace("$CompletedAt")) {
        return $null
    }

    try {
        return [int][math]::Max(0, [math]::Round(((Get-Date $CompletedAt) - (Get-Date $StartedAt)).TotalSeconds))
    } catch {
        return $null
    }
}

function ConvertTo-SyncFactorsWebRunSummary {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [pscustomobject]$Report
    )

    return [pscustomobject]@{
        runId = if ($Report.PSObject.Properties.Name -contains 'runId') { $Report.runId } else { $null }
        path = $Path
        artifactType = if ($Report.PSObject.Properties.Name -contains 'artifactType' -and -not [string]::IsNullOrWhiteSpace("$($Report.artifactType)")) { $Report.artifactType } else { 'SyncReport' }
        workerScope = if ($Report.PSObject.Properties.Name -contains 'workerScope') { $Report.workerScope } else { $null }
        configPath = if ($Report.PSObject.Properties.Name -contains 'configPath') { $Report.configPath } else { $null }
        mappingConfigPath = if ($Report.PSObject.Properties.Name -contains 'mappingConfigPath') { $Report.mappingConfigPath } else { $null }
        mode = if ($Report.PSObject.Properties.Name -contains 'mode') { $Report.mode } else { $null }
        dryRun = if ($Report.PSObject.Properties.Name -contains 'dryRun') { [bool]$Report.dryRun } else { $false }
        status = if ($Report.PSObject.Properties.Name -contains 'status') { $Report.status } else { $null }
        startedAt = if ($Report.PSObject.Properties.Name -contains 'startedAt') { $Report.startedAt } else { $null }
        completedAt = if ($Report.PSObject.Properties.Name -contains 'completedAt') { $Report.completedAt } else { $null }
        durationSeconds = Get-SyncFactorsWebDurationSeconds -StartedAt $(if ($Report.PSObject.Properties.Name -contains 'startedAt') { $Report.startedAt } else { $null }) -CompletedAt $(if ($Report.PSObject.Properties.Name -contains 'completedAt') { $Report.completedAt } else { $null })
        reversibleOperations = Get-SyncFactorsWebCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'operations') { $Report.operations } else { @() })
        creates = Get-SyncFactorsWebCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'creates') { $Report.creates } else { @() })
        updates = Get-SyncFactorsWebCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'updates') { $Report.updates } else { @() })
        enables = Get-SyncFactorsWebCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'enables') { $Report.enables } else { @() })
        disables = Get-SyncFactorsWebCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'disables') { $Report.disables } else { @() })
        graveyardMoves = Get-SyncFactorsWebCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'graveyardMoves') { $Report.graveyardMoves } else { @() })
        deletions = Get-SyncFactorsWebCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'deletions') { $Report.deletions } else { @() })
        quarantined = Get-SyncFactorsWebCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'quarantined') { $Report.quarantined } else { @() })
        conflicts = Get-SyncFactorsWebCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'conflicts') { $Report.conflicts } else { @() })
        guardrailFailures = Get-SyncFactorsWebCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'guardrailFailures') { $Report.guardrailFailures } else { @() })
        manualReview = Get-SyncFactorsWebCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'manualReview') { $Report.manualReview } else { @() })
        unchanged = Get-SyncFactorsWebCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'unchanged') { $Report.unchanged } else { @() })
        reviewSummary = if ($Report.PSObject.Properties.Name -contains 'reviewSummary') { $Report.reviewSummary } else { $null }
    }
}

function Get-SyncFactorsWebRecentRunSummaries {
    param(
        [Parameter(Mandatory)]
        [string[]]$Directories,
        [Parameter(Mandatory)]
        [int]$Limit,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$Warnings
    )

    $files = @(
        foreach ($directory in @($Directories | Where-Object { -not [string]::IsNullOrWhiteSpace("$_") } | Select-Object -Unique)) {
            if (-not (Test-Path -Path $directory -PathType Container)) {
                continue
            }

            Get-ChildItem -Path $directory -Filter 'syncfactors-*.json' -File
        }
    )

    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    $sorted = @(
        $files | Sort-Object `
            @{ Expression = {
                    if ($_.BaseName -match '(\d{8}-\d{6})$') {
                        try {
                            return [datetime]::ParseExact($Matches[1], 'yyyyMMdd-HHmmss', $culture)
                        } catch {
                            return $_.LastWriteTime
                        }
                    }

                    return $_.LastWriteTime
                }; Descending = $true }, `
            @{ Expression = { $_.Name }; Descending = $true }
    )

    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($file in $sorted) {
        if ($results.Count -ge $Limit) {
            break
        }

        try {
            $report = Get-Content -Path $file.FullName -Raw | ConvertFrom-Json -Depth 30
            $results.Add((ConvertTo-SyncFactorsWebRunSummary -Path $file.FullName -Report $report))
        } catch {
            $Warnings.Add("Skipped malformed report '$($file.FullName)': $($_.Exception.Message)")
        }
    }

    return @($results)
}

$warnings = [System.Collections.Generic.List[string]]::new()
$resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
$config = & $getSyncFactorsConfig -Path $resolvedConfigPath
$state = & $getSyncFactorsState -Path $config.state.path
$runtimeStatus = & $getSyncFactorsRuntimeStatusSnapshot -StatePath $config.state.path
if (-not $runtimeStatus) {
    $runtimeStatus = & $newSyncFactorsIdleRuntimeStatus -StatePath $config.state.path
}

$reportDirectories = @()
foreach ($candidate in @($config.reporting.outputDirectory, $config.reporting.reviewOutputDirectory)) {
    if (-not [string]::IsNullOrWhiteSpace("$candidate") -and $reportDirectories -notcontains $candidate) {
        $reportDirectories += $candidate
    }
}

$recentRuns = @(Get-SyncFactorsWebRecentRunSummaries -Directories $reportDirectories -Limit $HistoryLimit -Warnings $warnings)
$workerEntries = @()
if ($state -and $state.PSObject.Properties.Name -contains 'workers' -and $state.workers) {
    if ($state.workers -is [System.Collections.IDictionary]) {
        foreach ($key in $state.workers.Keys) {
            $workerEntries += [pscustomobject]@{
                workerId = $key
                adObjectGuid = $state.workers[$key].adObjectGuid
                distinguishedName = $state.workers[$key].distinguishedName
                suppressed = [bool]$state.workers[$key].suppressed
                firstDisabledAt = $state.workers[$key].firstDisabledAt
                deleteAfter = $state.workers[$key].deleteAfter
                lastSeenStatus = $state.workers[$key].lastSeenStatus
            }
        }
    } else {
        foreach ($property in @($state.workers.PSObject.Properties)) {
            $workerEntries += [pscustomobject]@{
                workerId = $property.Name
                adObjectGuid = $property.Value.adObjectGuid
                distinguishedName = $property.Value.distinguishedName
                suppressed = [bool]$property.Value.suppressed
                firstDisabledAt = $property.Value.firstDisabledAt
                deleteAfter = $property.Value.deleteAfter
                lastSeenStatus = $property.Value.lastSeenStatus
            }
        }
    }
}

$health = [pscustomobject]@{
    successFactors = [pscustomobject]@{ status = 'UNKNOWN'; detail = 'Health probe unavailable.' }
    activeDirectory = [pscustomobject]@{ status = 'UNKNOWN'; detail = 'Health probe unavailable.' }
}

if ($IsWindows) {
    try {
        $fullStatus = & $getSyncFactorsMonitorStatus -ConfigPath $resolvedConfigPath -HistoryLimit $HistoryLimit
        if ($fullStatus -and $fullStatus.PSObject.Properties.Name -contains 'health' -and $fullStatus.health) {
            $health = $fullStatus.health
        }
    } catch {
        $warnings.Add("Health probe unavailable: $($_.Exception.Message)")
    }
} else {
    try {
        $health.successFactors = & $monitoringModule {
            param($Config)
            Test-SyncFactorsMonitorSuccessFactorsConnection -Config $Config
        } $config
    } catch {
        $warnings.Add("SuccessFactors health probe unavailable: $($_.Exception.Message)")
    }

    $health.activeDirectory = [pscustomobject]@{
        status = 'UNKNOWN'
        detail = 'Active Directory health probe requires the Windows ActiveDirectory module.'
    }
    $warnings.Add('Active Directory health probe is skipped on non-Windows hosts for the web dashboard.')
}

$suppressedWorkers = @($workerEntries | Where-Object { $_.suppressed })
$pendingDeletionWorkers = @(
    $suppressedWorkers | Where-Object {
        $_.deleteAfter -and ((Get-Date $_.deleteAfter) -le (Get-Date))
    }
)

$result = [pscustomobject]@{
    configPath = $resolvedConfigPath
    latestRun = if ($recentRuns.Count -gt 0) { $recentRuns[0] } else { New-SyncFactorsWebEmptyRunSummary }
    currentRun = $runtimeStatus
    recentRuns = $recentRuns
    summary = [pscustomobject]@{
        lastCheckpoint = $state.checkpoint
        totalTrackedWorkers = @($workerEntries).Count
        suppressedWorkers = @($suppressedWorkers).Count
        pendingDeletionWorkers = @($pendingDeletionWorkers).Count
    }
    health = $health
    trackedWorkers = @($workerEntries | Sort-Object workerId)
    context = [pscustomobject]@{
        successFactorsAuth = & $getSyncFactorsSuccessFactorsAuthSummary -Config $config
        identityField = $config.successFactors.query.identityField
        identityAttribute = $config.ad.identityAttribute
        defaultActiveOu = $config.ad.defaultActiveOu
        graveyardOu = $config.ad.graveyardOu
        enableBeforeStartDays = $config.sync.enableBeforeStartDays
        deletionRetentionDays = $config.sync.deletionRetentionDays
        maxCreatesPerRun = if ($config.PSObject.Properties.Name -contains 'safety') { $config.safety.maxCreatesPerRun } else { $null }
        maxDisablesPerRun = if ($config.PSObject.Properties.Name -contains 'safety') { $config.safety.maxDisablesPerRun } else { $null }
        maxDeletionsPerRun = if ($config.PSObject.Properties.Name -contains 'safety') { $config.safety.maxDeletionsPerRun } else { $null }
        reviewOutputDirectory = $config.reporting.reviewOutputDirectory
    }
    paths = [pscustomobject]@{
        configPath = $resolvedConfigPath
        statePath = $config.state.path
        reportDirectory = $config.reporting.outputDirectory
        reviewReportDirectory = $config.reporting.reviewOutputDirectory
        reportDirectories = $reportDirectories
        runtimeStatusPath = & $getSyncFactorsRuntimeStatusPath -StatePath $config.state.path
    }
    warnings = @($warnings)
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 30
    return
}

$result
