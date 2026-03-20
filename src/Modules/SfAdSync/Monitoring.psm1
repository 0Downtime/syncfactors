Set-StrictMode -Version Latest

function Get-SfAdCollectionCount {
    [CmdletBinding()]
    param($Value)

    if ($null -eq $Value) {
        return 0
    }

    return @($Value).Count
}

function Get-SfAdRuntimeStatusPath {
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

function New-SfAdIdleRuntimeStatus {
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
        runtimeStatusPath = Get-SfAdRuntimeStatusPath -StatePath $StatePath
    }
}

function New-SfAdRuntimeStatusSnapshot {
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
        creates = if ($Report.Contains('creates')) { Get-SfAdCollectionCount -Value $Report['creates'] } else { 0 }
        updates = if ($Report.Contains('updates')) { Get-SfAdCollectionCount -Value $Report['updates'] } else { 0 }
        enables = if ($Report.Contains('enables')) { Get-SfAdCollectionCount -Value $Report['enables'] } else { 0 }
        disables = if ($Report.Contains('disables')) { Get-SfAdCollectionCount -Value $Report['disables'] } else { 0 }
        graveyardMoves = if ($Report.Contains('graveyardMoves')) { Get-SfAdCollectionCount -Value $Report['graveyardMoves'] } else { 0 }
        deletions = if ($Report.Contains('deletions')) { Get-SfAdCollectionCount -Value $Report['deletions'] } else { 0 }
        quarantined = if ($Report.Contains('quarantined')) { Get-SfAdCollectionCount -Value $Report['quarantined'] } else { 0 }
        conflicts = if ($Report.Contains('conflicts')) { Get-SfAdCollectionCount -Value $Report['conflicts'] } else { 0 }
        guardrailFailures = if ($Report.Contains('guardrailFailures')) { Get-SfAdCollectionCount -Value $Report['guardrailFailures'] } else { 0 }
        manualReview = if ($Report.Contains('manualReview')) { Get-SfAdCollectionCount -Value $Report['manualReview'] } else { 0 }
        unchanged = if ($Report.Contains('unchanged')) { Get-SfAdCollectionCount -Value $Report['unchanged'] } else { 0 }
        errorMessage = $ErrorMessage
        runtimeStatusPath = Get-SfAdRuntimeStatusPath -StatePath $StatePath
    }
}

function Save-SfAdRuntimeStatusSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Snapshot,
        [Parameter(Mandatory)]
        [string]$StatePath
    )

    $runtimeStatusPath = Get-SfAdRuntimeStatusPath -StatePath $StatePath
    $runtimeDirectory = Split-Path -Path $runtimeStatusPath -Parent
    if ($runtimeDirectory -and -not (Test-Path -Path $runtimeDirectory -PathType Container)) {
        New-Item -Path $runtimeDirectory -ItemType Directory -Force | Out-Null
    }

    $Snapshot | ConvertTo-Json -Depth 10 | Set-Content -Path $runtimeStatusPath
    return $runtimeStatusPath
}

function Write-SfAdRuntimeStatusSnapshot {
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

    $snapshot = New-SfAdRuntimeStatusSnapshot -Report $Report -StatePath $StatePath -Stage $Stage -Status $Status -ProcessedWorkers $ProcessedWorkers -TotalWorkers $TotalWorkers -CurrentWorkerId $CurrentWorkerId -LastAction $LastAction -CompletedAt $CompletedAt -ErrorMessage $ErrorMessage
    [void](Save-SfAdRuntimeStatusSnapshot -Snapshot $snapshot -StatePath $StatePath)
    return $snapshot
}

function Get-SfAdRuntimeStatusSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StatePath
    )

    $runtimeStatusPath = Get-SfAdRuntimeStatusPath -StatePath $StatePath
    if (-not (Test-Path -Path $runtimeStatusPath -PathType Leaf)) {
        return $null
    }

    return Get-Content -Path $runtimeStatusPath -Raw | ConvertFrom-Json -Depth 20
}

function Get-SfAdWorkerEntries {
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

function Get-SfAdDateTimeOrNull {
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

function Get-SfAdDurationSeconds {
    [CmdletBinding()]
    param(
        $StartedAt,
        $CompletedAt
    )

    $start = Get-SfAdDateTimeOrNull -Value $StartedAt
    $end = Get-SfAdDateTimeOrNull -Value $CompletedAt
    if ($null -eq $start -or $null -eq $end) {
        return $null
    }

    return [int][math]::Max(0, [math]::Round(($end - $start).TotalSeconds))
}

function New-SfAdEmptyRunSummary {
    [CmdletBinding()]
    param()

    return [pscustomobject]@{
        runId = $null
        path = $null
        artifactType = 'SyncReport'
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

function Get-SfAdReportDirectories {
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

function ConvertTo-SfAdRunSummary {
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
        configPath = if ($Report.PSObject.Properties.Name -contains 'configPath') { $Report.configPath } else { $null }
        mappingConfigPath = if ($Report.PSObject.Properties.Name -contains 'mappingConfigPath') { $Report.mappingConfigPath } else { $null }
        mode = if ($Report.PSObject.Properties.Name -contains 'mode') { $Report.mode } else { $null }
        dryRun = if ($Report.PSObject.Properties.Name -contains 'dryRun') { [bool]$Report.dryRun } else { $false }
        status = if ($Report.PSObject.Properties.Name -contains 'status') { $Report.status } else { $null }
        startedAt = if ($Report.PSObject.Properties.Name -contains 'startedAt') { $Report.startedAt } else { $null }
        completedAt = if ($Report.PSObject.Properties.Name -contains 'completedAt') { $Report.completedAt } else { $null }
        durationSeconds = Get-SfAdDurationSeconds -StartedAt $(if ($Report.PSObject.Properties.Name -contains 'startedAt') { $Report.startedAt } else { $null }) -CompletedAt $(if ($Report.PSObject.Properties.Name -contains 'completedAt') { $Report.completedAt } else { $null })
        reversibleOperations = Get-SfAdCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'operations') { $Report.operations } else { @() })
        creates = Get-SfAdCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'creates') { $Report.creates } else { @() })
        updates = Get-SfAdCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'updates') { $Report.updates } else { @() })
        enables = Get-SfAdCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'enables') { $Report.enables } else { @() })
        disables = Get-SfAdCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'disables') { $Report.disables } else { @() })
        graveyardMoves = Get-SfAdCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'graveyardMoves') { $Report.graveyardMoves } else { @() })
        deletions = Get-SfAdCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'deletions') { $Report.deletions } else { @() })
        quarantined = Get-SfAdCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'quarantined') { $Report.quarantined } else { @() })
        conflicts = Get-SfAdCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'conflicts') { $Report.conflicts } else { @() })
        guardrailFailures = Get-SfAdCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'guardrailFailures') { $Report.guardrailFailures } else { @() })
        manualReview = Get-SfAdCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'manualReview') { $Report.manualReview } else { @() })
        unchanged = Get-SfAdCollectionCount -Value $(if ($Report.PSObject.Properties.Name -contains 'unchanged') { $Report.unchanged } else { @() })
        reviewSummary = if ($Report.PSObject.Properties.Name -contains 'reviewSummary') { $Report.reviewSummary } else { $null }
    }
}

function Get-SfAdRecentRunSummaries {
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

            Get-ChildItem -Path $path -Filter 'sf-ad-sync-*.json' -File
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
                ConvertTo-SfAdRunSummary -Path $_.FullName -Report $report
            }
    )
}

function Get-SfAdMonitorStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath,
        [ValidateRange(1, 1000)]
        [int]$HistoryLimit = 10
    )

    $config = Get-SfAdSyncConfig -Path $ConfigPath
    $state = if ($config.state.path) { Get-SfAdSyncState -Path $config.state.path } else { [pscustomobject]@{ checkpoint = $null; workers = @{} } }
    $workerProperties = @(Get-SfAdWorkerEntries -Workers $state.workers)
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
    $suppressedWorkers = @($workerProperties | Where-Object { $_.Value.suppressed })
    $pendingDeletionWorkers = @(
        $suppressedWorkers | Where-Object {
            $_.Value.deleteAfter -and ((Get-Date $_.Value.deleteAfter) -le (Get-Date))
        }
    )

    $reportDirectories = @(Get-SfAdReportDirectories -Config $config)
    $recentRuns = @(Get-SfAdRecentRunSummaries -Directory $reportDirectories -Limit $HistoryLimit)
    $latestRun = if ($recentRuns.Count -gt 0) { $recentRuns[0] } else { New-SfAdEmptyRunSummary }
    $currentRun = Get-SfAdRuntimeStatusSnapshot -StatePath $config.state.path
    if (-not $currentRun) {
        $currentRun = New-SfAdIdleRuntimeStatus -StatePath $config.state.path
    }

    $resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
    return [pscustomobject]@{
        configPath = $resolvedConfigPath
        lastCheckpoint = $state.checkpoint
        totalTrackedWorkers = $workerProperties.Count
        suppressedWorkers = $suppressedWorkers.Count
        pendingDeletionWorkers = $pendingDeletionWorkers.Count
        latestReport = $latestRun
        latestRun = $latestRun
        currentRun = $currentRun
        recentRuns = $recentRuns
        summary = [pscustomobject]@{
            lastCheckpoint = $state.checkpoint
            totalTrackedWorkers = $workerProperties.Count
            suppressedWorkers = $suppressedWorkers.Count
            pendingDeletionWorkers = $pendingDeletionWorkers.Count
        }
        trackedWorkers = $trackedWorkers
        context = [pscustomobject]@{
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
            runtimeStatusPath = Get-SfAdRuntimeStatusPath -StatePath $config.state.path
        }
    }
}

function Format-SfAdMonitorView {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('SuccessFactors AD Sync Monitor')
    $lines.Add("Config: $($Status.paths.configPath)")
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

function New-SfAdMonitorUiState {
    [CmdletBinding()]
    param()

    return [pscustomobject]@{
        selectedRunIndex = 0
        selectedBucketIndex = 0
        selectedItemIndex = 0
        focus = 'History'
        filterText = ''
        preferredMode = $null
        statusMessage = 'Ready. Keys: q quit, r refresh, tab focus, arrows or j/k select run, [ ] bucket, left/right or h/l select item, / filter, c clear filter, p preflight, d delta dry-run, s delta sync, f full dry-run, a full sync, w worker preview, v review, o open path, y copy path, x export bucket.'
        commandOutput = @()
    }
}

function Get-SfAdMonitorBucketDefinitions {
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

function Get-SfAdMonitorSelectedRun {
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

function Get-SfAdMonitorSelectedRunReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $selectedRun = Get-SfAdMonitorSelectedRun -Status $Status -UiState $UiState
    if (-not $selectedRun -or [string]::IsNullOrWhiteSpace("$($selectedRun.path)")) {
        return $null
    }

    return Get-Content -Path $selectedRun.path -Raw | ConvertFrom-Json -Depth 20
}

function Get-SfAdMonitorSelectedBucket {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $selectedRun = Get-SfAdMonitorSelectedRun -Status $Status -UiState $UiState
    $selectedRunMode = if ($selectedRun -and $selectedRun.PSObject.Properties.Name -contains 'mode') { $selectedRun.mode } else { $null }
    $buckets = @(Get-SfAdMonitorBucketDefinitions -Mode $selectedRunMode)
    $index = if ($buckets.Count -eq 0) { 0 } else { [math]::Min([math]::Max([int]$UiState.selectedBucketIndex, 0), $buckets.Count - 1) }
    $bucket = if ($buckets.Count -eq 0) { [pscustomobject]@{ Name = 'quarantined'; Label = 'Quarantined' } } else { $buckets[$index] }

    $report = Get-SfAdMonitorSelectedRunReport -Status $Status -UiState $UiState
    $items = @()
    if ($report -and $report.PSObject.Properties.Name -contains $bucket.Name) {
        $items = @($report.$($bucket.Name))
    }

    return [pscustomobject]@{
        Bucket = $bucket
        Items = $items
    }
}

function Resolve-SfAdMonitorMappingConfigPath {
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

function Resolve-SfAdMonitorSelectedReportPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $selectedRun = Get-SfAdMonitorSelectedRun -Status $Status -UiState $UiState
    if (-not $selectedRun -or [string]::IsNullOrWhiteSpace("$($selectedRun.path)")) {
        return $null
    }

    return $selectedRun.path
}

function Get-SfAdMonitorActionContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState,
        [string]$MappingConfigPath
    )

    $selectedRun = Get-SfAdMonitorSelectedRun -Status $Status -UiState $UiState
    return [pscustomobject]@{
        configPath = $Status.paths.configPath
        mappingConfigPath = Resolve-SfAdMonitorMappingConfigPath -Status $Status -MappingConfigPath $MappingConfigPath
        reportPath = Resolve-SfAdMonitorSelectedReportPath -Status $Status -UiState $UiState
        selectedRun = $selectedRun
        selectedBucket = Get-SfAdMonitorSelectedBucket -Status $Status -UiState $UiState
    }
}

function ConvertTo-SfAdMonitorInlineText {
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

function Test-SfAdMonitorItemMatchesFilter {
    [CmdletBinding()]
    param(
        $Item,
        [string]$FilterText
    )

    if ([string]::IsNullOrWhiteSpace($FilterText)) {
        return $true
    }

    $needle = $FilterText.Trim().ToLowerInvariant()
    $haystack = (ConvertTo-SfAdMonitorInlineText -Value $Item).ToLowerInvariant()
    return $haystack.Contains($needle)
}

function Get-SfAdMonitorFilteredBucketItems {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$BucketSelection,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    return @(
        @($BucketSelection.Items) | Where-Object {
            Test-SfAdMonitorItemMatchesFilter -Item $_ -FilterText $UiState.filterText
        }
    )
}

function Get-SfAdMonitorSelectedBucketItem {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$BucketSelection,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $items = @(Get-SfAdMonitorFilteredBucketItems -BucketSelection $BucketSelection -UiState $UiState)
    if ($items.Count -eq 0) {
        return $null
    }

    $index = [math]::Min([math]::Max([int]$UiState.selectedItemIndex, 0), $items.Count - 1)
    return $items[$index]
}

function Get-SfAdMonitorSelectedWorkerId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $bucketSelection = Get-SfAdMonitorSelectedBucket -Status $Status -UiState $UiState
    $selectedItem = Get-SfAdMonitorSelectedBucketItem -BucketSelection $bucketSelection -UiState $UiState
    if ($selectedItem -and $selectedItem.PSObject.Properties.Name -contains 'workerId' -and -not [string]::IsNullOrWhiteSpace("$($selectedItem.workerId)")) {
        return "$($selectedItem.workerId)"
    }

    if (-not [string]::IsNullOrWhiteSpace("$($Status.currentRun.currentWorkerId)")) {
        return "$($Status.currentRun.currentWorkerId)"
    }

    return $null
}

function Get-SfAdMonitorSelectedBucketOperation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $report = Get-SfAdMonitorSelectedRunReport -Status $Status -UiState $UiState
    if (-not $report -or -not ($report.PSObject.Properties.Name -contains 'operations')) {
        return $null
    }

    $bucketSelection = Get-SfAdMonitorSelectedBucket -Status $Status -UiState $UiState
    $selectedItem = Get-SfAdMonitorSelectedBucketItem -BucketSelection $bucketSelection -UiState $UiState
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

function Test-SfAdMonitorSelectedRunIsReview {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $selectedRun = Get-SfAdMonitorSelectedRun -Status $Status -UiState $UiState
    $mode = if ($selectedRun -and $selectedRun.PSObject.Properties.Name -contains 'mode') { "$($selectedRun.mode)" } else { '' }
    $artifactType = if ($selectedRun -and $selectedRun.PSObject.Properties.Name -contains 'artifactType') { "$($selectedRun.artifactType)" } else { '' }
    return $mode -eq 'Review' -or $artifactType -in @('FirstSyncReview', 'WorkerPreview')
}

function Get-SfAdMonitorFailureGroups {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$BucketSelection,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $items = @(Get-SfAdMonitorFilteredBucketItems -BucketSelection $BucketSelection -UiState $UiState)
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

function Get-SfAdMonitorFilteredTrackedWorkers {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    return @(
        @($Status.trackedWorkers) | Where-Object {
            Test-SfAdMonitorItemMatchesFilter -Item $_ -FilterText $UiState.filterText
        }
    )
}

function Get-SfAdMonitorSelectedWorkerState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $workerId = Get-SfAdMonitorSelectedWorkerId -Status $Status -UiState $UiState
    if ([string]::IsNullOrWhiteSpace($workerId)) {
        return $null
    }

    $worker = @($Status.trackedWorkers | Where-Object { "$($_.workerId)" -eq $workerId })
    if ($worker.Count -eq 0) {
        return $null
    }

    return $worker[0]
}

function Get-SfAdMonitorCurrentRunDiagnostics {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$CurrentRun
    )

    $startedAt = Get-SfAdDateTimeOrNull -Value $CurrentRun.startedAt
    $lastUpdatedAt = Get-SfAdDateTimeOrNull -Value $CurrentRun.lastUpdatedAt
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

function Get-SfAdMonitorPropertyPairs {
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

function Get-SfAdMonitorOperationDiffLines {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Operation
    )

    $beforeMap = @{}
    foreach ($property in @(Get-SfAdMonitorPropertyPairs -Value $Operation.before)) {
        $beforeMap[$property.Name] = ConvertTo-SfAdMonitorInlineText -Value $property.Value
    }

    $afterMap = @{}
    foreach ($property in @(Get-SfAdMonitorPropertyPairs -Value $Operation.after)) {
        $afterMap[$property.Name] = ConvertTo-SfAdMonitorInlineText -Value $property.Value
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

function Format-SfAdMonitorSelectedObjectLines {
    [CmdletBinding()]
    param(
        $SelectedItem,
        $SelectedOperation
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    if ($SelectedItem) {
        $summaryParts = [System.Collections.Generic.List[string]]::new()
        foreach ($key in @('workerId','samAccountName','userPrincipalName','reason','threshold','targetOu')) {
            if ($SelectedItem.PSObject.Properties.Name -contains $key -and -not [string]::IsNullOrWhiteSpace("$(($SelectedItem.$key))")) {
                $summaryParts.Add("$key=$($SelectedItem.$key)")
            }
        }

        if ($summaryParts.Count -eq 0) {
            $summaryParts.Add((ConvertTo-SfAdMonitorInlineText -Value $SelectedItem))
        }

        $lines.Add("Item: $($summaryParts -join '    ')")
    } else {
        $lines.Add('No object is selected.')
    }

    if (-not $SelectedOperation) {
        $lines.Add('Operation: no matching reversible operation recorded for the selected object.')
    } else {
        $lines.Add("Operation: $($SelectedOperation.operationType)    Target: $(ConvertTo-SfAdMonitorInlineText -Value $SelectedOperation.target)")
        $diffLines = @(Get-SfAdMonitorOperationDiffLines -Operation $SelectedOperation)
        if ($diffLines.Count -eq 0) {
            if ($null -ne $SelectedOperation.after) {
                $lines.Add("After: $(ConvertTo-SfAdMonitorInlineText -Value $SelectedOperation.after)")
            } elseif ($null -ne $SelectedOperation.before) {
                $lines.Add("Before: $(ConvertTo-SfAdMonitorInlineText -Value $SelectedOperation.before)")
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

    return $lines
}

function Get-SfAdMonitorRunDelta {
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

function Format-SfAdMonitorDashboardView {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $selectedRun = Get-SfAdMonitorSelectedRun -Status $Status -UiState $UiState
    $selectedBucket = Get-SfAdMonitorSelectedBucket -Status $Status -UiState $UiState
    $filteredItems = @(Get-SfAdMonitorFilteredBucketItems -BucketSelection $selectedBucket -UiState $UiState)
    $selectedItem = Get-SfAdMonitorSelectedBucketItem -BucketSelection $selectedBucket -UiState $UiState
    $selectedOperation = Get-SfAdMonitorSelectedBucketOperation -Status $Status -UiState $UiState
    $selectedWorkerState = Get-SfAdMonitorSelectedWorkerState -Status $Status -UiState $UiState
    $failureGroups = @(Get-SfAdMonitorFailureGroups -BucketSelection $selectedBucket -UiState $UiState)
    $diagnostics = Get-SfAdMonitorCurrentRunDiagnostics -CurrentRun $Status.currentRun
    $comparisonRun = $null
    if (@($Status.recentRuns).Count -gt ([int]$UiState.selectedRunIndex + 1)) {
        $comparisonRun = @($Status.recentRuns)[[int]$UiState.selectedRunIndex + 1]
    }
    $runDelta = Get-SfAdMonitorRunDelta -ReferenceRun $selectedRun -ComparisonRun $comparisonRun
    $isReviewRun = Test-SfAdMonitorSelectedRunIsReview -Status $Status -UiState $UiState
    $lines = [System.Collections.Generic.List[string]]::new()
    $panelWidth = 110
    $topBorder = "╔" + ("═" * ($panelWidth - 2)) + "╗"
    $midBorder = "╠" + ("═" * ($panelWidth - 2)) + "╣"
    $bottomBorder = "╚" + ("═" * ($panelWidth - 2)) + "╝"
    $rule = "─" * $panelWidth

    $latestState = if ($Status.latestRun.status -eq 'Failed' -or $Status.currentRun.errorMessage) { 'ERROR' } elseif ($Status.currentRun.status -eq 'InProgress') { 'ACTIVE' } else { 'OK' }
    $lines.Add($topBorder)
    $lines.Add("║ SuccessFactors AD Sync Dashboard [$latestState]")
    $lines.Add("║ Config: $($Status.paths.configPath)")
    $lines.Add("║ Refreshed: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))    Focus: $($UiState.focus)    Selected run: $([math]::Min([int]$UiState.selectedRunIndex + 1, [math]::Max(@($Status.recentRuns).Count, 1))) / $([math]::Max(@($Status.recentRuns).Count, 1))    Bucket: $($selectedBucket.Bucket.Label)")
    $lines.Add("║ Filter: $(if ([string]::IsNullOrWhiteSpace($UiState.filterText)) { '(none)' } else { $UiState.filterText })    Matching entries: $($filteredItems.Count) / $(@($selectedBucket.Items).Count)    Selected item: $([math]::Min([int]$UiState.selectedItemIndex + 1, [math]::Max($filteredItems.Count, 1))) / $([math]::Max($filteredItems.Count, 1))")
    $lines.Add($midBorder)
    $lines.Add('▓ Current Run')
    $lines.Add("Status: $($Status.currentRun.status)    Stage: $($Status.currentRun.stage)    Mode: $($Status.currentRun.mode)    DryRun: $($Status.currentRun.dryRun)")
    $lines.Add("Started: $($Status.currentRun.startedAt)    Progress: $($Status.currentRun.processedWorkers) / $($Status.currentRun.totalWorkers)    Worker: $($Status.currentRun.currentWorkerId)")
    $lines.Add("Last action: $($Status.currentRun.lastAction)")
    if ($Status.currentRun.errorMessage) {
        $lines.Add("Error: $($Status.currentRun.errorMessage)")
    }
    $lines.Add("Live counts: C=$($Status.currentRun.creates) U=$($Status.currentRun.updates) E=$($Status.currentRun.enables) D=$($Status.currentRun.disables) G=$($Status.currentRun.graveyardMoves) X=$($Status.currentRun.deletions) Q=$($Status.currentRun.quarantined) F=$($Status.currentRun.conflicts) GF=$($Status.currentRun.guardrailFailures) MR=$($Status.currentRun.manualReview) NC=$($Status.currentRun.unchanged)")
    $lines.Add("Diagnostics: Elapsed=$(if ($null -ne $diagnostics.elapsedSeconds) { $diagnostics.elapsedSeconds } else { '-' })s    RefreshLag=$(if ($null -ne $diagnostics.refreshLagSeconds) { $diagnostics.refreshLagSeconds } else { '-' })s    Throughput=$(if ($null -ne $diagnostics.throughput) { $diagnostics.throughput } else { '-' })/s    ETA=$(if ($null -ne $diagnostics.etaSeconds) { $diagnostics.etaSeconds } else { '-' })s")
    $lines.Add($rule)
    $selectedRunDuration = if ($selectedRun.PSObject.Properties.Name -contains 'durationSeconds') { $selectedRun.durationSeconds } else { $null }
    $selectedRunReversibleOperations = if ($selectedRun.PSObject.Properties.Name -contains 'reversibleOperations') { $selectedRun.reversibleOperations } else { 0 }
    $selectedRunCreates = if ($selectedRun.PSObject.Properties.Name -contains 'creates') { $selectedRun.creates } else { 0 }
    $selectedRunUpdates = if ($selectedRun.PSObject.Properties.Name -contains 'updates') { $selectedRun.updates } else { 0 }
    $selectedRunEnables = if ($selectedRun.PSObject.Properties.Name -contains 'enables') { $selectedRun.enables } else { 0 }
    $selectedRunDisables = if ($selectedRun.PSObject.Properties.Name -contains 'disables') { $selectedRun.disables } else { 0 }
    $selectedRunGraveyardMoves = if ($selectedRun.PSObject.Properties.Name -contains 'graveyardMoves') { $selectedRun.graveyardMoves } else { 0 }
    $selectedRunDeletions = if ($selectedRun.PSObject.Properties.Name -contains 'deletions') { $selectedRun.deletions } else { 0 }
    $selectedRunQuarantined = if ($selectedRun.PSObject.Properties.Name -contains 'quarantined') { $selectedRun.quarantined } else { 0 }
    $selectedRunConflicts = if ($selectedRun.PSObject.Properties.Name -contains 'conflicts') { $selectedRun.conflicts } else { 0 }
    $selectedRunGuardrailFailures = if ($selectedRun.PSObject.Properties.Name -contains 'guardrailFailures') { $selectedRun.guardrailFailures } else { 0 }
    $selectedRunManualReview = if ($selectedRun.PSObject.Properties.Name -contains 'manualReview') { $selectedRun.manualReview } else { 0 }
    $selectedRunUnchanged = if ($selectedRun.PSObject.Properties.Name -contains 'unchanged') { $selectedRun.unchanged } else { 0 }
    $summaryTitle = if ($selectedRun.PSObject.Properties.Name -contains 'artifactType' -and "$($selectedRun.artifactType)" -eq 'WorkerPreview') {
        '▓ Worker Preview Summary'
    } elseif ($isReviewRun) {
        '▓ First Sync Review Summary'
    } else {
        '▓ Latest Run Summary'
    }
    $lines.Add($summaryTitle)
    $lines.Add("Status: $($selectedRun.status)    Mode: $($selectedRun.mode)    DryRun: $($selectedRun.dryRun)    Started: $($selectedRun.startedAt)")
    $lines.Add("Duration(s): $selectedRunDuration    Reversible ops: $selectedRunReversibleOperations")
    $lines.Add("Totals: C=$selectedRunCreates U=$selectedRunUpdates E=$selectedRunEnables D=$selectedRunDisables G=$selectedRunGraveyardMoves X=$selectedRunDeletions Q=$selectedRunQuarantined F=$selectedRunConflicts GF=$selectedRunGuardrailFailures MR=$selectedRunManualReview NC=$selectedRunUnchanged")
    if ($isReviewRun -and $selectedRun.reviewSummary) {
        $lines.Add("Review: existing=$($selectedRun.reviewSummary.existingUsersMatched) changed=$($selectedRun.reviewSummary.existingUsersWithAttributeChanges) aligned=$($selectedRun.reviewSummary.existingUsersWithoutAttributeChanges) creates=$($selectedRun.reviewSummary.proposedCreates) offboarding=$($selectedRun.reviewSummary.proposedOffboarding)")
        $lines.Add("Review notes: mappingCount=$($selectedRun.reviewSummary.mappingCount) deletionPassSkipped=$($selectedRun.reviewSummary.deletionPassSkipped)")
    }
    if ($comparisonRun -and $runDelta) {
        $lines.Add("Compared to older run $($comparisonRun.runId): ΔC=$($runDelta.creates) ΔU=$($runDelta.updates) ΔD=$($runDelta.disables) ΔX=$($runDelta.deletions) ΔQ=$($runDelta.quarantined) ΔF=$($runDelta.conflicts) ΔGF=$($runDelta.guardrailFailures)")
    }
    $lines.Add($rule)
    $lines.Add('▓ State Summary')
    $lines.Add("Checkpoint: $($Status.summary.lastCheckpoint)")
    $lines.Add("Tracked: $($Status.summary.totalTrackedWorkers)    Suppressed: $($Status.summary.suppressedWorkers)    Pending deletion: $($Status.summary.pendingDeletionWorkers)")
    $lines.Add("Context: identityField=$($Status.context.identityField) identityAttribute=$($Status.context.identityAttribute) enableBefore=$($Status.context.enableBeforeStartDays)d deleteRetention=$($Status.context.deletionRetentionDays)d")
    $lines.Add("OUs: active=$($Status.context.defaultActiveOu)    graveyard=$($Status.context.graveyardOu)")
    $lines.Add("Safety: create=$($Status.context.maxCreatesPerRun) disable=$($Status.context.maxDisablesPerRun) delete=$($Status.context.maxDeletionsPerRun)")
    $lines.Add("Paths: mapping=$(Resolve-SfAdMonitorMappingConfigPath -Status $Status)    state=$($Status.paths.statePath)")
    $liveReportDirectory = if ($Status.paths.PSObject.Properties.Name -contains 'reportDirectory') { $Status.paths.reportDirectory } else { $null }
    $reviewReportDirectory = if ($Status.paths.PSObject.Properties.Name -contains 'reviewReportDirectory') { $Status.paths.reviewReportDirectory } else { $null }
    $lines.Add("Reports: live=$liveReportDirectory    review=$reviewReportDirectory")
    $lines.Add($rule)
    $lines.Add('▓ Recent Runs')
    $lines.Add(' Sel Status     Mode  Dry  Started             Dur(s) Create Update Disable Delete Conflict Guardrail')
    $runs = @($Status.recentRuns)
    if ($runs.Count -eq 0) {
        $lines.Add('  -  No sync reports found.')
    } else {
        for ($i = 0; $i -lt $runs.Count; $i += 1) {
            $run = $runs[$i]
            $marker = if ($i -eq [math]::Min([math]::Max([int]$UiState.selectedRunIndex, 0), $runs.Count - 1)) { ' > ' } else { '   ' }
            $lines.Add(("{0}{1,-10} {2,-5} {3,-4} {4,-19} {5,6} {6,6} {7,6} {8,7} {9,6} {10,8} {11,9}" -f `
                    $marker, `
                    $(if ($run.status) { $run.status } else { '-' }), `
                    $(if ($run.mode) { $run.mode } else { '-' }), `
                    $(if ($run.dryRun) { 'yes' } else { 'no' }), `
                    $(if ($run.startedAt) { $run.startedAt } else { '-' }), `
                    $(if ($null -ne $run.durationSeconds) { $run.durationSeconds } else { '-' }), `
                    $run.creates, `
                    $run.updates, `
                    $run.disables, `
                    $run.deletions, `
                    $run.conflicts, `
                    $run.guardrailFailures))
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
        for ($i = 0; $i -lt [math]::Min($filteredItems.Count, 8); $i += 1) {
            $prefix = if ($selectedItem -and $filteredItems[$i] -eq $selectedItem) { '>' } else { '-' }
            $lines.Add("$prefix $(ConvertTo-SfAdMonitorInlineText -Value $filteredItems[$i])")
        }

        if ($filteredItems.Count -gt 8) {
            $lines.Add("... $($filteredItems.Count - 8) more")
        }
    }

    $lines.Add($rule)
    $lines.Add('▓ Selected Object')
    foreach ($line in @(Format-SfAdMonitorSelectedObjectLines -SelectedItem $selectedItem -SelectedOperation $selectedOperation)) {
        $lines.Add($line)
    }

    $lines.Add($rule)
    $lines.Add('▓ Worker State')
    if ($selectedWorkerState) {
        $lines.Add("Tracked: $(ConvertTo-SfAdMonitorInlineText -Value $selectedWorkerState)")
    } else {
        $stateMatches = @(Get-SfAdMonitorFilteredTrackedWorkers -Status $Status -UiState $UiState)
        if ($stateMatches.Count -eq 0) {
            $lines.Add('No tracked worker state matches the current context.')
        } else {
            foreach ($stateMatch in $stateMatches | Select-Object -First 4) {
                $lines.Add("- $(ConvertTo-SfAdMonitorInlineText -Value $stateMatch)")
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

    $lines.Add($midBorder)
    $lines.Add("║ Status: $($UiState.statusMessage)")
    $lines.Add('║ Keys: q quit, r refresh, tab focus, up/down or j/k select run, [ or ] bucket, left/right or h/l select item, / filter, c clear filter, enter inspect, p preflight, d delta dry-run, s delta sync, f full dry-run, a full sync, w worker preview, v review, o open report, y copy report path, x export bucket')
    $lines.Add($bottomBorder)
    return $lines
}

Export-ModuleMember -Function Get-SfAdRuntimeStatusPath, New-SfAdIdleRuntimeStatus, New-SfAdRuntimeStatusSnapshot, Save-SfAdRuntimeStatusSnapshot, Write-SfAdRuntimeStatusSnapshot, Get-SfAdRuntimeStatusSnapshot, Get-SfAdRecentRunSummaries, Get-SfAdMonitorStatus, Format-SfAdMonitorView, New-SfAdMonitorUiState, Get-SfAdMonitorBucketDefinitions, Get-SfAdMonitorSelectedRun, Get-SfAdMonitorSelectedRunReport, Get-SfAdMonitorSelectedBucket, Resolve-SfAdMonitorMappingConfigPath, Resolve-SfAdMonitorSelectedReportPath, Get-SfAdMonitorActionContext, Format-SfAdMonitorDashboardView, Get-SfAdMonitorFilteredBucketItems, Get-SfAdMonitorSelectedBucketItem, Get-SfAdMonitorSelectedBucketOperation, Get-SfAdMonitorFailureGroups, Get-SfAdMonitorSelectedWorkerState, Get-SfAdMonitorCurrentRunDiagnostics, Get-SfAdMonitorOperationDiffLines, Format-SfAdMonitorSelectedObjectLines, Get-SfAdReportDirectories, Test-SfAdMonitorSelectedRunIsReview
