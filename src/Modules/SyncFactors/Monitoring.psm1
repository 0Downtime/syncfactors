Set-StrictMode -Version Latest

$moduleRoot = $PSScriptRoot
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'State.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'SuccessFactors.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'ActiveDirectorySync.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'Persistence.psm1') -Force -DisableNameChecking

function Get-SyncFactorsCollectionCount {
    [CmdletBinding()]
    param($Value)

    if ($null -eq $Value) {
        return 0
    }

    return @($Value).Count
}

function Get-SyncFactorsRuntimeStatusPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StatePath
    )

    $directory = Split-Path -Path $StatePath -Parent
    if ([string]::IsNullOrWhiteSpace($directory)) {
        return 'runtime-status.json'
    }

    return Join-Path -Path $directory -ChildPath 'runtime-status.json'
}

function Test-SyncFactorsMonitorSuccessFactorsConnection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    try {
        $probeQuery = @{
            '$select' = $Config.successFactors.query.identityField
            '$top'    = '1'
        }
        Invoke-SfODataGet -Config $Config -RelativePath $Config.successFactors.query.entitySet -Query $probeQuery | Out-Null
        return [pscustomobject]@{
            status = 'OK'
            detail = Get-SyncFactorsSuccessFactorsAuthSummary -Config $Config
        }
    } catch {
        return [pscustomobject]@{
            status = 'ERROR'
            detail = $_.Exception.Message
        }
    }
}

function Test-SyncFactorsMonitorActiveDirectoryConnection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    try {
        Ensure-ActiveDirectoryModule

        $directoryContext = @{}
        $server = if ($Config.ad.PSObject.Properties.Name -contains 'server') { "$($Config.ad.server)" } else { '' }
        $username = if ($Config.ad.PSObject.Properties.Name -contains 'username') { "$($Config.ad.username)" } else { '' }
        $bindPassword = if ($Config.ad.PSObject.Properties.Name -contains 'bindPassword') { "$($Config.ad.bindPassword)" } else { '' }

        if (-not [string]::IsNullOrWhiteSpace($server)) {
            $directoryContext['Server'] = $server
        }

        if (-not [string]::IsNullOrWhiteSpace($username)) {
            $securePassword = ConvertTo-SyncFactorsSecureString -Value $bindPassword
            $directoryContext['Credential'] = [pscredential]::new($username, $securePassword)
        }

        Get-ADRootDSE -ErrorAction Stop @directoryContext | Out-Null
        return [pscustomobject]@{
            status = 'OK'
            detail = if (-not [string]::IsNullOrWhiteSpace($server)) { $server } else { 'default domain context' }
        }
    } catch {
        return [pscustomobject]@{
            status = 'ERROR'
            detail = $_.Exception.Message
        }
    }
}

function Format-SyncFactorsMonitorHealthSummary {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$Health
    )

    if (-not $Health) {
        return '-'
    }

    $status = if ($Health.PSObject.Properties.Name -contains 'status' -and -not [string]::IsNullOrWhiteSpace("$($Health.status)")) {
        "$($Health.status)"
    } else {
        '-'
    }

    $detail = if ($Health.PSObject.Properties.Name -contains 'detail' -and -not [string]::IsNullOrWhiteSpace("$($Health.detail)")) {
        "$($Health.detail)".Replace([Environment]::NewLine, ' ').Trim()
    } else {
        ''
    }

    if ([string]::IsNullOrWhiteSpace($detail)) {
        return $status
    }

    if ($detail.Length -gt 36) {
        $detail = $detail.Substring(0, 36) + '...'
    }

    return "$status ($detail)"
}

function New-SyncFactorsIdleRuntimeStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StatePath
    )

    return [pscustomobject]@{
        runId = $null
        status = 'Idle'
        mode = $null
        dryRun = $false
        stage = 'Completed'
        startedAt = $null
        lastUpdatedAt = $null
        completedAt = $null
        currentWorkerId = $null
        lastAction = 'No active sync run.'
        processedWorkers = 0
        totalWorkers = 0
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
        errorMessage = $null
        runtimeStatusPath = Get-SyncFactorsRuntimeStatusPath -StatePath $StatePath
    }
}

function New-SyncFactorsRuntimeStatusSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [string]$StatePath,
        [Parameter(Mandatory)]
        [string]$Stage,
        [string]$Status,
        [int]$ProcessedWorkers = 0,
        [int]$TotalWorkers = 0,
        [string]$CurrentWorkerId,
        [string]$LastAction,
        [string]$CompletedAt,
        [string]$ErrorMessage
    )

    $effectiveStatus = if ($PSBoundParameters.ContainsKey('Status') -and -not [string]::IsNullOrWhiteSpace($Status)) {
        $Status
    } elseif ($Report.Contains('status') -and -not [string]::IsNullOrWhiteSpace("$($Report['status'])")) {
        "$($Report['status'])"
    } else {
        'Idle'
    }

    return [pscustomobject][ordered]@{
        runId = if ($Report.Contains('runId')) { $Report['runId'] } else { $null }
        status = $effectiveStatus
        mode = if ($Report.Contains('mode')) { $Report['mode'] } else { $null }
        dryRun = if ($Report.Contains('dryRun')) { [bool]$Report['dryRun'] } else { $false }
        stage = $Stage
        startedAt = if ($Report.Contains('startedAt')) { $Report['startedAt'] } else { $null }
        lastUpdatedAt = (Get-Date).ToString('o')
        completedAt = $CompletedAt
        currentWorkerId = $CurrentWorkerId
        lastAction = $LastAction
        processedWorkers = $ProcessedWorkers
        totalWorkers = $TotalWorkers
        creates = if ($Report.Contains('creates')) { Get-SyncFactorsCollectionCount -Value $Report['creates'] } else { 0 }
        updates = if ($Report.Contains('updates')) { Get-SyncFactorsCollectionCount -Value $Report['updates'] } else { 0 }
        enables = if ($Report.Contains('enables')) { Get-SyncFactorsCollectionCount -Value $Report['enables'] } else { 0 }
        disables = if ($Report.Contains('disables')) { Get-SyncFactorsCollectionCount -Value $Report['disables'] } else { 0 }
        graveyardMoves = if ($Report.Contains('graveyardMoves')) { Get-SyncFactorsCollectionCount -Value $Report['graveyardMoves'] } else { 0 }
        deletions = if ($Report.Contains('deletions')) { Get-SyncFactorsCollectionCount -Value $Report['deletions'] } else { 0 }
        quarantined = if ($Report.Contains('quarantined')) { Get-SyncFactorsCollectionCount -Value $Report['quarantined'] } else { 0 }
        conflicts = if ($Report.Contains('conflicts')) { Get-SyncFactorsCollectionCount -Value $Report['conflicts'] } else { 0 }
        guardrailFailures = if ($Report.Contains('guardrailFailures')) { Get-SyncFactorsCollectionCount -Value $Report['guardrailFailures'] } else { 0 }
        manualReview = if ($Report.Contains('manualReview')) { Get-SyncFactorsCollectionCount -Value $Report['manualReview'] } else { 0 }
        unchanged = if ($Report.Contains('unchanged')) { Get-SyncFactorsCollectionCount -Value $Report['unchanged'] } else { 0 }
        errorMessage = $ErrorMessage
        runtimeStatusPath = Get-SyncFactorsRuntimeStatusPath -StatePath $StatePath
    }
}

function Save-SyncFactorsRuntimeStatusSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Snapshot,
        [Parameter(Mandatory)]
        [string]$StatePath
    )

    $runtimeStatusPath = Get-SyncFactorsRuntimeStatusPath -StatePath $StatePath
    $runtimeDirectory = Split-Path -Path $runtimeStatusPath -Parent
    if ($runtimeDirectory -and -not (Test-Path -Path $runtimeDirectory -PathType Container)) {
        New-Item -Path $runtimeDirectory -ItemType Directory -Force | Out-Null
    }

    $Snapshot | ConvertTo-Json -Depth 10 | Set-Content -Path $runtimeStatusPath
    Save-SyncFactorsRuntimeStatusToSqlite -Snapshot $Snapshot -StatePath $StatePath
    return $runtimeStatusPath
}

function Write-SyncFactorsRuntimeStatusSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [string]$StatePath,
        [Parameter(Mandatory)]
        [string]$Stage,
        [string]$Status,
        [int]$ProcessedWorkers = 0,
        [int]$TotalWorkers = 0,
        [string]$CurrentWorkerId,
        [string]$LastAction,
        [string]$CompletedAt,
        [string]$ErrorMessage
    )

    $snapshot = New-SyncFactorsRuntimeStatusSnapshot -Report $Report -StatePath $StatePath -Stage $Stage -Status $Status -ProcessedWorkers $ProcessedWorkers -TotalWorkers $TotalWorkers -CurrentWorkerId $CurrentWorkerId -LastAction $LastAction -CompletedAt $CompletedAt -ErrorMessage $ErrorMessage
    [void](Save-SyncFactorsRuntimeStatusSnapshot -Snapshot $snapshot -StatePath $StatePath)
    return $snapshot
}

function Get-SyncFactorsRuntimeStatusSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StatePath
    )

    $runtimeStatusPath = Get-SyncFactorsRuntimeStatusPath -StatePath $StatePath
    if (-not (Test-Path -Path $runtimeStatusPath -PathType Leaf)) {
        return Get-SyncFactorsRuntimeStatusSnapshotFromSqlite -StatePath $StatePath
    }

    try {
        return Get-Content -Path $runtimeStatusPath -Raw | ConvertFrom-Json -Depth 20
    } catch {
        return Get-SyncFactorsRuntimeStatusSnapshotFromSqlite -StatePath $StatePath
    }
}

function Get-SyncFactorsWorkerEntries {
    [CmdletBinding()]
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

function Get-SyncFactorsDateTimeOrNull {
    [CmdletBinding()]
    param($Value)

    if ([string]::IsNullOrWhiteSpace("$Value")) {
        return $null
    }

    try {
        return [datetimeoffset](Get-Date $Value)
    } catch {
        return $null
    }
}

function Get-SyncFactorsDurationSeconds {
    [CmdletBinding()]
    param(
        $StartedAt,
        $CompletedAt
    )

    $start = Get-SyncFactorsDateTimeOrNull -Value $StartedAt
    $end = Get-SyncFactorsDateTimeOrNull -Value $CompletedAt
    if ($null -eq $start -or $null -eq $end) {
        return $null
    }

    return [int][math]::Max(0, [math]::Round(($end - $start).TotalSeconds))
}

function New-SyncFactorsEmptyRunSummary {
    [CmdletBinding()]
    param()

    return [pscustomobject]@{
        runId = $null
        path = $null
        artifactType = 'SyncReport'
        workerScope = $null
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

function Get-SyncFactorsReportDirectories {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $directories = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @($Config.reporting.outputDirectory, $(if ($Config.reporting.PSObject.Properties.Name -contains 'reviewOutputDirectory') { $Config.reporting.reviewOutputDirectory } else { $null }))) {
        if ([string]::IsNullOrWhiteSpace("$candidate")) {
            continue
        }

        if ($directories -notcontains $candidate) {
            $directories.Add($candidate)
        }
    }

    return @($directories)
}

function ConvertTo-SyncFactorsRunSummary {
    [CmdletBinding()]
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
        durationSeconds = Get-SyncFactorsDurationSeconds -StartedAt $(if ($Report.PSObject.Properties.Name -contains 'startedAt') { $Report.startedAt } else { $null }) -CompletedAt $(if ($Report.PSObject.Properties.Name -contains 'completedAt') { $Report.completedAt } else { $null })
        reversibleOperations = Get-SyncFactorsCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'operations') { $Report.operations } else { @() })
        creates = Get-SyncFactorsCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'creates') { $Report.creates } else { @() })
        updates = Get-SyncFactorsCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'updates') { $Report.updates } else { @() })
        enables = Get-SyncFactorsCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'enables') { $Report.enables } else { @() })
        disables = Get-SyncFactorsCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'disables') { $Report.disables } else { @() })
        graveyardMoves = Get-SyncFactorsCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'graveyardMoves') { $Report.graveyardMoves } else { @() })
        deletions = Get-SyncFactorsCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'deletions') { $Report.deletions } else { @() })
        quarantined = Get-SyncFactorsCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'quarantined') { $Report.quarantined } else { @() })
        conflicts = Get-SyncFactorsCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'conflicts') { $Report.conflicts } else { @() })
        guardrailFailures = Get-SyncFactorsCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'guardrailFailures') { $Report.guardrailFailures } else { @() })
        manualReview = Get-SyncFactorsCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'manualReview') { $Report.manualReview } else { @() })
        unchanged = Get-SyncFactorsCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'unchanged') { $Report.unchanged } else { @() })
        reviewSummary = if ($Report.PSObject.Properties.Name -contains 'reviewSummary') { $Report.reviewSummary } else { $null }
    }
}

function Get-SyncFactorsRecentRunSummaries {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Directory,
        [ValidateRange(1, 1000)]
        [int]$Limit = 10
    )

    $directories = @($Directory | Where-Object { -not [string]::IsNullOrWhiteSpace("$_") } | Select-Object -Unique)
    if ($directories.Count -eq 0) {
        return @()
    }

    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    return @(
        @(foreach ($path in $directories) {
            if (-not (Test-Path -Path $path -PathType Container)) {
                continue
            }

            Get-ChildItem -Path $path -Filter 'syncfactors-*.json' -File
        }) |
            Sort-Object `
                @{ Expression = {
                        if ($_.BaseName -match '(\d{8}-\d{6})$') {
                            return [datetime]::ParseExact($Matches[1], 'yyyyMMdd-HHmmss', $culture)
                        }

                        return $_.LastWriteTime
                    }; Descending = $true }, `
                @{ Expression = { $_.Name }; Descending = $true } |
            Select-Object -First $Limit |
            ForEach-Object {
                $report = Get-Content -Path $_.FullName -Raw | ConvertFrom-Json -Depth 20
                ConvertTo-SyncFactorsRunSummary -Path $_.FullName -Report $report
            }
    )
}

function Get-SyncFactorsRecentRunSummariesFromPersistence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [ValidateRange(1, 1000)]
        [int]$Limit = 10
    )

    $sqlitePath = Get-SyncFactorsSqlitePath -Config $Config
    if (-not [string]::IsNullOrWhiteSpace($sqlitePath) -and (Test-Path -Path $sqlitePath -PathType Leaf)) {
        $recentRuns = @(Get-SyncFactorsRecentRunsFromSqlite -StatePath $Config.state.path -DatabasePath $sqlitePath -Limit $Limit)
        if ($recentRuns.Count -gt 0) {
            return $recentRuns
        }
    }

    return @(Get-SyncFactorsRecentRunSummaries -Directory (Get-SyncFactorsReportDirectories -Config $Config) -Limit $Limit)
}

function Get-SyncFactorsMonitorStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath,
        [ValidateRange(1, 1000)]
        [int]$HistoryLimit = 10
    )

    $config = Get-SyncFactorsConfig -Path $ConfigPath
    $sqlitePath = Get-SyncFactorsSqlitePath -Config $config
    $state = if ($config.state.path -and (Test-Path -Path $config.state.path -PathType Leaf)) { Get-SyncFactorsState -Path $config.state.path } else { [pscustomobject]@{ checkpoint = $null; workers = @{} } }
    $trackedWorkers = @()
    if (-not [string]::IsNullOrWhiteSpace($sqlitePath) -and (Test-Path -Path $sqlitePath -PathType Leaf)) {
        $trackedWorkers = @(Get-SyncFactorsTrackedWorkersFromSqlite -StatePath $config.state.path -DatabasePath $sqlitePath)
    }
    if ($trackedWorkers.Count -eq 0) {
        $workerProperties = @(Get-SyncFactorsWorkerEntries -Workers $state.workers)
        $trackedWorkers = @(
            $workerProperties |
                Sort-Object Name |
                ForEach-Object {
                    [pscustomobject]@{
                        workerId = $_.Name
                        adObjectGuid = if ($_.Value.PSObject.Properties.Name -contains 'adObjectGuid') { $_.Value.adObjectGuid } else { $null }
                        distinguishedName = if ($_.Value.PSObject.Properties.Name -contains 'distinguishedName') { $_.Value.distinguishedName } else { $null }
                        suppressed = if ($_.Value.PSObject.Properties.Name -contains 'suppressed') { [bool]$_.Value.suppressed } else { $false }
                        firstDisabledAt = if ($_.Value.PSObject.Properties.Name -contains 'firstDisabledAt') { $_.Value.firstDisabledAt } else { $null }
                        deleteAfter = if ($_.Value.PSObject.Properties.Name -contains 'deleteAfter') { $_.Value.deleteAfter } else { $null }
                        lastSeenStatus = if ($_.Value.PSObject.Properties.Name -contains 'lastSeenStatus') { $_.Value.lastSeenStatus } else { $null }
                    }
                }
        )
    }
    $workerProperties = @(
        $trackedWorkers | ForEach-Object {
            [pscustomobject]@{
                Name = $_.workerId
                Value = $_
            }
        }
    )
    $suppressedWorkers = @($workerProperties | Where-Object { $_.Value.suppressed })
    $pendingDeletionWorkers = @(
        $suppressedWorkers | Where-Object {
            $_.Value.deleteAfter -and ((Get-Date $_.Value.deleteAfter) -le (Get-Date))
        }
    )

    $reportDirectories = @(Get-SyncFactorsReportDirectories -Config $config)
    $recentRuns = @(Get-SyncFactorsRecentRunSummariesFromPersistence -Config $config -Limit $HistoryLimit)
    $latestRun = if ($recentRuns.Count -gt 0) { $recentRuns[0] } else { New-SyncFactorsEmptyRunSummary }
    $currentRun = Get-SyncFactorsRuntimeStatusSnapshot -StatePath $config.state.path
    if (-not $currentRun) {
        $currentRun = New-SyncFactorsIdleRuntimeStatus -StatePath $config.state.path
    }
    $successFactorsConnection = Test-SyncFactorsMonitorSuccessFactorsConnection -Config $config
    $activeDirectoryConnection = Test-SyncFactorsMonitorActiveDirectoryConnection -Config $config

    $resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
    $lastCheckpoint = if ($state -and $state.PSObject.Properties.Name -contains 'checkpoint') { $state.checkpoint } else { $null }
    if ([string]::IsNullOrWhiteSpace("$lastCheckpoint") -and -not [string]::IsNullOrWhiteSpace($sqlitePath) -and (Test-Path -Path $sqlitePath -PathType Leaf)) {
        $lastCheckpoint = Get-SyncFactorsStateCheckpointFromSqlite -StatePath $config.state.path -DatabasePath $sqlitePath
    }
    return [pscustomobject]@{
        configPath = $resolvedConfigPath
        lastCheckpoint = $lastCheckpoint
        totalTrackedWorkers = $workerProperties.Count
        suppressedWorkers = $suppressedWorkers.Count
        pendingDeletionWorkers = $pendingDeletionWorkers.Count
        latestReport = $latestRun
        latestRun = $latestRun
        currentRun = $currentRun
        recentRuns = $recentRuns
        summary = [pscustomobject]@{
            lastCheckpoint = $lastCheckpoint
            totalTrackedWorkers = $workerProperties.Count
            suppressedWorkers = $suppressedWorkers.Count
            pendingDeletionWorkers = $pendingDeletionWorkers.Count
        }
        health = [pscustomobject]@{
            successFactors = $successFactorsConnection
            activeDirectory = $activeDirectoryConnection
        }
        trackedWorkers = $trackedWorkers
        context = [pscustomobject]@{
            successFactorsAuth = Get-SyncFactorsSuccessFactorsAuthSummary -Config $config
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
            runtimeStatusPath = Get-SyncFactorsRuntimeStatusPath -StatePath $config.state.path
            sqlitePath = $sqlitePath
        }
    }
}

function Format-SyncFactorsMonitorView {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status
    )

    $authSummary = if (
        $Status.PSObject.Properties.Name -contains 'context' -and
        $Status.context -and
        $Status.context.PSObject.Properties.Name -contains 'successFactorsAuth' -and
        -not [string]::IsNullOrWhiteSpace("$($Status.context.successFactorsAuth)")
    ) { "$($Status.context.successFactorsAuth)" } else { '-' }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('SuccessFactors AD Sync Monitor')
    $lines.Add("Config: $($Status.paths.configPath)")
    $lines.Add("SuccessFactors auth: $authSummary")
    $lines.Add("Health: SF=$(Format-SyncFactorsMonitorHealthSummary -Health $Status.health.successFactors)    AD=$(Format-SyncFactorsMonitorHealthSummary -Health $Status.health.activeDirectory)")
    $lines.Add("Refreshed: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))")
    $lines.Add('')
    $lines.Add('Current Run')
    $lines.Add("Status: $($Status.currentRun.status)    Stage: $($Status.currentRun.stage)    Mode: $($Status.currentRun.mode)    DryRun: $($Status.currentRun.dryRun)")
    $lines.Add("Started: $($Status.currentRun.startedAt)    Completed: $($Status.currentRun.completedAt)")
    $lines.Add("Progress: $($Status.currentRun.processedWorkers) / $($Status.currentRun.totalWorkers)    Worker: $($Status.currentRun.currentWorkerId)")
    $lines.Add("Last action: $($Status.currentRun.lastAction)")
    if ($Status.currentRun.errorMessage) {
        $lines.Add("Error: $($Status.currentRun.errorMessage)")
    }
    $lines.Add("Counts: C=$($Status.currentRun.creates) U=$($Status.currentRun.updates) E=$($Status.currentRun.enables) D=$($Status.currentRun.disables) G=$($Status.currentRun.graveyardMoves) X=$($Status.currentRun.deletions) Q=$($Status.currentRun.quarantined) F=$($Status.currentRun.conflicts) GF=$($Status.currentRun.guardrailFailures) MR=$($Status.currentRun.manualReview) NC=$($Status.currentRun.unchanged)")
    $lines.Add('')
    $lines.Add('State Summary')
    $lines.Add("Checkpoint: $($Status.summary.lastCheckpoint)")
    $lines.Add("Tracked: $($Status.summary.totalTrackedWorkers)    Suppressed: $($Status.summary.suppressedWorkers)    Pending deletion: $($Status.summary.pendingDeletionWorkers)")
    $lines.Add('')
    $lines.Add('Recent Runs')
    $lines.Add('Status     Mode  Started             Dur(s) Create Update Disable Delete Conflict Guardrail')
    foreach ($run in @($Status.recentRuns)) {
        $lines.Add(("{0,-10} {1,-5} {2,-19} {3,6} {4,6} {5,6} {6,7} {7,6} {8,8} {9,9}" -f `
                $(if ($run.status) { $run.status } else { '-' }), `
                $(if ($run.mode) { $run.mode } else { '-' }), `
                $(if ($run.startedAt) { $run.startedAt } else { '-' }), `
                $(if ($null -ne $run.durationSeconds) { $run.durationSeconds } else { '-' }), `
                $run.creates, `
                $run.updates, `
                $run.disables, `
                $run.deletions, `
                $run.conflicts, `
                $run.guardrailFailures))
    }

    if (@($Status.recentRuns).Count -eq 0) {
        $lines.Add('No sync reports found.')
    }

    $lines.Add('')
    $lines.Add('Keys: q quit, r refresh')
    return $lines
}

function New-SyncFactorsMonitorUiState {
    [CmdletBinding()]
    param()

    return [pscustomobject]@{
        viewMode = 'Dashboard'
        selectedRunIndex = 0
        selectedBucketIndex = 0
        selectedItemIndex = 0
        reportCategoryIndex = 0
        reportEntryIndex = 0
        pendingReportIndex = 0
        focus = 'History'
        filterText = ''
        autoRefreshEnabled = $false
        preferredMode = $null
        pendingAction = $null
        pendingWorkerId = $null
        workerPreviewResult = $null
        workerPreviewDiffRows = @()
        statusMessage = 'Ready. Keys: q quit, r refresh, t toggle auto-refresh, tab focus, arrows or j/k select run, [ ] bucket, left/right or h/l select item, / filter, c clear filter, p preflight, d delta dry-run, s delta sync, f full dry-run, a full sync, w worker preview, v review, z fresh reset, o open report explorer, y copy path, x export bucket.'
        commandOutput = @()
    }
}

function Get-SyncFactorsMonitorBucketDefinitions {
    [CmdletBinding()]
    param(
        [string]$Mode
    )

    if ("$Mode" -eq 'Review') {
        return @(
            [pscustomobject]@{ Name = 'updates'; Label = 'Existing Changes' }
            [pscustomobject]@{ Name = 'unchanged'; Label = 'Existing Aligned' }
            [pscustomobject]@{ Name = 'creates'; Label = 'New Users' }
            [pscustomobject]@{ Name = 'disables'; Label = 'Offboarding' }
            [pscustomobject]@{ Name = 'graveyardMoves'; Label = 'Placement Changes' }
            [pscustomobject]@{ Name = 'enables'; Label = 'Enable Candidates' }
            [pscustomobject]@{ Name = 'quarantined'; Label = 'Quarantined' }
            [pscustomobject]@{ Name = 'conflicts'; Label = 'Conflicts' }
            [pscustomobject]@{ Name = 'manualReview'; Label = 'Manual Review' }
            [pscustomobject]@{ Name = 'guardrailFailures'; Label = 'Guardrails' }
        )
    }

    return @(
        [pscustomobject]@{ Name = 'quarantined'; Label = 'Quarantined' }
        [pscustomobject]@{ Name = 'conflicts'; Label = 'Conflicts' }
        [pscustomobject]@{ Name = 'manualReview'; Label = 'Manual Review' }
        [pscustomobject]@{ Name = 'guardrailFailures'; Label = 'Guardrails' }
        [pscustomobject]@{ Name = 'creates'; Label = 'Creates' }
        [pscustomobject]@{ Name = 'updates'; Label = 'Updates' }
        [pscustomobject]@{ Name = 'enables'; Label = 'Enables' }
        [pscustomobject]@{ Name = 'disables'; Label = 'Disables' }
        [pscustomobject]@{ Name = 'graveyardMoves'; Label = 'Graveyard Moves' }
        [pscustomobject]@{ Name = 'deletions'; Label = 'Deletions' }
        [pscustomobject]@{ Name = 'unchanged'; Label = 'Unchanged' }
    )
}

function Get-SyncFactorsMonitorSelectedRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $runs = @($Status.recentRuns)
    if ($runs.Count -eq 0) {
        return $Status.latestRun
    }

    $index = [math]::Min([math]::Max([int]$UiState.selectedRunIndex, 0), $runs.Count - 1)
    return $runs[$index]
}

function Get-SyncFactorsMonitorSelectedRunReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $selectedRun = Get-SyncFactorsMonitorSelectedRun -Status $Status -UiState $UiState
    if (-not $selectedRun) {
        return $null
    }

    $sqlitePath = if (
        $Status.PSObject.Properties.Name -contains 'paths' -and
        $Status.paths -and
        $Status.paths.PSObject.Properties.Name -contains 'sqlitePath'
    ) {
        "$($Status.paths.sqlitePath)"
    } else {
        $null
    }
    if (-not [string]::IsNullOrWhiteSpace($sqlitePath) -and (Test-Path -Path $sqlitePath -PathType Leaf) -and $selectedRun.PSObject.Properties.Name -contains 'runId' -and -not [string]::IsNullOrWhiteSpace("$($selectedRun.runId)")) {
        $report = Get-SyncFactorsRunReportFromSqlite -RunId "$($selectedRun.runId)" -DatabasePath $sqlitePath
        if ($report) {
            return $report
        }
    }

    if ([string]::IsNullOrWhiteSpace("$($selectedRun.path)")) {
        return $null
    }

    if (-not (Test-Path -Path $selectedRun.path -PathType Leaf)) {
        return $null
    }

    return Get-Content -Path $selectedRun.path -Raw | ConvertFrom-Json -Depth 20
}

function Get-SyncFactorsMonitorSelectedBucket {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $selectedRun = Get-SyncFactorsMonitorSelectedRun -Status $Status -UiState $UiState
    $selectedRunMode = if ($selectedRun -and $selectedRun.PSObject.Properties.Name -contains 'mode') { $selectedRun.mode } else { $null }
    $buckets = @(Get-SyncFactorsMonitorBucketDefinitions -Mode $selectedRunMode)
    $index = if ($buckets.Count -eq 0) { 0 } else { [math]::Min([math]::Max([int]$UiState.selectedBucketIndex, 0), $buckets.Count - 1) }
    $bucket = if ($buckets.Count -eq 0) { [pscustomobject]@{ Name = 'quarantined'; Label = 'Quarantined' } } else { $buckets[$index] }

    $report = Get-SyncFactorsMonitorSelectedRunReport -Status $Status -UiState $UiState
    $items = @()
    if ($report -and $report.PSObject.Properties.Name -contains $bucket.Name) {
        $items = @($report.$($bucket.Name))
    }

    return [pscustomobject]@{
        Bucket = $bucket
        Items = $items
    }
}

function Resolve-SyncFactorsMonitorMappingConfigPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [string]$MappingConfigPath
    )

    if (-not [string]::IsNullOrWhiteSpace($MappingConfigPath)) {
        return (Resolve-Path -Path $MappingConfigPath).Path
    }

    foreach ($run in @($Status.recentRuns)) {
        if ($run -and $run.PSObject.Properties.Name -contains 'mappingConfigPath' -and -not [string]::IsNullOrWhiteSpace("$($run.mappingConfigPath)") -and (Test-Path -Path $run.mappingConfigPath -PathType Leaf)) {
            return (Resolve-Path -Path $run.mappingConfigPath).Path
        }
    }

    return $null
}

function Resolve-SyncFactorsMonitorSelectedReportPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $selectedRun = Get-SyncFactorsMonitorSelectedRun -Status $Status -UiState $UiState
    if (-not $selectedRun -or [string]::IsNullOrWhiteSpace("$($selectedRun.path)")) {
        return $null
    }

    return $selectedRun.path
}

function Get-SyncFactorsMonitorActionContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState,
        [string]$MappingConfigPath
    )

    $selectedRun = Get-SyncFactorsMonitorSelectedRun -Status $Status -UiState $UiState
    return [pscustomobject]@{
        configPath = $Status.paths.configPath
        mappingConfigPath = Resolve-SyncFactorsMonitorMappingConfigPath -Status $Status -MappingConfigPath $MappingConfigPath
        reportPath = Resolve-SyncFactorsMonitorSelectedReportPath -Status $Status -UiState $UiState
        selectedRun = $selectedRun
        selectedBucket = Get-SyncFactorsMonitorSelectedBucket -Status $Status -UiState $UiState
    }
}

function ConvertTo-SyncFactorsMonitorInlineText {
    [CmdletBinding()]
    param($Value)

    if ($null -eq $Value) {
        return ''
    }

    if ($Value -is [System.Array]) {
        return (@($Value) -join ', ')
    }

    if ($Value -is [System.Collections.IDictionary]) {
        return (@($Value.Keys | ForEach-Object { "$_=$($Value[$_])" }) -join ', ')
    }

    $properties = @($Value.PSObject.Properties)
    if ($properties.Count -gt 0 -and -not ($Value -is [string])) {
        return (@($properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', ')
    }

    return "$Value"
}

function Test-SyncFactorsMonitorItemMatchesFilter {
    [CmdletBinding()]
    param(
        $Item,
        [string]$FilterText
    )

    if ([string]::IsNullOrWhiteSpace($FilterText)) {
        return $true
    }

    $needle = $FilterText.Trim().ToLowerInvariant()
    $haystack = (ConvertTo-SyncFactorsMonitorInlineText -Value $Item).ToLowerInvariant()
    return $haystack.Contains($needle)
}

function Get-SyncFactorsMonitorFilteredBucketItems {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$BucketSelection,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    return @(
        @($BucketSelection.Items) | Where-Object {
            Test-SyncFactorsMonitorItemMatchesFilter -Item $_ -FilterText $UiState.filterText
        }
    )
}

function Get-SyncFactorsMonitorSelectedBucketItem {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$BucketSelection,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $items = @(Get-SyncFactorsMonitorFilteredBucketItems -BucketSelection $BucketSelection -UiState $UiState)
    if ($items.Count -eq 0) {
        return $null
    }

    $index = [math]::Min([math]::Max([int]$UiState.selectedItemIndex, 0), $items.Count - 1)
    return $items[$index]
}

function Get-SyncFactorsMonitorSelectedWorkerId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $bucketSelection = Get-SyncFactorsMonitorSelectedBucket -Status $Status -UiState $UiState
    $selectedItem = Get-SyncFactorsMonitorSelectedBucketItem -BucketSelection $bucketSelection -UiState $UiState
    if ($selectedItem -and $selectedItem.PSObject.Properties.Name -contains 'workerId' -and -not [string]::IsNullOrWhiteSpace("$($selectedItem.workerId)")) {
        return "$($selectedItem.workerId)"
    }

    if (-not [string]::IsNullOrWhiteSpace("$($Status.currentRun.currentWorkerId)")) {
        return "$($Status.currentRun.currentWorkerId)"
    }

    return $null
}

function Get-SyncFactorsMonitorSelectedBucketOperation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $report = Get-SyncFactorsMonitorSelectedRunReport -Status $Status -UiState $UiState
    if (-not $report -or -not ($report.PSObject.Properties.Name -contains 'operations')) {
        return $null
    }

    $bucketSelection = Get-SyncFactorsMonitorSelectedBucket -Status $Status -UiState $UiState
    $selectedItem = Get-SyncFactorsMonitorSelectedBucketItem -BucketSelection $bucketSelection -UiState $UiState
    $workerId = if ($selectedItem -and $selectedItem.PSObject.Properties.Name -contains 'workerId') { "$($selectedItem.workerId)" } else { $null }
    if ([string]::IsNullOrWhiteSpace($workerId)) {
        return $null
    }

    $operations = @($report.operations | Where-Object {
        "$($_.workerId)" -eq $workerId -and "$($_.bucket)" -eq $bucketSelection.Bucket.Name
    } | Sort-Object sequence -Descending)

    if ($operations.Count -eq 0) {
        return $null
    }

    return $operations[0]
}

function Test-SyncFactorsMonitorSelectedRunIsReview {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $selectedRun = Get-SyncFactorsMonitorSelectedRun -Status $Status -UiState $UiState
    $mode = if ($selectedRun -and $selectedRun.PSObject.Properties.Name -contains 'mode') { "$($selectedRun.mode)" } else { '' }
    $artifactType = if ($selectedRun -and $selectedRun.PSObject.Properties.Name -contains 'artifactType') { "$($selectedRun.artifactType)" } else { '' }
    return $mode -eq 'Review' -or $artifactType -in @('FirstSyncReview', 'WorkerPreview')
}

function Test-SyncFactorsMonitorSelectedRunIsWorkerPreview {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $selectedRun = Get-SyncFactorsMonitorSelectedRun -Status $Status -UiState $UiState
    if (-not $selectedRun) {
        return $false
    }

    if (-not ($selectedRun.PSObject.Properties.Name -contains 'artifactType')) {
        return $false
    }

    return "$($selectedRun.artifactType)" -eq 'WorkerPreview'
}

function Get-SyncFactorsMonitorSelectedRunWorkerId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $selectedRun = Get-SyncFactorsMonitorSelectedRun -Status $Status -UiState $UiState
    if (
        $selectedRun -and
        $selectedRun.PSObject.Properties.Name -contains 'workerScope' -and
        $selectedRun.workerScope -and
        $selectedRun.workerScope.PSObject.Properties.Name -contains 'workerId' -and
        -not [string]::IsNullOrWhiteSpace("$($selectedRun.workerScope.workerId)")
    ) {
        return "$($selectedRun.workerScope.workerId)"
    }

    return $null
}

function Get-SyncFactorsMonitorWorkerRelatedRuns {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [string]$WorkerId
    )

    if ([string]::IsNullOrWhiteSpace($WorkerId)) {
        return @()
    }

    $results = [System.Collections.Generic.List[object]]::new()
    $runs = @($Status.recentRuns)
    for ($index = 0; $index -lt $runs.Count; $index += 1) {
        $run = $runs[$index]
        if (
            -not $run -or
            -not ($run.PSObject.Properties.Name -contains 'workerScope') -or
            -not $run.workerScope -or
            -not ($run.workerScope.PSObject.Properties.Name -contains 'workerId') -or
            [string]::IsNullOrWhiteSpace("$($run.workerScope.workerId)") -or
            "$($run.workerScope.workerId)" -ne $WorkerId
        ) {
            continue
        }

        $artifactType = if ($run.PSObject.Properties.Name -contains 'artifactType' -and -not [string]::IsNullOrWhiteSpace("$($run.artifactType)")) {
            "$($run.artifactType)"
        } else {
            'Run'
        }
        $statusText = if ($run.PSObject.Properties.Name -contains 'status' -and -not [string]::IsNullOrWhiteSpace("$($run.status)")) {
            "$($run.status)"
        } else {
            '-'
        }
        $startedAtText = if ($run.PSObject.Properties.Name -contains 'startedAt' -and -not [string]::IsNullOrWhiteSpace("$($run.startedAt)")) {
            "$($run.startedAt)"
        } else {
            '-'
        }

        $results.Add([pscustomobject]@{
                RunIndex = $index
                Run = $run
                Label = ("{0} {1} {2}" -f $artifactType, $statusText, $startedAtText)
            })
    }

    return @($results)
}

function Get-SyncFactorsMonitorFailureGroups {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$BucketSelection,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $items = @(Get-SyncFactorsMonitorFilteredBucketItems -BucketSelection $BucketSelection -UiState $UiState)
    return @(
        $items |
            Group-Object -Property {
                if ($_.PSObject.Properties.Name -contains 'reason' -and -not [string]::IsNullOrWhiteSpace("$($_.reason)")) {
                    return "reason:$($_.reason)"
                }
                if ($_.PSObject.Properties.Name -contains 'threshold' -and -not [string]::IsNullOrWhiteSpace("$($_.threshold)")) {
                    return "threshold:$($_.threshold)"
                }
                return 'misc'
            } |
            Sort-Object `
                @{ Expression = { $_.Count }; Descending = $true }, `
                @{ Expression = { $_.Name }; Descending = $false } |
            ForEach-Object {
                $label = if ($_.Name -like 'reason:*') {
                    $_.Name.Substring(7)
                } elseif ($_.Name -like 'threshold:*') {
                    $_.Name.Substring(10)
                } else {
                    'Other'
                }

                [pscustomobject]@{
                    label = $label
                    count = $_.Count
                }
            }
    )
}

function Get-SyncFactorsMonitorFilteredTrackedWorkers {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    return @(
        @($Status.trackedWorkers) | Where-Object {
            Test-SyncFactorsMonitorItemMatchesFilter -Item $_ -FilterText $UiState.filterText
        }
    )
}

function Get-SyncFactorsMonitorSelectedWorkerState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $workerId = Get-SyncFactorsMonitorSelectedWorkerId -Status $Status -UiState $UiState
    if ([string]::IsNullOrWhiteSpace($workerId)) {
        return $null
    }

    $worker = @($Status.trackedWorkers | Where-Object { "$($_.workerId)" -eq $workerId })
    if ($worker.Count -eq 0) {
        return $null
    }

    return $worker[0]
}

function Get-SyncFactorsMonitorCurrentRunDiagnostics {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$CurrentRun
    )

    $startedAt = Get-SyncFactorsDateTimeOrNull -Value $CurrentRun.startedAt
    $lastUpdatedAt = Get-SyncFactorsDateTimeOrNull -Value $CurrentRun.lastUpdatedAt
    $now = [datetimeoffset](Get-Date)
    $elapsedSeconds = if ($startedAt) { [int][math]::Max(0, [math]::Round(($now - $startedAt).TotalSeconds)) } else { $null }
    $refreshLagSeconds = if ($lastUpdatedAt) { [int][math]::Max(0, [math]::Round(($now - $lastUpdatedAt).TotalSeconds)) } else { $null }
    $throughput = if ($elapsedSeconds -and $elapsedSeconds -gt 0 -and $CurrentRun.processedWorkers -gt 0) {
        [math]::Round(($CurrentRun.processedWorkers / $elapsedSeconds), 2)
    } else {
        $null
    }
    $etaSeconds = if ($throughput -and $throughput -gt 0 -and $CurrentRun.totalWorkers -gt $CurrentRun.processedWorkers) {
        [int][math]::Ceiling((($CurrentRun.totalWorkers - $CurrentRun.processedWorkers) / $throughput))
    } else {
        $null
    }

    return [pscustomobject]@{
        elapsedSeconds = $elapsedSeconds
        refreshLagSeconds = $refreshLagSeconds
        throughput = $throughput
        etaSeconds = $etaSeconds
    }
}

function Get-SyncFactorsMonitorPropertyPairs {
    [CmdletBinding()]
    param($Value)

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [System.Collections.IDictionary]) {
        return @(
            foreach ($key in $Value.Keys) {
                [pscustomobject]@{
                    Name = "$key"
                    Value = $Value[$key]
                }
            }
        )
    }

    return @(
        $Value.PSObject.Properties | ForEach-Object {
            [pscustomobject]@{
                Name = $_.Name
                Value = $_.Value
            }
        }
    )
}

function Get-SyncFactorsMonitorOperationDiffLines {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Operation
    )

    $beforeMap = @{}
    foreach ($property in @(Get-SyncFactorsMonitorPropertyPairs -Value $Operation.before)) {
        $beforeMap[$property.Name] = ConvertTo-SyncFactorsMonitorInlineText -Value $property.Value
    }

    $afterMap = @{}
    foreach ($property in @(Get-SyncFactorsMonitorPropertyPairs -Value $Operation.after)) {
        $afterMap[$property.Name] = ConvertTo-SyncFactorsMonitorInlineText -Value $property.Value
    }

    $keys = @($beforeMap.Keys + $afterMap.Keys | Sort-Object -Unique)
    if ($keys.Count -eq 0) {
        return @()
    }

    return @(
        foreach ($key in $keys) {
            $beforeValue = if ($beforeMap.ContainsKey($key)) { $beforeMap[$key] } else { '(unset)' }
            $afterValue = if ($afterMap.ContainsKey($key)) { $afterMap[$key] } else { '(unset)' }
            "${key}: $beforeValue -> $afterValue"
        }
    )
}

function Get-SyncFactorsMonitorBucketDisplayLabel {
    [CmdletBinding()]
    param(
        [string]$BucketName
    )

    switch ("$BucketName") {
        'creates' { return 'Create account' }
        'updates' { return 'Update attributes' }
        'enables' { return 'Enable account' }
        'disables' { return 'Disable account' }
        'graveyardMoves' { return 'Move to graveyard OU' }
        'deletions' { return 'Delete account' }
        'unchanged' { return 'No change' }
        default { return $BucketName }
    }
}

function Get-SyncFactorsMonitorOperationSummaryLines {
    [CmdletBinding()]
    param(
        $Item,
        $Operation
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    if (-not $Operation) {
        return @()
    }

    $targetSam = if (
        $Operation.PSObject.Properties.Name -contains 'target' -and
        $Operation.target -and
        $Operation.target.PSObject.Properties.Name -contains 'samAccountName' -and
        -not [string]::IsNullOrWhiteSpace("$($Operation.target.samAccountName)")
    ) {
        "$($Operation.target.samAccountName)"
    } elseif ($Item -and $Item.PSObject.Properties.Name -contains 'samAccountName') {
        "$($Item.samAccountName)"
    } else {
        'user'
    }

    switch ("$($Operation.operationType)") {
        'DisableUser' {
            $lines.Add("Action: Disable account $targetSam")
            $lines.Add('Effect: Account sign-in will be turned off.')
        }
        'EnableUser' {
            $lines.Add("Action: Enable account $targetSam")
            $lines.Add('Effect: Account sign-in will be turned on.')
        }
        'MoveUser' {
            $fromOu = if ($Operation.before -and $Operation.before.PSObject.Properties.Name -contains 'parentOu' -and -not [string]::IsNullOrWhiteSpace("$($Operation.before.parentOu)")) {
                "$($Operation.before.parentOu)"
            } else {
                '(unknown)'
            }
            $toOu = if ($Operation.after -and $Operation.after.PSObject.Properties.Name -contains 'targetOu' -and -not [string]::IsNullOrWhiteSpace("$($Operation.after.targetOu)")) {
                "$($Operation.after.targetOu)"
            } else {
                $(if ($Item -and $Item.PSObject.Properties.Name -contains 'targetOu' -and -not [string]::IsNullOrWhiteSpace("$($Item.targetOu)")) { "$($Item.targetOu)" } else { '(unknown)' })
            }
            $lines.Add("Action: Move account $targetSam")
            $lines.Add("From OU: $fromOu")
            $lines.Add("To OU: $toOu")
        }
        'CreateUser' {
            $targetOu = if ($Operation.after -and $Operation.after.PSObject.Properties.Name -contains 'targetOu' -and -not [string]::IsNullOrWhiteSpace("$($Operation.after.targetOu)")) {
                "$($Operation.after.targetOu)"
            } elseif ($Item -and $Item.PSObject.Properties.Name -contains 'targetOu' -and -not [string]::IsNullOrWhiteSpace("$($Item.targetOu)")) {
                "$($Item.targetOu)"
            } else {
                '(configured default)'
            }
            $lines.Add("Action: Create account $targetSam")
            $lines.Add("Target OU: $targetOu")
        }
        'DeleteUser' {
            $lines.Add("Action: Delete account $targetSam")
            $lines.Add('Effect: The AD user object will be removed.')
        }
        'UpdateAttributes' {
            $attributeCount = @(Get-SyncFactorsMonitorOperationDiffLines -Operation $Operation).Count
            $lines.Add("Action: Update attributes for $targetSam")
            $lines.Add("Effect: $attributeCount attribute$(if ($attributeCount -eq 1) { '' } else { 's' }) will change.")
        }
        'AddGroupMembership' {
            $lines.Add("Action: Add group membership for $targetSam")
        }
        default {
            $lines.Add("Action: $($Operation.operationType) for $targetSam")
        }
    }

    return @($lines)
}

function Get-SyncFactorsMonitorOperatorActionLines {
    [CmdletBinding()]
    param(
        $Item
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    if (-not $Item) {
        return @()
    }

    if ($Item.PSObject.Properties.Name -contains 'reviewCaseType' -and -not [string]::IsNullOrWhiteSpace("$($Item.reviewCaseType)")) {
        $lines.Add("Review workflow: $($Item.reviewCaseType)")
    }
    if ($Item.PSObject.Properties.Name -contains 'operatorActionSummary' -and -not [string]::IsNullOrWhiteSpace("$($Item.operatorActionSummary)")) {
        $lines.Add("Operator summary: $($Item.operatorActionSummary)")
    }
    if ($Item.PSObject.Properties.Name -contains 'operatorActions' -and $Item.operatorActions) {
        foreach ($action in @($Item.operatorActions) | Select-Object -First 3) {
            $label = if ($action.PSObject.Properties.Name -contains 'label' -and -not [string]::IsNullOrWhiteSpace("$($action.label)")) { "$($action.label)" } else { 'Action' }
            $description = if ($action.PSObject.Properties.Name -contains 'description' -and -not [string]::IsNullOrWhiteSpace("$($action.description)")) { "$($action.description)" } else { '' }
            if ([string]::IsNullOrWhiteSpace($description)) {
                $lines.Add("Operator action: $label")
            } else {
                $lines.Add("Operator action: $label - $description")
            }
        }
        if (@($Item.operatorActions).Count -gt 3) {
            $lines.Add("... $(@($Item.operatorActions).Count - 3) more operator actions")
        }
    }

    return @($lines)
}

function Get-SyncFactorsMonitorReportExplorerCategoryDefinitions {
    [CmdletBinding()]
    param()

    return @(
        [pscustomobject]@{ Name = 'Changed'; Label = 'Changed'; Marker = '[UPDATE]' }
        [pscustomobject]@{ Name = 'Created'; Label = 'Created'; Marker = '[CREATE]' }
        [pscustomobject]@{ Name = 'Deleted'; Label = 'Deleted'; Marker = '[DELETE]' }
    )
}

function Get-SyncFactorsMonitorOperationForItem {
    [CmdletBinding()]
    param(
        [pscustomobject]$Report,
        [string]$BucketName,
        $Item
    )

    if (-not $Report -or -not ($Report.PSObject.Properties.Name -contains 'operations')) {
        return $null
    }

    $workerId = if ($Item -and $Item.PSObject.Properties.Name -contains 'workerId') { "$($Item.workerId)" } else { $null }
    $samAccountName = if ($Item -and $Item.PSObject.Properties.Name -contains 'samAccountName') { "$($Item.samAccountName)" } else { $null }
    $userPrincipalName = if ($Item -and $Item.PSObject.Properties.Name -contains 'userPrincipalName') { "$($Item.userPrincipalName)" } else { $null }

    $matches = @($Report.operations | Where-Object {
            if ($BucketName -and "$($_.bucket)" -ne $BucketName) {
                return $false
            }

            if (-not [string]::IsNullOrWhiteSpace($workerId) -and "$($_.workerId)" -eq $workerId) {
                return $true
            }

            if (
                -not [string]::IsNullOrWhiteSpace($samAccountName) -and
                $_.target -and
                $_.target.PSObject.Properties.Name -contains 'samAccountName' -and
                "$($_.target.samAccountName)" -eq $samAccountName
            ) {
                return $true
            }

            if (
                -not [string]::IsNullOrWhiteSpace($userPrincipalName) -and
                $_.target -and
                $_.target.PSObject.Properties.Name -contains 'userPrincipalName' -and
                "$($_.target.userPrincipalName)" -eq $userPrincipalName
            ) {
                return $true
            }

            return $false
        } | Sort-Object sequence -Descending)

    if ($matches.Count -eq 0) {
        return $null
    }

    return $matches[0]
}

function Get-SyncFactorsMonitorReportExplorerEntries {
    [CmdletBinding()]
    param(
        [pscustomobject]$Report
    )

    if (-not $Report) {
        return @()
    }

    $bucketCategoryMap = @{
        updates = 'Changed'
        enables = 'Changed'
        disables = 'Changed'
        graveyardMoves = 'Changed'
        creates = 'Created'
        deletions = 'Deleted'
    }

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($bucketName in @('updates', 'enables', 'disables', 'graveyardMoves', 'creates', 'deletions')) {
        if ($Report.PSObject.Properties.Name -notcontains $bucketName) {
            continue
        }

        foreach ($item in @($Report.$bucketName)) {
            $operation = Get-SyncFactorsMonitorOperationForItem -Report $Report -BucketName $bucketName -Item $item
            $changeCount = 0
            if ($item -and $item.PSObject.Properties.Name -contains 'changedAttributeDetails') {
                $changeCount = @($item.changedAttributeDetails).Count
            } elseif ($item -and $item.PSObject.Properties.Name -contains 'attributeRows') {
                $changeCount = @($item.attributeRows | Where-Object { $_.changed }).Count
            } elseif ($operation) {
                $changeCount = @(Get-SyncFactorsMonitorOperationDiffLines -Operation $operation).Count
            }

            $entries.Add([pscustomobject]@{
                    Category = $bucketCategoryMap[$bucketName]
                    BucketName = $bucketName
                    BucketLabel = Get-SyncFactorsMonitorBucketDisplayLabel -BucketName $bucketName
                    WorkerId = if ($item -and $item.PSObject.Properties.Name -contains 'workerId') { "$($item.workerId)" } else { '' }
                    SamAccountName = if ($item -and $item.PSObject.Properties.Name -contains 'samAccountName') { "$($item.samAccountName)" } else { '' }
                    UserPrincipalName = if ($item -and $item.PSObject.Properties.Name -contains 'userPrincipalName') { "$($item.userPrincipalName)" } else { '' }
                    Reason = if ($item -and $item.PSObject.Properties.Name -contains 'reason') { "$($item.reason)" } else { '' }
                    ReviewCategory = if ($item -and $item.PSObject.Properties.Name -contains 'reviewCategory') { "$($item.reviewCategory)" } else { '' }
                    TargetOu = if ($item -and $item.PSObject.Properties.Name -contains 'targetOu') { "$($item.targetOu)" } else { '' }
                    ChangeCount = $changeCount
                    Item = $item
                    Operation = $operation
                })
        }
    }

    return @($entries)
}

function Get-SyncFactorsMonitorReportExplorerSelection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $report = Get-SyncFactorsMonitorSelectedRunReport -Status $Status -UiState $UiState
    $categories = @(Get-SyncFactorsMonitorReportExplorerCategoryDefinitions)
    $entries = @(Get-SyncFactorsMonitorReportExplorerEntries -Report $report)
    $categoryIndex = if ($categories.Count -eq 0) { 0 } else { [math]::Min([math]::Max([int]$UiState.reportCategoryIndex, 0), $categories.Count - 1) }
    $selectedCategory = if ($categories.Count -eq 0) { $null } else { $categories[$categoryIndex] }
    $categoryEntries = if ($selectedCategory) { @($entries | Where-Object { $_.Category -eq $selectedCategory.Name }) } else { @() }
    $entryIndex = if ($categoryEntries.Count -eq 0) { 0 } else { [math]::Min([math]::Max([int]$UiState.reportEntryIndex, 0), $categoryEntries.Count - 1) }
    $selectedEntry = if ($categoryEntries.Count -eq 0) { $null } else { $categoryEntries[$entryIndex] }

    return [pscustomobject]@{
        Report = $report
        Categories = @($categories)
        Entries = @($entries)
        SelectedCategory = $selectedCategory
        CategoryEntries = @($categoryEntries)
        SelectedEntry = $selectedEntry
        SelectedCategoryIndex = $categoryIndex
        SelectedEntryIndex = $entryIndex
    }
}

function Get-SyncFactorsMonitorReportExplorerDiffRows {
    [CmdletBinding()]
    param(
        $Entry
    )

    if (-not $Entry) {
        return @()
    }

    $item = $Entry.Item
    if ($item -and $item.PSObject.Properties.Name -contains 'changedAttributeDetails') {
        return @(
            foreach ($row in @($item.changedAttributeDetails)) {
                [pscustomobject]@{
                    Marker = '[UPDATE]'
                    Attribute = "$($row.targetAttribute)"
                    Before = ConvertTo-SyncFactorsMonitorInlineText -Value $row.currentAdValue
                    After = ConvertTo-SyncFactorsMonitorInlineText -Value $row.proposedValue
                    Source = if ($row.PSObject.Properties.Name -contains 'sourceField') { "$($row.sourceField)" } else { '' }
                }
            }
        )
    }

    if ($item -and $item.PSObject.Properties.Name -contains 'attributeRows') {
        return @(
            foreach ($row in @($item.attributeRows | Where-Object { $_.changed })) {
                [pscustomobject]@{
                    Marker = '[UPDATE]'
                    Attribute = "$($row.targetAttribute)"
                    Before = ConvertTo-SyncFactorsMonitorInlineText -Value $row.currentAdValue
                    After = ConvertTo-SyncFactorsMonitorInlineText -Value $row.proposedValue
                    Source = if ($row.PSObject.Properties.Name -contains 'sourceField') { "$($row.sourceField)" } else { '' }
                }
            }
        )
    }

    if ($Entry.Operation) {
        $beforeMap = @{}
        foreach ($property in @(Get-SyncFactorsMonitorPropertyPairs -Value $Entry.Operation.before)) {
            $beforeMap[$property.Name] = ConvertTo-SyncFactorsMonitorInlineText -Value $property.Value
        }

        $afterMap = @{}
        foreach ($property in @(Get-SyncFactorsMonitorPropertyPairs -Value $Entry.Operation.after)) {
            $afterMap[$property.Name] = ConvertTo-SyncFactorsMonitorInlineText -Value $property.Value
        }

        $keys = @($beforeMap.Keys + $afterMap.Keys | Sort-Object -Unique)
        if ("$($Entry.Operation.operationType)" -eq 'MoveUser') {
            $keys = @($keys | Where-Object { $_ -notin @('distinguishedName', 'parentOu', 'targetOu') })
        }
        return @(
            foreach ($key in $keys) {
                $beforeValue = if ($beforeMap.ContainsKey($key)) { $beforeMap[$key] } else { '(unset)' }
                $afterValue = if ($afterMap.ContainsKey($key)) { $afterMap[$key] } else { '(unset)' }
                $marker = if ($beforeValue -eq '(unset)' -and $afterValue -ne '(unset)') {
                    '[CREATE]'
                } elseif ($beforeValue -ne '(unset)' -and $afterValue -eq '(unset)') {
                    '[DELETE]'
                } else {
                    '[UPDATE]'
                }

                [pscustomobject]@{
                    Marker = $marker
                    Attribute = $key
                    Before = $beforeValue
                    After = $afterValue
                    Source = ''
                }
            }
        )
    }

    return @()
}

function Get-SyncFactorsMonitorWorkerPreviewDiffRows {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$PreviewResult
    )

    if (-not $PreviewResult) {
        return @()
    }

    if ($PreviewResult.PSObject.Properties.Name -contains 'changedAttributes') {
        $rows = @(
            foreach ($row in @($PreviewResult.changedAttributes)) {
                $attribute = if ($row.PSObject.Properties.Name -contains 'targetAttribute') { "$($row.targetAttribute)" } else { '' }
                $beforeValue = if ($row.PSObject.Properties.Name -contains 'currentAdValue') {
                    ConvertTo-SyncFactorsMonitorInlineText -Value $row.currentAdValue
                } else {
                    '(unset)'
                }
                $afterValue = if ($row.PSObject.Properties.Name -contains 'proposedValue') {
                    ConvertTo-SyncFactorsMonitorInlineText -Value $row.proposedValue
                } else {
                    '(unset)'
                }

                if (
                    [string]::IsNullOrWhiteSpace($attribute) -or
                    $beforeValue -eq $afterValue
                ) {
                    continue
                }

                [pscustomobject]@{
                    Attribute = $attribute
                    Before = $beforeValue
                    After = $afterValue
                }
            }
        )

        if ($rows.Count -gt 0) {
            return @($rows)
        }
    }

    if ($PreviewResult.PSObject.Properties.Name -contains 'operations') {
        $results = [System.Collections.Generic.List[object]]::new()
        foreach ($operation in @($PreviewResult.operations)) {
            $beforeMap = @{}
            foreach ($property in @(Get-SyncFactorsMonitorPropertyPairs -Value $operation.before)) {
                $beforeMap[$property.Name] = ConvertTo-SyncFactorsMonitorInlineText -Value $property.Value
            }

            $afterMap = @{}
            foreach ($property in @(Get-SyncFactorsMonitorPropertyPairs -Value $operation.after)) {
                $afterMap[$property.Name] = ConvertTo-SyncFactorsMonitorInlineText -Value $property.Value
            }

            $keys = @($beforeMap.Keys + $afterMap.Keys | Sort-Object -Unique)
            if ("$($operation.operationType)" -eq 'MoveUser') {
                $keys = @($keys | Where-Object { $_ -notin @('distinguishedName', 'parentOu', 'targetOu') })
            }

            foreach ($key in $keys) {
                $beforeValue = if ($beforeMap.ContainsKey($key)) { $beforeMap[$key] } else { '(unset)' }
                $afterValue = if ($afterMap.ContainsKey($key)) { $afterMap[$key] } else { '(unset)' }
                if ($beforeValue -eq $afterValue) {
                    continue
                }

                $results.Add([pscustomobject]@{
                        Attribute = $key
                        Before = $beforeValue
                        After = $afterValue
                    })
            }
        }

        return @($results)
    }

    return @()
}

function Get-SyncFactorsMonitorWorkerSyncSummaryLines {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [pscustomobject]$SyncResult,
        [AllowNull()]
        [pscustomobject]$Report
    )

    if (-not $SyncResult -and -not $Report) {
        return @()
    }

    $workerId = if (
        $SyncResult -and
        $SyncResult.PSObject.Properties.Name -contains 'workerScope' -and
        $SyncResult.workerScope -and
        $SyncResult.workerScope.PSObject.Properties.Name -contains 'workerId'
    ) {
        "$($SyncResult.workerScope.workerId)"
    } elseif (
        $Report -and
        $Report.PSObject.Properties.Name -contains 'workerScope' -and
        $Report.workerScope -and
        $Report.workerScope.PSObject.Properties.Name -contains 'workerId'
    ) {
        "$($Report.workerScope.workerId)"
    } else {
        '-'
    }

    $reportPath = if ($SyncResult -and $SyncResult.PSObject.Properties.Name -contains 'reportPath') { "$($SyncResult.reportPath)" } else { '-' }
    $runId = if ($SyncResult -and $SyncResult.PSObject.Properties.Name -contains 'runId') { "$($SyncResult.runId)" } else { $(if ($Report) { "$($Report.runId)" } else { '-' }) }
    $status = if ($SyncResult -and $SyncResult.PSObject.Properties.Name -contains 'status') { "$($SyncResult.status)" } else { $(if ($Report) { "$($Report.status)" } else { '-' }) }
    $artifactType = if ($SyncResult -and $SyncResult.PSObject.Properties.Name -contains 'artifactType') { "$($SyncResult.artifactType)" } else { $(if ($Report) { "$($Report.artifactType)" } else { '-' }) }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('Single-worker sync completed.')
    $lines.Add("WorkerId=$workerId")
    $lines.Add("RunId=$runId")
    $lines.Add("Status=$status")
    $lines.Add("Artifact=$artifactType")
    $lines.Add("Report=$reportPath")

    if ($Report) {
        $bucketParts = [System.Collections.Generic.List[string]]::new()
        foreach ($bucketName in @('creates', 'updates', 'enables', 'disables', 'graveyardMoves', 'deletions')) {
            $bucketCount = if ($Report.PSObject.Properties.Name -contains $bucketName) { @($Report.$bucketName).Count } else { 0 }
            if ($bucketCount -gt 0) {
                $bucketParts.Add("$bucketName=$bucketCount")
            }
        }
        if ($bucketParts.Count -gt 0) {
            $lines.Add("Buckets: $($bucketParts -join ', ')")
        } else {
            $lines.Add('Buckets: none')
        }

        $operationLines = [System.Collections.Generic.List[string]]::new()
        foreach ($operation in @($Report.operations | Select-Object -First 6)) {
            $targetSam = if (
                $operation.PSObject.Properties.Name -contains 'target' -and
                $operation.target -and
                $operation.target.PSObject.Properties.Name -contains 'samAccountName' -and
                -not [string]::IsNullOrWhiteSpace("$($operation.target.samAccountName)")
            ) {
                "$($operation.target.samAccountName)"
            } else {
                '-'
            }

            $operationLines.Add("$($operation.operationType) ($targetSam)")
        }
        if ($operationLines.Count -gt 0) {
            $lines.Add('Operations:')
            foreach ($line in $operationLines) {
                $lines.Add("- $line")
            }
        }
    }

    return @($lines)
}

function Format-SyncFactorsMonitorWorkerPreviewFlowView {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $previewResult = $UiState.workerPreviewResult
    $diffRows = @(if (@($UiState.workerPreviewDiffRows).Count -gt 0) { $UiState.workerPreviewDiffRows } else { Get-SyncFactorsMonitorWorkerPreviewDiffRows -PreviewResult $previewResult })
    $panelWidth = 110
    $topBorder = "╔" + ("═" * ($panelWidth - 2)) + "╗"
    $midBorder = "╠" + ("═" * ($panelWidth - 2)) + "╣"
    $bottomBorder = "╚" + ("═" * ($panelWidth - 2)) + "╝"
    $rule = "─" * $panelWidth
    $lines = [System.Collections.Generic.List[string]]::new()

    $preview = if ($previewResult -and $previewResult.PSObject.Properties.Name -contains 'preview') { $previewResult.preview } else { $null }
    $workerId = if (
        $previewResult -and
        $previewResult.PSObject.Properties.Name -contains 'workerScope' -and
        $previewResult.workerScope -and
        $previewResult.workerScope.PSObject.Properties.Name -contains 'workerId'
    ) {
        "$($previewResult.workerScope.workerId)"
    } else {
        '-'
    }

    $lines.Add($topBorder)
    $lines.Add("║ Single-Worker Diff Review    Worker: $workerId    Run: $(if ($previewResult -and $previewResult.runId) { $previewResult.runId } else { 'no-run' })")
    $lines.Add("║ Report: $(if ($previewResult -and $previewResult.reportPath) { $previewResult.reportPath } else { '(none)' })")
    $lines.Add("║ Matched AD user: $(if ($preview -and $preview.PSObject.Properties.Name -contains 'matchedExistingUser' -and $null -ne $preview.matchedExistingUser) { $preview.matchedExistingUser } else { '-' })    SamAccountName: $(if ($preview -and $preview.PSObject.Properties.Name -contains 'samAccountName' -and -not [string]::IsNullOrWhiteSpace("$($preview.samAccountName)")) { $preview.samAccountName } else { '-' })")
    $lines.Add("║ Review category: $(if ($preview -and $preview.PSObject.Properties.Name -contains 'reviewCategory' -and -not [string]::IsNullOrWhiteSpace("$($preview.reviewCategory)")) { $preview.reviewCategory } else { '-' })    Reason: $(if ($preview -and $preview.PSObject.Properties.Name -contains 'reason' -and -not [string]::IsNullOrWhiteSpace("$($preview.reason)")) { $preview.reason } else { '-' })")
    $lines.Add("║ Target OU: $(if ($preview -and $preview.PSObject.Properties.Name -contains 'targetOu' -and -not [string]::IsNullOrWhiteSpace("$($preview.targetOu)")) { $preview.targetOu } else { '-' })")
    $lines.Add($midBorder)
    $lines.Add('▓ Attribute Diff')
    $lines.Add((' {0,-28} {1,-36} {2,-36}' -f 'Attribute', 'Old Value', 'New Value'))
    if ($diffRows.Count -eq 0) {
        $lines.Add(' No attribute changes were detected for this worker preview.')
    } else {
        foreach ($row in $diffRows | Select-Object -First 12) {
            $lines.Add((' {0,-28} {1,-36} {2,-36}' -f `
                    "$($row.Attribute)", `
                    "$($row.Before)", `
                    "$($row.After)"))
        }
        if ($diffRows.Count -gt 12) {
            $lines.Add(" ... $($diffRows.Count - 12) more changed attributes")
        }
    }
    $lines.Add($rule)
    $lines.Add('▓ Actions')
    $lines.Add('Press a to apply this worker sync.')
    $lines.Add('Press o to open the full report explorer for this preview.')
    $lines.Add('Press Esc to return to the dashboard without applying.')
    $lines.Add($midBorder)
    $lines.Add("║ Status: $($UiState.statusMessage)")
    $lines.Add('║ Keys: a apply worker sync, o open preview report, Esc dashboard')
    $lines.Add($bottomBorder)
    return $lines
}

function Format-SyncFactorsMonitorReportExplorerView {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $selectedRun = Get-SyncFactorsMonitorSelectedRun -Status $Status -UiState $UiState
    $selection = Get-SyncFactorsMonitorReportExplorerSelection -Status $Status -UiState $UiState
    $selectionEntries = @($selection.Entries)
    $selectionCategoryEntries = @($selection.CategoryEntries)
    $selectionCategories = @($selection.Categories)
    $diffRows = @(Get-SyncFactorsMonitorReportExplorerDiffRows -Entry $selection.SelectedEntry)
    $lines = [System.Collections.Generic.List[string]]::new()
    $panelWidth = 110
    $topBorder = "╔" + ("═" * ($panelWidth - 2)) + "╗"
    $midBorder = "╠" + ("═" * ($panelWidth - 2)) + "╣"
    $bottomBorder = "╚" + ("═" * ($panelWidth - 2)) + "╝"
    $rule = "─" * $panelWidth

    $changedCount = @($selectionEntries | Where-Object { $_.Category -eq 'Changed' }).Count
    $createdCount = @($selectionEntries | Where-Object { $_.Category -eq 'Created' }).Count
    $deletedCount = @($selectionEntries | Where-Object { $_.Category -eq 'Deleted' }).Count
    $selectedCategoryLabel = if ($selection.SelectedCategory) { $selection.SelectedCategory.Label } else { 'None' }
    $selectedEntryPosition = [math]::Min($selection.SelectedEntryIndex + 1, [math]::Max($selectionCategoryEntries.Count, 1))

    $lines.Add($topBorder)
    $lines.Add("║ Report Explorer    Run: $(if ($selectedRun.runId) { $selectedRun.runId } else { 'no-run' })    Category: $selectedCategoryLabel    Entry: $selectedEntryPosition/$([math]::Max($selectionCategoryEntries.Count, 1))")
    $lines.Add("║ Summary: [UPDATE] Changed=$changedCount    [CREATE] Created=$createdCount    [DELETE] Deleted=$deletedCount")
    $lines.Add("║ Report: $(if ($selectedRun.path) { $selectedRun.path } else { '(none)' })")
    $lines.Add($midBorder)
    $lines.Add('▓ Categories')
    foreach ($category in $selectionCategories) {
        $isSelected = $selection.SelectedCategory -and $selection.SelectedCategory.Name -eq $category.Name
        $count = @($selectionEntries | Where-Object { $_.Category -eq $category.Name }).Count
        $prefix = if ($isSelected) { ' > ' } else { '   ' }
        $lines.Add("$prefix$($category.Marker) $($category.Label) ($count)")
    }
    $lines.Add($rule)
    $lines.Add('▓ Objects')
    $lines.Add(' Sel Marker      WorkerId     SamAccountName        Bucket          Chg  Reason/Category')
    if ($selectionCategoryEntries.Count -eq 0) {
        $lines.Add('  -  No objects in the selected category.')
    } else {
        for ($i = 0; $i -lt [math]::Min($selectionCategoryEntries.Count, 6); $i += 1) {
            $entry = $selectionCategoryEntries[$i]
            $marker = if ($selection.SelectedEntry -and $entry -eq $selection.SelectedEntry) { ' > ' } else { '   ' }
            $rowMarker = switch ($entry.Category) {
                'Created' { '[CREATE]' }
                'Deleted' { '[DELETE]' }
                default { '[UPDATE]' }
            }
            $reasonText = if (-not [string]::IsNullOrWhiteSpace($entry.Reason)) { $entry.Reason } elseif (-not [string]::IsNullOrWhiteSpace($entry.ReviewCategory)) { $entry.ReviewCategory } else { '-' }
            $lines.Add(("{0}{1,-11} {2,-12} {3,-21} {4,-14} {5,3}  {6}" -f `
                    $marker, `
                    $rowMarker, `
                    $(if ($entry.WorkerId) { $entry.WorkerId } else { '-' }), `
                    $(if ($entry.SamAccountName) { $entry.SamAccountName } else { '-' }), `
                    $entry.BucketLabel, `
                    $entry.ChangeCount, `
                    $reasonText))
        }

        if ($selectionCategoryEntries.Count -gt 6) {
            $lines.Add("... $($selectionCategoryEntries.Count - 6) more objects")
        }
    }
    $lines.Add($rule)
    $lines.Add('▓ Selected Object')
    if (-not $selection.SelectedEntry) {
        $lines.Add('No object is selected.')
    } else {
        $entry = $selection.SelectedEntry
        $entryMarker = switch ($entry.Category) {
            'Created' { '[CREATE]' }
            'Deleted' { '[DELETE]' }
            default { '[UPDATE]' }
        }
        $lines.Add("$entryMarker workerId=$(if ($entry.WorkerId) { $entry.WorkerId } else { '-' })    samAccountName=$(if ($entry.SamAccountName) { $entry.SamAccountName } else { '-' })    action=$($entry.BucketLabel)")
        if (-not [string]::IsNullOrWhiteSpace($entry.TargetOu)) {
            $lines.Add("Target OU: $($entry.TargetOu)")
        }
        if ($entry.Operation) {
            $lines.Add("Operation: $($entry.Operation.operationType)")
        }
        foreach ($summaryLine in @(Get-SyncFactorsMonitorOperationSummaryLines -Item $entry.Item -Operation $entry.Operation)) {
            $lines.Add($summaryLine)
        }
        if ($diffRows.Count -eq 0) {
            $lines.Add('No attribute-level changes were recorded for this object.')
        } else {
            foreach ($row in $diffRows | Select-Object -First 8) {
                $sourceText = if ([string]::IsNullOrWhiteSpace($row.Source)) { '' } else { " [$($row.Source)]" }
                $lines.Add(("{0} {1}{2}: {3} -> {4}" -f $row.Marker, $row.Attribute, $sourceText, $row.Before, $row.After))
            }
            if ($diffRows.Count -gt 8) {
                $lines.Add("... $($diffRows.Count - 8) more attributes")
            }
        }
    }
    $lines.Add($midBorder)
    $lines.Add("║ Status: $($UiState.statusMessage)")
    $lines.Add('║ Keys: q quit, r refresh, j/k move object, [ ] move category, o or Esc close explorer, y copy path')
    $lines.Add($bottomBorder)
    return $lines
}

function Format-SyncFactorsMonitorSelectedObjectLines {
    [CmdletBinding()]
    param(
        $SelectedItem,
        $SelectedOperation
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    if ($SelectedItem) {
        $summaryParts = [System.Collections.Generic.List[string]]::new()
        foreach ($key in @('workerId','samAccountName','userPrincipalName','reason','threshold','targetOu','reviewCaseType')) {
            if ($SelectedItem.PSObject.Properties.Name -contains $key -and -not [string]::IsNullOrWhiteSpace("$(($SelectedItem.$key))")) {
                $summaryParts.Add("$key=$($SelectedItem.$key)")
            }
        }

        if ($summaryParts.Count -eq 0) {
            $summaryParts.Add((ConvertTo-SyncFactorsMonitorInlineText -Value $SelectedItem))
        }

        $lines.Add("Item: $($summaryParts -join '    ')")
    } else {
        $lines.Add('No object is selected.')
    }

    if (-not $SelectedOperation) {
        $lines.Add('Operation: no matching reversible operation recorded for the selected object.')
    } else {
        $lines.Add("Operation: $($SelectedOperation.operationType)    Target: $(ConvertTo-SyncFactorsMonitorInlineText -Value $SelectedOperation.target)")
        foreach ($summaryLine in @(Get-SyncFactorsMonitorOperationSummaryLines -Item $SelectedItem -Operation $SelectedOperation)) {
            $lines.Add($summaryLine)
        }
        $diffLines = @(Get-SyncFactorsMonitorOperationDiffLines -Operation $SelectedOperation)
        if ("$($SelectedOperation.operationType)" -eq 'MoveUser') {
            $diffLines = @($diffLines | Where-Object { $_ -notmatch '^(distinguishedName|parentOu|targetOu): ' })
        }
        if ($diffLines.Count -eq 0) {
            if ($null -ne $SelectedOperation.after) {
                $lines.Add("After: $(ConvertTo-SyncFactorsMonitorInlineText -Value $SelectedOperation.after)")
            } elseif ($null -ne $SelectedOperation.before) {
                $lines.Add("Before: $(ConvertTo-SyncFactorsMonitorInlineText -Value $SelectedOperation.before)")
            }
        } else {
            foreach ($line in $diffLines | Select-Object -First 6) {
                $lines.Add("Δ $line")
            }

            if ($diffLines.Count -gt 6) {
                $lines.Add("... $($diffLines.Count - 6) more changes")
            }
        }
    }

    if ($SelectedItem -and $SelectedItem.PSObject.Properties.Name -contains 'changedAttributeDetails') {
        $detailRows = @($SelectedItem.changedAttributeDetails)
        foreach ($row in $detailRows | Select-Object -First 6) {
            $lines.Add("Map: $($row.sourceField) -> $($row.targetAttribute) [$($row.transform)]")
            $lines.Add("     $($row.currentAdValue) -> $($row.proposedValue)")
        }
        if ($detailRows.Count -gt 6) {
            $lines.Add("... $($detailRows.Count - 6) more mapped changes")
        }
    } elseif ($SelectedItem -and $SelectedItem.PSObject.Properties.Name -contains 'attributeRows') {
        $changedRows = @($SelectedItem.attributeRows | Where-Object { $_.changed })
        foreach ($row in $changedRows | Select-Object -First 4) {
            $lines.Add("Map: $($row.sourceField) -> $($row.targetAttribute) [$($row.transform)]")
            $lines.Add("     $($row.currentAdValue) -> $($row.proposedValue)")
        }
    }

    foreach ($operatorLine in @(Get-SyncFactorsMonitorOperatorActionLines -Item $SelectedItem)) {
        $lines.Add($operatorLine)
    }

    return $lines
}

function Get-SyncFactorsMonitorRunDelta {
    [CmdletBinding()]
    param(
        [pscustomobject]$ReferenceRun,
        [pscustomobject]$ComparisonRun
    )

    if (-not $ReferenceRun -or -not $ComparisonRun) {
        return $null
    }

    return [pscustomobject]@{
        creates = [int]$ReferenceRun.creates - [int]$ComparisonRun.creates
        updates = [int]$ReferenceRun.updates - [int]$ComparisonRun.updates
        disables = [int]$ReferenceRun.disables - [int]$ComparisonRun.disables
        deletions = [int]$ReferenceRun.deletions - [int]$ComparisonRun.deletions
        quarantined = [int]$ReferenceRun.quarantined - [int]$ComparisonRun.quarantined
        conflicts = [int]$ReferenceRun.conflicts - [int]$ComparisonRun.conflicts
        guardrailFailures = [int]$ReferenceRun.guardrailFailures - [int]$ComparisonRun.guardrailFailures
    }
}

function Format-SyncFactorsMonitorDashboardView {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    if ($UiState.PSObject.Properties.Name -contains 'viewMode' -and "$($UiState.viewMode)" -eq 'ReportExplorer') {
        return @(Format-SyncFactorsMonitorReportExplorerView -Status $Status -UiState $UiState)
    }

    if ($UiState.PSObject.Properties.Name -contains 'viewMode' -and "$($UiState.viewMode)" -eq 'WorkerPreviewDiff') {
        return @(Format-SyncFactorsMonitorWorkerPreviewFlowView -Status $Status -UiState $UiState)
    }

    $selectedRun = Get-SyncFactorsMonitorSelectedRun -Status $Status -UiState $UiState
    $selectedBucket = Get-SyncFactorsMonitorSelectedBucket -Status $Status -UiState $UiState
    $filteredItems = @(Get-SyncFactorsMonitorFilteredBucketItems -BucketSelection $selectedBucket -UiState $UiState)
    $selectedItem = Get-SyncFactorsMonitorSelectedBucketItem -BucketSelection $selectedBucket -UiState $UiState
    $selectedOperation = Get-SyncFactorsMonitorSelectedBucketOperation -Status $Status -UiState $UiState
    $selectedWorkerState = Get-SyncFactorsMonitorSelectedWorkerState -Status $Status -UiState $UiState
    $failureGroups = @(Get-SyncFactorsMonitorFailureGroups -BucketSelection $selectedBucket -UiState $UiState)
    $diagnostics = Get-SyncFactorsMonitorCurrentRunDiagnostics -CurrentRun $Status.currentRun
    $comparisonRun = $null
    if (@($Status.recentRuns).Count -gt ([int]$UiState.selectedRunIndex + 1)) {
        $comparisonRun = @($Status.recentRuns)[[int]$UiState.selectedRunIndex + 1]
    }
    $runDelta = Get-SyncFactorsMonitorRunDelta -ReferenceRun $selectedRun -ComparisonRun $comparisonRun
    $isReviewRun = Test-SyncFactorsMonitorSelectedRunIsReview -Status $Status -UiState $UiState
    $lines = [System.Collections.Generic.List[string]]::new()
    $panelWidth = 110
    $recentRunRowLimit = 5
    $detailRowLimit = 4
    $topBorder = "╔" + ("═" * ($panelWidth - 2)) + "╗"
    $midBorder = "╠" + ("═" * ($panelWidth - 2)) + "╣"
    $bottomBorder = "╚" + ("═" * ($panelWidth - 2)) + "╝"
    $rule = "─" * $panelWidth

    $authSummary = if (
        $Status.PSObject.Properties.Name -contains 'context' -and
        $Status.context -and
        $Status.context.PSObject.Properties.Name -contains 'successFactorsAuth' -and
        -not [string]::IsNullOrWhiteSpace("$($Status.context.successFactorsAuth)")
    ) { "$($Status.context.successFactorsAuth)" } else { '-' }
    $identityField = if (
        $Status.PSObject.Properties.Name -contains 'context' -and
        $Status.context -and
        $Status.context.PSObject.Properties.Name -contains 'identityField' -and
        -not [string]::IsNullOrWhiteSpace("$($Status.context.identityField)")
    ) { "$($Status.context.identityField)" } else { '-' }
    $identityAttribute = if (
        $Status.PSObject.Properties.Name -contains 'context' -and
        $Status.context -and
        $Status.context.PSObject.Properties.Name -contains 'identityAttribute' -and
        -not [string]::IsNullOrWhiteSpace("$($Status.context.identityAttribute)")
    ) { "$($Status.context.identityAttribute)" } else { '-' }
    $successFactorsHealth = if (
        $Status.PSObject.Properties.Name -contains 'health' -and
        $Status.health -and
        $Status.health.PSObject.Properties.Name -contains 'successFactors' -and
        $Status.health.successFactors
    ) { Format-SyncFactorsMonitorHealthSummary -Health $Status.health.successFactors } else { '-' }
    $activeDirectoryHealth = if (
        $Status.PSObject.Properties.Name -contains 'health' -and
        $Status.health -and
        $Status.health.PSObject.Properties.Name -contains 'activeDirectory' -and
        $Status.health.activeDirectory
    ) { Format-SyncFactorsMonitorHealthSummary -Health $Status.health.activeDirectory } else { '-' }
    $latestState = if ($Status.latestRun.status -eq 'Failed' -or $Status.currentRun.errorMessage) { 'ERROR' } elseif ($Status.currentRun.status -eq 'InProgress') { 'ACTIVE' } else { 'OK' }
    $selectedRunPosition = [math]::Min([int]$UiState.selectedRunIndex + 1, [math]::Max(@($Status.recentRuns).Count, 1))
    $selectedItemPosition = [math]::Min([int]$UiState.selectedItemIndex + 1, [math]::Max($filteredItems.Count, 1))
    $isCurrentRunActive = "$($Status.currentRun.status)" -eq 'InProgress'
    $lines.Add($topBorder)
    $lines.Add("║ SuccessFactors AD Sync Dashboard [$latestState]    AutoRefresh: $(if ($UiState.autoRefreshEnabled) { 'On' } else { 'Paused' })    Refreshed: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))")
    $lines.Add("║ Health: SF=$successFactorsHealth    AD=$activeDirectoryHealth    Run: $selectedRunPosition/$([math]::Max(@($Status.recentRuns).Count, 1))    Bucket: $($selectedBucket.Bucket.Label)")
    $lines.Add("║ Filter: $(if ([string]::IsNullOrWhiteSpace($UiState.filterText)) { '(none)' } else { $UiState.filterText })    Match: $($filteredItems.Count)/$(@($selectedBucket.Items).Count)    Item: $selectedItemPosition/$([math]::Max($filteredItems.Count, 1))")
    $lines.Add("║ Config: SF Auth=$authSummary    Identity=$identityField -> AD $identityAttribute")
    $lines.Add($midBorder)
    $lines.Add('▓ Current Run')
    $lines.Add("Status: $($Status.currentRun.status)    Stage: $($Status.currentRun.stage)    Mode: $($Status.currentRun.mode)    DryRun: $($Status.currentRun.dryRun)")
    $lines.Add("Progress: $($Status.currentRun.processedWorkers) / $($Status.currentRun.totalWorkers)    Worker: $($Status.currentRun.currentWorkerId)    Last action: $($Status.currentRun.lastAction)")
    if ($Status.currentRun.errorMessage) {
        $lines.Add("Error: $($Status.currentRun.errorMessage)")
    }
    if ($isCurrentRunActive) {
        $lines.Add("Diagnostics: Elapsed=$(if ($null -ne $diagnostics.elapsedSeconds) { $diagnostics.elapsedSeconds } else { '-' })s    RefreshLag=$(if ($null -ne $diagnostics.refreshLagSeconds) { $diagnostics.refreshLagSeconds } else { '-' })s    Throughput=$(if ($null -ne $diagnostics.throughput) { $diagnostics.throughput } else { '-' })/s    ETA=$(if ($null -ne $diagnostics.etaSeconds) { $diagnostics.etaSeconds } else { '-' })s")
    }
    $lines.Add($rule)
    $selectedRunDuration = if ($selectedRun.PSObject.Properties.Name -contains 'durationSeconds') { $selectedRun.durationSeconds } else { '-' }
    $selectedRunCreates = if ($selectedRun.PSObject.Properties.Name -contains 'creates') { $selectedRun.creates } else { 0 }
    $selectedRunUpdates = if ($selectedRun.PSObject.Properties.Name -contains 'updates') { $selectedRun.updates } else { 0 }
    $selectedRunDisables = if ($selectedRun.PSObject.Properties.Name -contains 'disables') { $selectedRun.disables } else { 0 }
    $selectedRunDeletions = if ($selectedRun.PSObject.Properties.Name -contains 'deletions') { $selectedRun.deletions } else { 0 }
    $selectedRunQuarantined = if ($selectedRun.PSObject.Properties.Name -contains 'quarantined') { $selectedRun.quarantined } else { 0 }
    $selectedRunConflicts = if ($selectedRun.PSObject.Properties.Name -contains 'conflicts') { $selectedRun.conflicts } else { 0 }
    $selectedRunGuardrailFailures = if ($selectedRun.PSObject.Properties.Name -contains 'guardrailFailures') { $selectedRun.guardrailFailures } else { 0 }
    $selectedRunManualReview = if ($selectedRun.PSObject.Properties.Name -contains 'manualReview') { $selectedRun.manualReview } else { 0 }
    $summaryTitle = if ($selectedRun.PSObject.Properties.Name -contains 'artifactType' -and "$($selectedRun.artifactType)" -eq 'WorkerPreview') {
        '▓ Worker Preview Summary'
    } elseif ($selectedRun.PSObject.Properties.Name -contains 'artifactType' -and "$($selectedRun.artifactType)" -eq 'WorkerSync') {
        '▓ Single Worker Sync Summary'
    } elseif ($isReviewRun) {
        '▓ First Sync Review Summary'
    } else {
        '▓ Latest Run Summary'
    }
    $lines.Add($summaryTitle)
    $lines.Add("Status: $($selectedRun.status)    Mode: $($selectedRun.mode)    DryRun: $($selectedRun.dryRun)    Started: $($selectedRun.startedAt)    Dur(s): $selectedRunDuration")
    $lines.Add("Totals: C=$selectedRunCreates U=$selectedRunUpdates D=$selectedRunDisables X=$selectedRunDeletions Q=$selectedRunQuarantined F=$selectedRunConflicts GF=$selectedRunGuardrailFailures MR=$selectedRunManualReview")
    if ($isReviewRun -and $selectedRun.PSObject.Properties.Name -contains 'reviewSummary' -and $selectedRun.reviewSummary) {
        $lines.Add("Review: existing=$($selectedRun.reviewSummary.existingUsersMatched) changed=$($selectedRun.reviewSummary.existingUsersWithAttributeChanges) aligned=$($selectedRun.reviewSummary.existingUsersWithoutAttributeChanges) creates=$($selectedRun.reviewSummary.proposedCreates) offboarding=$($selectedRun.reviewSummary.proposedOffboarding)")
        if ($selectedRun.reviewSummary.PSObject.Properties.Name -contains 'operatorActionCases' -and $selectedRun.reviewSummary.operatorActionCases) {
            $lines.Add("Manual review cases: quarantined=$($selectedRun.reviewSummary.operatorActionCases.quarantinedWorkers) unresolvedManagers=$($selectedRun.reviewSummary.operatorActionCases.unresolvedManagers) rehires=$($selectedRun.reviewSummary.operatorActionCases.rehireCases)")
        }
    }
    if ($selectedRun.PSObject.Properties.Name -contains 'workerScope' -and $selectedRun.workerScope -and $selectedRun.workerScope.PSObject.Properties.Name -contains 'workerId') {
        $lines.Add("Worker scope: $($selectedRun.workerScope.workerId)")
    }
    if ($comparisonRun -and $runDelta) {
        $lines.Add("Compared to older run $($comparisonRun.runId): ΔC=$($runDelta.creates) ΔU=$($runDelta.updates) ΔD=$($runDelta.disables) ΔX=$($runDelta.deletions) ΔQ=$($runDelta.quarantined) ΔF=$($runDelta.conflicts) ΔGF=$($runDelta.guardrailFailures)")
    }
    $lines.Add($rule)
    $lines.Add('▓ State Summary')
    $lines.Add("Checkpoint: $($Status.summary.lastCheckpoint)    Tracked: $($Status.summary.totalTrackedWorkers)    Suppressed: $($Status.summary.suppressedWorkers)    Pending deletion: $($Status.summary.pendingDeletionWorkers)")
    $lines.Add($rule)
    $lines.Add('▓ Recent Runs')
    $lines.Add(' Sel Status     Mode  Dry  Started             Dur(s)     C     U     D     X   Q/F')
    $runs = @($Status.recentRuns)
    if ($runs.Count -eq 0) {
        $lines.Add('  -  No sync reports found.')
    } else {
        for ($i = 0; $i -lt [math]::Min($runs.Count, $recentRunRowLimit); $i += 1) {
            $run = $runs[$i]
            $marker = if ($i -eq [math]::Min([math]::Max([int]$UiState.selectedRunIndex, 0), $runs.Count - 1)) { ' > ' } else { '   ' }
            $runCreates = if ($run.PSObject.Properties.Name -contains 'creates') { $run.creates } else { 0 }
            $runUpdates = if ($run.PSObject.Properties.Name -contains 'updates') { $run.updates } else { 0 }
            $runDisables = if ($run.PSObject.Properties.Name -contains 'disables') { $run.disables } else { 0 }
            $runDeletions = if ($run.PSObject.Properties.Name -contains 'deletions') { $run.deletions } else { 0 }
            $runQuarantined = if ($run.PSObject.Properties.Name -contains 'quarantined') { $run.quarantined } else { 0 }
            $runConflicts = if ($run.PSObject.Properties.Name -contains 'conflicts') { $run.conflicts } else { 0 }
            $lines.Add(("{0}{1,-10} {2,-5} {3,-4} {4,-19} {5,6} {6,5} {7,5} {8,5} {9,5} {10,6}/{11,-2}" -f `
                    $marker, `
                    $(if ($run.status) { $run.status } else { '-' }), `
                    $(if ($run.mode) { $run.mode } else { '-' }), `
                    $(if ($run.dryRun) { 'yes' } else { 'no' }), `
                    $(if ($run.startedAt) { $run.startedAt } else { '-' }), `
                    $(if ($null -ne $run.durationSeconds) { $run.durationSeconds } else { '-' }), `
                    $runCreates, `
                    $runUpdates, `
                    $runDisables, `
                    $runDeletions, `
                    $runQuarantined, `
                    $runConflicts, `
                    $run.guardrailFailures))
        }
        if ($runs.Count -gt $recentRunRowLimit) {
            $lines.Add("... $($runs.Count - $recentRunRowLimit) older runs")
        }
    }

    $lines.Add($rule)
    $lines.Add("▓ Detail: $($selectedBucket.Bucket.Label) for $(if ($selectedRun.runId) { $selectedRun.runId } else { 'no-run' })")
    if ($failureGroups.Count -gt 0 -and @('quarantined','conflicts','manualReview','guardrailFailures') -contains $selectedBucket.Bucket.Name) {
        $groupText = @($failureGroups | Select-Object -First 4 | ForEach-Object { "$($_.label)=$($_.count)" }) -join '    '
        $lines.Add("Reason groups: $groupText")
    }
    if (@($selectedBucket.Items).Count -eq 0) {
        $lines.Add('No entries in the selected bucket.')
    } elseif ($filteredItems.Count -eq 0) {
        $lines.Add('No entries match the active filter.')
    } else {
        for ($i = 0; $i -lt [math]::Min($filteredItems.Count, $detailRowLimit); $i += 1) {
            $prefix = if ($selectedItem -and $filteredItems[$i] -eq $selectedItem) { '>' } else { '-' }
            $lines.Add("$prefix $(ConvertTo-SyncFactorsMonitorInlineText -Value $filteredItems[$i])")
        }

        if ($filteredItems.Count -gt $detailRowLimit) {
            $lines.Add("... $($filteredItems.Count - $detailRowLimit) more")
        }
    }

    $lines.Add($rule)
    $lines.Add('▓ Selected Object')
    foreach ($line in @(Format-SyncFactorsMonitorSelectedObjectLines -SelectedItem $selectedItem -SelectedOperation $selectedOperation)) {
        $lines.Add($line)
    }
    if ($selectedWorkerState) {
        $lines.Add("Tracked: $(ConvertTo-SyncFactorsMonitorInlineText -Value $selectedWorkerState)")
    } else {
        $stateMatches = @(Get-SyncFactorsMonitorFilteredTrackedWorkers -Status $Status -UiState $UiState)
        if ($stateMatches.Count -eq 0) {
            $lines.Add('No tracked worker state matches the current context.')
        } else {
            foreach ($stateMatch in $stateMatches | Select-Object -First 4) {
                $lines.Add("- $(ConvertTo-SyncFactorsMonitorInlineText -Value $stateMatch)")
            }
        }
    }

    if (@($UiState.commandOutput).Count -gt 0) {
        $lines.Add($rule)
        $lines.Add('▓ Command Output')
        foreach ($line in @($UiState.commandOutput) | Select-Object -First 6) {
            $lines.Add($line)
        }
    }

    if ($UiState.pendingAction -eq 'ApplyWorkerSync') {
        $lines.Add($rule)
        $lines.Add('▓ Worker Review Actions')
        $lines.Add("Selected worker: $($UiState.pendingWorkerId)")
        $lines.Add('Press a to write the reviewed changes to AD.')
        $lines.Add('Press o to choose a related worker report.')
        $lines.Add('Press Esc to cancel and return to the review screen.')
    } elseif ($UiState.pendingAction -eq 'WorkerReportPicker') {
        $lines.Add($rule)
        $lines.Add('▓ Worker Report Picker')
        $lines.Add("Selected worker: $($UiState.pendingWorkerId)")
        $relatedRuns = @(Get-SyncFactorsMonitorWorkerRelatedRuns -Status $Status -WorkerId $UiState.pendingWorkerId)
        if ($relatedRuns.Count -eq 0) {
            $lines.Add('No related one-worker reports were found.')
        } else {
            for ($i = 0; $i -lt [math]::Min($relatedRuns.Count, 6); $i += 1) {
                $prefix = if ($i -eq [math]::Min([math]::Max([int]$UiState.pendingReportIndex, 0), $relatedRuns.Count - 1)) { ' > ' } else { '   ' }
                $lines.Add("$prefix$($relatedRuns[$i].Label)")
            }
            if ($relatedRuns.Count -gt 6) {
                $lines.Add("... $($relatedRuns.Count - 6) more reports")
            }
        }
        $lines.Add('Press Enter or o to open the selected report.')
        $lines.Add('Press j/k to move through related reports.')
        $lines.Add('Press Esc to return to worker actions.')
    } elseif (Test-SyncFactorsMonitorSelectedRunIsWorkerPreview -Status $Status -UiState $UiState) {
        $workerPreviewWorkerId = Get-SyncFactorsMonitorSelectedRunWorkerId -Status $Status -UiState $UiState
        if (-not [string]::IsNullOrWhiteSpace($workerPreviewWorkerId)) {
            $lines.Add($rule)
            $lines.Add('▓ Worker Review Actions')
            $lines.Add("Single-worker review ready for $workerPreviewWorkerId.")
            $lines.Add('Press g to choose whether to apply this worker to AD or open the review report.')
        }
    }

    $lines.Add($midBorder)
    $lines.Add("║ Status: $($UiState.statusMessage)")
    $lines.Add('║ Keys: q quit, r refresh, t auto-refresh, tab focus, j/k run, [ ] bucket, h/l item, / filter, c clear, enter inspect')
    $lines.Add('║ Runs: p preflight, d delta dry-run, s delta sync, f full dry-run, a full sync, w worker preview, v review, g worker apply, z fresh reset, o open report explorer, y copy path, x export bucket')
    $lines.Add($bottomBorder)
    return $lines
}

Export-ModuleMember -Function Get-SyncFactorsRuntimeStatusPath, New-SyncFactorsIdleRuntimeStatus, New-SyncFactorsRuntimeStatusSnapshot, Save-SyncFactorsRuntimeStatusSnapshot, Write-SyncFactorsRuntimeStatusSnapshot, Get-SyncFactorsRuntimeStatusSnapshot, Get-SyncFactorsRecentRunSummaries, Get-SyncFactorsMonitorStatus, Format-SyncFactorsMonitorView, New-SyncFactorsMonitorUiState, Get-SyncFactorsMonitorBucketDefinitions, Get-SyncFactorsMonitorSelectedRun, Get-SyncFactorsMonitorSelectedRunReport, Get-SyncFactorsMonitorSelectedBucket, Resolve-SyncFactorsMonitorMappingConfigPath, Resolve-SyncFactorsMonitorSelectedReportPath, Get-SyncFactorsMonitorActionContext, Format-SyncFactorsMonitorDashboardView, Get-SyncFactorsMonitorFilteredBucketItems, Get-SyncFactorsMonitorSelectedBucketItem, Get-SyncFactorsMonitorSelectedBucketOperation, Get-SyncFactorsMonitorFailureGroups, Get-SyncFactorsMonitorSelectedWorkerState, Get-SyncFactorsMonitorCurrentRunDiagnostics, Get-SyncFactorsMonitorOperationDiffLines, Format-SyncFactorsMonitorSelectedObjectLines, Get-SyncFactorsReportDirectories, Test-SyncFactorsMonitorSelectedRunIsReview, Test-SyncFactorsMonitorSelectedRunIsWorkerPreview, Get-SyncFactorsMonitorSelectedRunWorkerId, Get-SyncFactorsMonitorWorkerRelatedRuns, Get-SyncFactorsMonitorReportExplorerCategoryDefinitions, Get-SyncFactorsMonitorReportExplorerEntries, Get-SyncFactorsMonitorReportExplorerSelection, Get-SyncFactorsMonitorReportExplorerDiffRows, Format-SyncFactorsMonitorReportExplorerView, Get-SyncFactorsMonitorWorkerPreviewDiffRows, Get-SyncFactorsMonitorWorkerSyncSummaryLines, Format-SyncFactorsMonitorWorkerPreviewFlowView
