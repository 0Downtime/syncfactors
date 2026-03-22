[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [ValidateRange(1, 1000)]
    [int]$HistoryLimit = 25,
    [switch]$AsJson,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Path $PSScriptRoot -Parent
$moduleRoot = Join-Path -Path $projectRoot -ChildPath 'src/Modules/SyncFactors'
$configModule = Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking -PassThru
$monitoringModule = Import-Module (Join-Path $moduleRoot 'Monitoring.psm1') -Force -DisableNameChecking -PassThru
$persistenceModule = Import-Module (Join-Path $moduleRoot 'Persistence.psm1') -Force -DisableNameChecking -PassThru

$getSyncFactorsConfig = $configModule.ExportedFunctions['Get-SyncFactorsConfig']
$getSyncFactorsSuccessFactorsAuthSummary = $configModule.ExportedFunctions['Get-SyncFactorsSuccessFactorsAuthSummary']
$newSyncFactorsIdleRuntimeStatus = $monitoringModule.ExportedFunctions['New-SyncFactorsIdleRuntimeStatus']
$getSyncFactorsMonitorStatus = $monitoringModule.ExportedFunctions['Get-SyncFactorsMonitorStatus']
$getSyncFactorsRuntimeStatusPath = $monitoringModule.ExportedFunctions['Get-SyncFactorsRuntimeStatusPath']
$getSyncFactorsSqlitePath = $persistenceModule.ExportedFunctions['Get-SyncFactorsSqlitePath']
$getSyncFactorsRuntimeStatusSnapshotFromSqlite = $persistenceModule.ExportedFunctions['Get-SyncFactorsRuntimeStatusSnapshotFromSqlite']
$getSyncFactorsTrackedWorkersFromSqlite = $persistenceModule.ExportedFunctions['Get-SyncFactorsTrackedWorkersFromSqlite']
$getSyncFactorsStateCheckpointFromSqlite = $persistenceModule.ExportedFunctions['Get-SyncFactorsStateCheckpointFromSqlite']
$getSyncFactorsRecentRunsFromSqlite = $persistenceModule.ExportedFunctions['Get-SyncFactorsRecentRunsFromSqlite']

function New-SyncFactorsWebEmptyState {
    return [pscustomobject]@{
        checkpoint = $null
        workers = @{}
    }
}

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
$sqlitePath = & $getSyncFactorsSqlitePath -Config $config

if ([string]::IsNullOrWhiteSpace($sqlitePath) -or -not (Test-Path -Path $sqlitePath -PathType Leaf)) {
    $warnings.Add('SQLite operational store is unavailable. Import or generate the SQLite store before using the web dashboard.')
}

$runtimeStatus = $null
if (-not [string]::IsNullOrWhiteSpace($sqlitePath) -and (Test-Path -Path $sqlitePath -PathType Leaf)) {
    try {
        $runtimeStatus = & $getSyncFactorsRuntimeStatusSnapshotFromSqlite -StatePath $config.state.path -DatabasePath $sqlitePath
    } catch {
        $warnings.Add("SQLite runtime status unavailable: $($_.Exception.Message)")
    }
}

if (-not $runtimeStatus) {
    $runtimeStatus = & $newSyncFactorsIdleRuntimeStatus -StatePath $config.state.path
}

$reportDirectories = @()
foreach ($candidate in @($config.reporting.outputDirectory, $config.reporting.reviewOutputDirectory)) {
    if (-not [string]::IsNullOrWhiteSpace("$candidate") -and $reportDirectories -notcontains $candidate) {
        $reportDirectories += $candidate
    }
}

$recentRuns = @()
if (-not [string]::IsNullOrWhiteSpace($sqlitePath) -and (Test-Path -Path $sqlitePath -PathType Leaf)) {
    try {
        $recentRuns = @(& $getSyncFactorsRecentRunsFromSqlite -StatePath $config.state.path -DatabasePath $sqlitePath -Limit $HistoryLimit)
    } catch {
        $warnings.Add("SQLite run history unavailable: $($_.Exception.Message)")
    }
}

$workerEntries = @()
if (-not [string]::IsNullOrWhiteSpace($sqlitePath) -and (Test-Path -Path $sqlitePath -PathType Leaf)) {
    try {
        $workerEntries = @(& $getSyncFactorsTrackedWorkersFromSqlite -StatePath $config.state.path -DatabasePath $sqlitePath)
    } catch {
        $warnings.Add("SQLite worker state unavailable: $($_.Exception.Message)")
    }
}
$lastCheckpoint = $null
if (-not [string]::IsNullOrWhiteSpace($sqlitePath) -and (Test-Path -Path $sqlitePath -PathType Leaf)) {
    try {
        $lastCheckpoint = & $getSyncFactorsStateCheckpointFromSqlite -StatePath $config.state.path -DatabasePath $sqlitePath
    } catch {
        $warnings.Add("SQLite checkpoint unavailable: $($_.Exception.Message)")
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
        lastCheckpoint = $lastCheckpoint
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
        sqlitePath = $sqlitePath
    }
    warnings = @($warnings)
}

if ($AsJson) {
    $json = $result | ConvertTo-Json -Depth 30
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $outputDirectory = Split-Path -Path $OutputPath -Parent
        if (-not [string]::IsNullOrWhiteSpace($outputDirectory) -and -not (Test-Path -Path $outputDirectory -PathType Container)) {
            New-Item -Path $outputDirectory -ItemType Directory -Force | Out-Null
        }

        Set-Content -Path $OutputPath -Value $json -Encoding utf8
        return
    }

    $json
    return
}

$result
