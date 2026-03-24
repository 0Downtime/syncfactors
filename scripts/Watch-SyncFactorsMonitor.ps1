[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [ValidateRange(1, 3600)]
    [int]$RefreshIntervalSeconds = 60,
    [ValidateRange(1, 1000)]
    [int]$HistoryLimit = 10,
    [switch]$PauseAutoRefresh,
    [switch]$RunOnce,
    [switch]$AsText
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleRoot = Join-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -ChildPath 'src/Modules/SyncFactors'
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force
Import-Module (Join-Path $moduleRoot 'State.psm1') -Force
Import-Module (Join-Path $moduleRoot 'Monitoring.psm1') -Force
Import-Module (Join-Path $moduleRoot 'Persistence.psm1') -Force

function Get-OptionalResolvedPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        return $null
    }

    return (Resolve-Path -Path $Path).Path
}

function Get-SyncFactorsMonitorFreshResetLogPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath
    )

    $config = Get-Content -Path $ConfigPath -Raw | ConvertFrom-Json -Depth 20
    $directory = if (
        $config.PSObject.Properties.Name -contains 'reporting' -and
        $config.reporting -and
        $config.reporting.PSObject.Properties.Name -contains 'outputDirectory' -and
        -not [string]::IsNullOrWhiteSpace("$($config.reporting.outputDirectory)")
    ) {
        "$($config.reporting.outputDirectory)"
    } else {
        [System.IO.Path]::GetTempPath()
    }

    if (-not (Test-Path -Path $directory -PathType Container)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    return Join-Path -Path $directory -ChildPath "syncfactors-fresh-reset-$timestamp.log"
}

function Get-SyncFactorsMonitorFreshResetPreviewReportPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath
    )

    $config = Get-Content -Path $ConfigPath -Raw | ConvertFrom-Json -Depth 20
    $directory = if (
        $config.PSObject.Properties.Name -contains 'reporting' -and
        $config.reporting -and
        $config.reporting.PSObject.Properties.Name -contains 'outputDirectory' -and
        -not [string]::IsNullOrWhiteSpace("$($config.reporting.outputDirectory)")
    ) {
        "$($config.reporting.outputDirectory)"
    } else {
        [System.IO.Path]::GetTempPath()
    }

    if (-not (Test-Path -Path $directory -PathType Container)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    return Join-Path -Path $directory -ChildPath "syncfactors-ResetPreview-$timestamp.json"
}

function Show-SyncFactorsMonitorFrame {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ResolvedConfigPath,
        [string]$ResolvedMappingConfigPath,
        [Parameter(Mandatory)]
        [int]$HistoryDepth,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState,
        [switch]$AsTextOutput
    )

    try {
        $status = Get-SyncFactorsMonitorStatus -ConfigPath $ResolvedConfigPath -HistoryLimit $HistoryDepth
        $rawLines = if ($AsTextOutput) {
            @(Format-SyncFactorsMonitorView -Status $status)
        } else {
            @(Format-SyncFactorsMonitorDashboardView -Status $status -UiState $UiState)
        }
        $lines = @($rawLines | Where-Object { $null -ne $_ } | ForEach-Object { "$_" })
        if ($lines.Count -eq 0) {
            $lines = @(
                'SuccessFactors AD Sync Dashboard'
                "Config: $ResolvedConfigPath"
                "Refreshed: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))"
                ''
                'Monitor returned no output.'
                ''
                'Keys: q quit, r refresh'
            )
        }
    } catch {
        $lines = @(
            'SuccessFactors AD Sync Dashboard',
            "Config: $ResolvedConfigPath",
            "Refreshed: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))",
            '',
            'Monitor error',
            $_.Exception.Message,
            '',
            'Keys: q quit, r refresh'
        )
        $status = $null
    }

    if ($AsTextOutput) {
        return [pscustomobject]@{
            Status = $status
            Lines = $lines
        }
    }

    Clear-Host
    Write-SyncFactorsStyledMonitorFrame -Lines $lines

    return [pscustomobject]@{
        Status = $status
        Lines = $lines
    }
}

function Write-SyncFactorsStyledMonitorFrame {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Lines
    )

    $palette = @{
        Header = 'Cyan'
        Border = 'DarkCyan'
        Section = 'Yellow'
        Active = 'Green'
        Warning = 'Yellow'
        Error = 'Red'
        Selection = 'Magenta'
        Muted = 'DarkGray'
        Detail = 'Gray'
        Footer = 'Cyan'
        Default = 'White'
    }

    foreach ($line in $Lines) {
        $color = $palette.Default

        if ($line -match '^[╔╠╚].*[╗╣╝]$' -or $line -match '^─+$') {
            $color = $palette.Border
        } elseif ($line -match '^║ SuccessFactors AD Sync Dashboard') {
            $color = $palette.Header
        } elseif ($line -match '^║ Health:') {
            if ($line -match 'SF=OK' -and $line -match 'AD=OK') {
                $color = $palette.Active
            } elseif ($line -match 'SF=ERROR' -and $line -match 'AD=ERROR') {
                $color = $palette.Error
            } else {
                $color = $palette.Warning
            }
        } elseif ($line -match '\[CREATE\]') {
            $color = $palette.Active
        } elseif ($line -match '\[DELETE\]') {
            $color = $palette.Error
        } elseif ($line -match '\[UPDATE\]') {
            $color = $palette.Warning
        } elseif ($line -match '^▓ ') {
            $color = $palette.Section
        } elseif ($line -match '^\s+>\s' -or $line -match '^\s+>') {
            $color = $palette.Selection
        } elseif ($line -match '^Status: .*InProgress' -or $line -match '\[ACTIVE\]') {
            $color = $palette.Active
        } elseif ($line -match '^Status: .*Failed' -or $line -match '\[ERROR\]' -or $line -match '^Error:') {
            $color = $palette.Error
        } elseif ($line -match 'Q=\d*[1-9]\d*' -or $line -match 'F=\d*[1-9]\d*' -or $line -match 'GF=\d*[1-9]\d*' -or $line -match 'MR=\d*[1-9]\d*') {
            $color = $palette.Warning
        } elseif ($line -match '^║ Status:' -or $line -match '^║ Keys:') {
            $color = $palette.Footer
        } elseif ($line -match '^-' -or $line -match '^No entries' -or $line -match '^Command Output$') {
            $color = $palette.Detail
        } elseif ([string]::IsNullOrWhiteSpace($line)) {
            $color = $palette.Muted
        }

        Write-Host $line -ForegroundColor $color
    }
}

function Read-SyncFactorsMonitorFilterText {
    [CmdletBinding()]
    param(
        [string]$CurrentFilter
    )

    Write-Host ''
    Write-Host "Filter current bucket entries. Leave blank to clear the filter." -ForegroundColor Cyan
    $prompt = if ([string]::IsNullOrWhiteSpace($CurrentFilter)) { 'Filter' } else { "Filter [$CurrentFilter]" }
    return Read-Host -Prompt $prompt
}

function Read-SyncFactorsMonitorWorkerId {
    [CmdletBinding()]
    param()

    Write-Host ''
    Write-Host 'Start a single-worker preview by SuccessFactors identity field value.' -ForegroundColor Cyan
    return Read-Host -Prompt 'WorkerId'
}

function Confirm-SyncFactorsMonitorWriteAction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Label,
        [Parameter(Mandatory)]
        [string]$WriteTarget
    )

    Write-Host ''
    Write-Host "$Label may write $WriteTarget." -ForegroundColor Yellow
    $response = Read-Host -Prompt 'Type YES to continue'
    return "$response".Trim() -ceq 'YES'
}

function Get-SyncFactorsMonitorWorkerPreviewEntriesFromReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Report,
        [Parameter(Mandatory)]
        [string]$WorkerId
    )

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($bucket in @('manualReview', 'conflicts', 'quarantined', 'creates', 'updates', 'enables', 'disables', 'graveyardMoves', 'deletions', 'unchanged')) {
        if ($Report.PSObject.Properties.Name -notcontains $bucket) {
            continue
        }

        foreach ($item in @($Report.$bucket | Where-Object {
                    $_ -and
                    $_.PSObject.Properties.Name -contains 'workerId' -and
                    "$($_.workerId)" -eq $WorkerId
                })) {
            $entries.Add([pscustomobject]@{
                    bucket = $bucket
                    item = $item
                })
        }
    }

    return @($entries)
}

function Get-SyncFactorsMonitorWorkerPreviewValueFromEntries {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Entries,
        [Parameter(Mandatory)]
        [string]$PropertyName
    )

    foreach ($entry in $Entries) {
        if ($entry.item.PSObject.Properties.Name -contains $PropertyName) {
            $value = $entry.item.$PropertyName
            if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace("$value")) {
                return $value
            }
        }
    }

    return $null
}

function ConvertTo-SyncFactorsMonitorWorkerPreviewResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Report,
        [Parameter(Mandatory)]
        [string]$ReportPath
    )

    $workerId = if (
        $Report.PSObject.Properties.Name -contains 'workerScope' -and
        $Report.workerScope -and
        $Report.workerScope.PSObject.Properties.Name -contains 'workerId' -and
        -not [string]::IsNullOrWhiteSpace("$($Report.workerScope.workerId)")
    ) {
        "$($Report.workerScope.workerId)"
    } else {
        $null
    }

    if ([string]::IsNullOrWhiteSpace($workerId)) {
        return $null
    }

    $entries = @(Get-SyncFactorsMonitorWorkerPreviewEntriesFromReport -Report $Report -WorkerId $workerId)
    $bucketNames = @($entries | ForEach-Object { $_.bucket } | Select-Object -Unique)
    $changedAttributes = @()
    foreach ($entry in $entries) {
        if ($entry.item.PSObject.Properties.Name -contains 'changedAttributeDetails') {
            $changedAttributes = @($entry.item.changedAttributeDetails)
            break
        }

        if ($entry.item.PSObject.Properties.Name -contains 'attributeRows') {
            $changedAttributes = @($entry.item.attributeRows | Where-Object { $_.changed })
            break
        }
    }

    $matchedExistingUser = if (@($entries | Where-Object { $_.item.PSObject.Properties.Name -contains 'matchedExistingUser' -and [bool]$_.item.matchedExistingUser }).Count -gt 0) {
        $true
    } elseif (@($bucketNames | Where-Object { $_ -in @('updates', 'unchanged', 'enables', 'disables', 'graveyardMoves', 'deletions') }).Count -gt 0) {
        $true
    } elseif (@($bucketNames | Where-Object { $_ -eq 'creates' }).Count -gt 0) {
        $false
    } else {
        $null
    }

    return [pscustomobject]@{
        reportPath = $ReportPath
        runId = $Report.runId
        mode = $Report.mode
        status = $Report.status
        artifactType = $Report.artifactType
        workerScope = $Report.workerScope
        reviewSummary = $Report.reviewSummary
        previewMode = 'full'
        preview = [pscustomobject]@{
            workerId = $workerId
            buckets = $bucketNames
            matchedExistingUser = $matchedExistingUser
            reviewCategory = Get-SyncFactorsMonitorWorkerPreviewValueFromEntries -Entries $entries -PropertyName 'reviewCategory'
            reason = Get-SyncFactorsMonitorWorkerPreviewValueFromEntries -Entries $entries -PropertyName 'reason'
            samAccountName = Get-SyncFactorsMonitorWorkerPreviewValueFromEntries -Entries $entries -PropertyName 'samAccountName'
            targetOu = Get-SyncFactorsMonitorWorkerPreviewValueFromEntries -Entries $entries -PropertyName 'targetOu'
            currentDistinguishedName = Get-SyncFactorsMonitorWorkerPreviewValueFromEntries -Entries $entries -PropertyName 'currentDistinguishedName'
            currentEnabled = Get-SyncFactorsMonitorWorkerPreviewValueFromEntries -Entries $entries -PropertyName 'currentEnabled'
            proposedEnable = Get-SyncFactorsMonitorWorkerPreviewValueFromEntries -Entries $entries -PropertyName 'proposedEnable'
        }
        changedAttributes = $changedAttributes
        operations = @($Report.operations | Where-Object { "$($_.workerId)" -eq $workerId })
        entries = @($entries)
    }
}

function Set-SyncFactorsMonitorWorkerPreviewState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$UiState,
        [Parameter(Mandatory)]
        [pscustomobject]$PreviewResult,
        [AllowNull()]
        [pscustomobject]$Status
    )

    $UiState.viewMode = 'WorkerPreviewDiff'
    $UiState.pendingAction = $null
    $UiState.pendingReportIndex = 0
    $UiState.pendingWorkerId = if (
        $PreviewResult.PSObject.Properties.Name -contains 'workerScope' -and
        $PreviewResult.workerScope -and
        $PreviewResult.workerScope.PSObject.Properties.Name -contains 'workerId'
    ) { "$($PreviewResult.workerScope.workerId)" } else { $null }
    $UiState.workerPreviewResult = $PreviewResult
    $UiState.workerPreviewDiffRows = @(Get-SyncFactorsMonitorWorkerPreviewDiffRows -PreviewResult $PreviewResult)
    $UiState.statusMessage = "Worker preview loaded for $($UiState.pendingWorkerId). Review the diff, then press a to apply or Esc to cancel."
    $UiState.commandOutput = @(
        "PreviewRunId=$($PreviewResult.runId)"
        "PreviewReport=$($PreviewResult.reportPath)"
    )

    if ($Status -and -not [string]::IsNullOrWhiteSpace("$($PreviewResult.reportPath)")) {
        $runs = @($Status.recentRuns)
        for ($index = 0; $index -lt $runs.Count; $index += 1) {
            if ("$($runs[$index].path)" -eq "$($PreviewResult.reportPath)") {
                $UiState.selectedRunIndex = $index
                break
            }
        }
    }
}

function Clear-SyncFactorsMonitorWorkerPreviewState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $UiState.viewMode = 'Dashboard'
    $UiState.pendingAction = $null
    $UiState.pendingWorkerId = $null
    $UiState.workerPreviewResult = $null
    $UiState.workerPreviewDiffRows = @()
}

function Invoke-SyncFactorsMonitorInlineWorkerPreview {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState,
        [AllowEmptyString()]
        [string]$ResolvedMappingConfigPath
    )

    $context = Get-SyncFactorsMonitorActionContext -Status $Status -UiState $UiState -MappingConfigPath $ResolvedMappingConfigPath
    if (-not $context.mappingConfigPath) {
        $UiState.statusMessage = 'Worker preview unavailable: no mapping config path was provided and none could be inferred from recent runs.'
        $UiState.commandOutput = @()
        return
    }

    $workerId = Read-SyncFactorsMonitorWorkerId
    if ([string]::IsNullOrWhiteSpace($workerId)) {
        $UiState.statusMessage = 'Worker preview cancelled: no worker ID was provided.'
        $UiState.commandOutput = @()
        return
    }

    $workerId = $workerId.Trim()
    $projectRoot = Split-Path -Path $PSScriptRoot -Parent
    try {
        $json = & pwsh -NoLogo -NoProfile -File (Join-Path $projectRoot 'scripts/Invoke-SyncFactorsWorkerPreview.ps1') `
            -ConfigPath $context.configPath `
            -MappingConfigPath $context.mappingConfigPath `
            -WorkerId $workerId `
            -PreviewMode Full `
            -AsJson 2>&1
        $previewResult = @($json) -join [Environment]::NewLine | ConvertFrom-Json -Depth 30
        Set-SyncFactorsMonitorWorkerPreviewState -UiState $UiState -PreviewResult $previewResult
    } catch {
        Clear-SyncFactorsMonitorWorkerPreviewState -UiState $UiState
        $UiState.statusMessage = "Worker preview failed for $workerId."
        $UiState.commandOutput = @($_.Exception.Message)
    }
}

function Open-SyncFactorsMonitorSelectedWorkerPreview {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    if (-not (Test-SyncFactorsMonitorSelectedRunIsWorkerPreview -Status $Status -UiState $UiState)) {
        $UiState.statusMessage = 'Worker apply is only available on a selected single-worker review run.'
        $UiState.commandOutput = @()
        return
    }

    $selectedRun = Get-SyncFactorsMonitorSelectedRun -Status $Status -UiState $UiState
    if (-not $selectedRun -or [string]::IsNullOrWhiteSpace("$($selectedRun.path)") -or -not (Test-Path -Path $selectedRun.path -PathType Leaf)) {
        $UiState.statusMessage = 'Selected worker preview report is unavailable.'
        $UiState.commandOutput = @()
        return
    }

    try {
        $report = Get-Content -Path $selectedRun.path -Raw | ConvertFrom-Json -Depth 30
        $previewResult = ConvertTo-SyncFactorsMonitorWorkerPreviewResult -Report $report -ReportPath $selectedRun.path
        if (-not $previewResult) {
            throw 'Selected worker preview is missing worker scope.'
        }
        Set-SyncFactorsMonitorWorkerPreviewState -UiState $UiState -PreviewResult $previewResult -Status $Status
    } catch {
        Clear-SyncFactorsMonitorWorkerPreviewState -UiState $UiState
        $UiState.statusMessage = 'Failed to load the selected worker preview.'
        $UiState.commandOutput = @($_.Exception.Message)
    }
}

function Invoke-SyncFactorsMonitorInlineWorkerApply {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState,
        [AllowEmptyString()]
        [string]$ResolvedMappingConfigPath,
        [Parameter(Mandatory)]
        [int]$HistoryDepth
    )

    $context = Get-SyncFactorsMonitorActionContext -Status $Status -UiState $UiState -MappingConfigPath $ResolvedMappingConfigPath
    if (-not $context.mappingConfigPath) {
        $UiState.statusMessage = 'Worker apply unavailable: no mapping config path was provided and none could be inferred from recent runs.'
        $UiState.commandOutput = @()
        return
    }

    $previewResult = $UiState.workerPreviewResult
    $workerId = if (
        $previewResult -and
        $previewResult.PSObject.Properties.Name -contains 'workerScope' -and
        $previewResult.workerScope -and
        $previewResult.workerScope.PSObject.Properties.Name -contains 'workerId'
    ) { "$($previewResult.workerScope.workerId)" } else { $null }
    if ([string]::IsNullOrWhiteSpace($workerId)) {
        $UiState.statusMessage = 'Worker apply unavailable: the inline worker preview is missing worker scope.'
        $UiState.commandOutput = @()
        return
    }

    if (-not (Confirm-SyncFactorsMonitorWriteAction -Label "Apply worker sync for $workerId" -WriteTarget 'AD objects, sync state, runtime status, and report files')) {
        $UiState.statusMessage = 'Worker apply cancelled.'
        $UiState.commandOutput = @()
        return
    }

    $projectRoot = Split-Path -Path $PSScriptRoot -Parent
    try {
        $json = & pwsh -NoLogo -NoProfile -File (Join-Path $projectRoot 'scripts/Invoke-SyncFactorsWorkerSync.ps1') `
            -ConfigPath $context.configPath `
            -MappingConfigPath $context.mappingConfigPath `
            -WorkerId $workerId `
            -AsJson 2>&1
        $syncResult = @($json) -join [Environment]::NewLine | ConvertFrom-Json -Depth 30
        $syncReport = if ($syncResult.reportPath) {
            Get-SyncFactorsReportFromReference -Reference $syncResult.reportPath -StatePath $context.statePath
        } else {
            $null
        }
        $freshStatus = Get-SyncFactorsMonitorStatus -ConfigPath $context.configPath -HistoryLimit $HistoryDepth
        Clear-SyncFactorsMonitorWorkerPreviewState -UiState $UiState
        if ($syncResult.reportPath) {
            $runs = @($freshStatus.recentRuns)
            for ($index = 0; $index -lt $runs.Count; $index += 1) {
                if ("$($runs[$index].path)" -eq "$($syncResult.reportPath)") {
                    $UiState.selectedRunIndex = $index
                    break
                }
            }
        }
        $UiState.statusMessage = "Single-worker sync completed for $workerId."
        $UiState.commandOutput = @(Get-SyncFactorsMonitorWorkerSyncSummaryLines -SyncResult $syncResult -Report $syncReport)
    } catch {
        $UiState.statusMessage = "Worker apply failed for $workerId."
        $UiState.commandOutput = @($_.Exception.Message)
    }
}

function Export-SyncFactorsMonitorBucketSelection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $bucketSelection = Get-SyncFactorsMonitorSelectedBucket -Status $Status -UiState $UiState
    $items = @(Get-SyncFactorsMonitorFilteredBucketItems -BucketSelection $bucketSelection -UiState $UiState)
    if ($items.Count -eq 0) {
        $UiState.statusMessage = 'Export skipped: no filtered bucket entries to export.'
        $UiState.commandOutput = @()
        return
    }

    if (-not (Confirm-SyncFactorsMonitorWriteAction -Label 'Bucket export' -WriteTarget 'a JSON file in the temp directory')) {
        $UiState.statusMessage = 'Bucket export cancelled.'
        $UiState.commandOutput = @()
        return
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $path = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath "syncfactors-monitor-$($bucketSelection.Bucket.Name)-$timestamp.json"
    $items | ConvertTo-Json -Depth 20 | Set-Content -Path $path
    $UiState.statusMessage = "Exported filtered bucket to $path"
    $UiState.commandOutput = @($path)
}

function Invoke-SyncFactorsMonitorShortcut {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Preflight','DeltaDryRun','DeltaRun','FullDryRun','FullRun','ReviewRun','WorkerPreview','ApplyReviewedWorker','FreshSyncReset','OpenReport','CopyReportPath')]
        [string]$Action,
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState,
        [AllowEmptyString()]
        [string]$ResolvedMappingConfigPath
    )

    $context = Get-SyncFactorsMonitorActionContext -Status $Status -UiState $UiState -MappingConfigPath $ResolvedMappingConfigPath
    $projectRoot = Split-Path -Path $PSScriptRoot -Parent

    switch ($Action) {
        'Preflight' {
            if (-not $context.mappingConfigPath) {
                $UiState.statusMessage = 'Preflight unavailable: no mapping config path was provided and none could be inferred from recent runs.'
                $UiState.commandOutput = @()
                return
            }

            try {
                $output = & pwsh -NoLogo -NoProfile -File (Join-Path $projectRoot 'scripts/Invoke-SyncFactorsPreflight.ps1') -ConfigPath $context.configPath -MappingConfigPath $context.mappingConfigPath 2>&1
                $UiState.statusMessage = 'Preflight completed.'
                $UiState.commandOutput = @($output | ForEach-Object { "$_" })
            } catch {
                $UiState.statusMessage = 'Preflight failed.'
                $UiState.commandOutput = @($_.Exception.Message)
            }
        }
        'DeltaDryRun' {
            if (-not $context.mappingConfigPath) {
                $UiState.statusMessage = 'Delta dry-run unavailable: no mapping config path was provided and none could be inferred from recent runs.'
                $UiState.commandOutput = @()
                return
            }

            if (-not (Confirm-SyncFactorsMonitorWriteAction -Label 'Delta dry-run' -WriteTarget 'runtime status and report files')) {
                $UiState.statusMessage = 'Delta dry-run cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'src/Invoke-SyncFactors.ps1')
                '-ConfigPath'
                $context.configPath
                '-MappingConfigPath'
                $context.mappingConfigPath
                '-Mode'
                'Delta'
                '-DryRun'
            )

            try {
                Start-Process -FilePath 'pwsh' -ArgumentList $argumentList | Out-Null
                $UiState.statusMessage = 'Started delta dry-run in a new PowerShell process.'
                $UiState.commandOutput = @("Mode=Delta", "DryRun=True", "Config=$($context.configPath)", "Mapping=$($context.mappingConfigPath)")
            } catch {
                $UiState.statusMessage = 'Failed to start delta dry-run.'
                $UiState.commandOutput = @($_.Exception.Message)
            }
        }
        'DeltaRun' {
            if (-not $context.mappingConfigPath) {
                $UiState.statusMessage = 'Delta run unavailable: no mapping config path was provided and none could be inferred from recent runs.'
                $UiState.commandOutput = @()
                return
            }

            if (-not (Confirm-SyncFactorsMonitorWriteAction -Label 'Delta sync' -WriteTarget 'AD objects, sync state, runtime status, and report files')) {
                $UiState.statusMessage = 'Delta sync cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'src/Invoke-SyncFactors.ps1')
                '-ConfigPath'
                $context.configPath
                '-MappingConfigPath'
                $context.mappingConfigPath
                '-Mode'
                'Delta'
            )

            try {
                Start-Process -FilePath 'pwsh' -ArgumentList $argumentList | Out-Null
                $UiState.statusMessage = 'Started delta sync in a new PowerShell process.'
                $UiState.commandOutput = @("Mode=Delta", "DryRun=False", "Config=$($context.configPath)", "Mapping=$($context.mappingConfigPath)")
            } catch {
                $UiState.statusMessage = 'Failed to start delta sync.'
                $UiState.commandOutput = @($_.Exception.Message)
            }
        }
        'FullDryRun' {
            if (-not $context.mappingConfigPath) {
                $UiState.statusMessage = 'Full dry-run unavailable: no mapping config path was provided and none could be inferred from recent runs.'
                $UiState.commandOutput = @()
                return
            }

            if (-not (Confirm-SyncFactorsMonitorWriteAction -Label 'Full dry-run' -WriteTarget 'runtime status and report files')) {
                $UiState.statusMessage = 'Full dry-run cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'src/Invoke-SyncFactors.ps1')
                '-ConfigPath'
                $context.configPath
                '-MappingConfigPath'
                $context.mappingConfigPath
                '-Mode'
                'Full'
                '-DryRun'
            )

            try {
                Start-Process -FilePath 'pwsh' -ArgumentList $argumentList | Out-Null
                $UiState.statusMessage = 'Started full dry-run in a new PowerShell process.'
                $UiState.commandOutput = @("Mode=Full", "DryRun=True", "Config=$($context.configPath)", "Mapping=$($context.mappingConfigPath)")
            } catch {
                $UiState.statusMessage = 'Failed to start full dry-run.'
                $UiState.commandOutput = @($_.Exception.Message)
            }
        }
        'FullRun' {
            if (-not $context.mappingConfigPath) {
                $UiState.statusMessage = 'Full run unavailable: no mapping config path was provided and none could be inferred from recent runs.'
                $UiState.commandOutput = @()
                return
            }

            if (-not (Confirm-SyncFactorsMonitorWriteAction -Label 'Full sync' -WriteTarget 'AD objects, sync state, runtime status, and report files')) {
                $UiState.statusMessage = 'Full sync cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'src/Invoke-SyncFactors.ps1')
                '-ConfigPath'
                $context.configPath
                '-MappingConfigPath'
                $context.mappingConfigPath
                '-Mode'
                'Full'
            )

            try {
                Start-Process -FilePath 'pwsh' -ArgumentList $argumentList | Out-Null
                $UiState.statusMessage = 'Started full sync in a new PowerShell process.'
                $UiState.commandOutput = @("Mode=Full", "DryRun=False", "Config=$($context.configPath)", "Mapping=$($context.mappingConfigPath)")
            } catch {
                $UiState.statusMessage = 'Failed to start full sync.'
                $UiState.commandOutput = @($_.Exception.Message)
            }
        }
        'ReviewRun' {
            if (-not $context.mappingConfigPath) {
                $UiState.statusMessage = 'Review unavailable: no mapping config path was provided and none could be inferred from recent runs.'
                $UiState.commandOutput = @()
                return
            }

            if (-not (Confirm-SyncFactorsMonitorWriteAction -Label 'First-sync review' -WriteTarget 'runtime status and review report files')) {
                $UiState.statusMessage = 'First-sync review cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'scripts/Invoke-SyncFactorsFirstSyncReview.ps1')
                '-ConfigPath'
                $context.configPath
                '-MappingConfigPath'
                $context.mappingConfigPath
            )

            try {
                Start-Process -FilePath 'pwsh' -ArgumentList $argumentList | Out-Null
                $UiState.statusMessage = 'Started first-sync review in a new PowerShell process.'
                $UiState.commandOutput = @("Config=$($context.configPath)", "Mapping=$($context.mappingConfigPath)")
            } catch {
                $UiState.statusMessage = 'Failed to start first-sync review.'
                $UiState.commandOutput = @($_.Exception.Message)
            }
        }
        'WorkerPreview' {
            Invoke-SyncFactorsMonitorInlineWorkerPreview -Status $Status -UiState $UiState -ResolvedMappingConfigPath $ResolvedMappingConfigPath
        }
        'ApplyReviewedWorker' {
            if (-not $context.mappingConfigPath) {
                $UiState.statusMessage = 'Worker apply unavailable: no mapping config path was provided and none could be inferred from recent runs.'
                $UiState.commandOutput = @()
                return
            }

            $selectedRun = $context.selectedRun
            $workerId = if (
                $selectedRun -and
                $selectedRun.PSObject.Properties.Name -contains 'workerScope' -and
                $selectedRun.workerScope -and
                $selectedRun.workerScope.PSObject.Properties.Name -contains 'workerId'
            ) { "$($selectedRun.workerScope.workerId)" } else { $null }
            if ([string]::IsNullOrWhiteSpace($workerId)) {
                $UiState.statusMessage = 'Worker apply unavailable: the selected run is not scoped to one worker.'
                $UiState.commandOutput = @()
                return
            }

            if (-not (Confirm-SyncFactorsMonitorWriteAction -Label "Apply worker sync for $workerId" -WriteTarget 'AD and sync state')) {
                $UiState.statusMessage = 'Worker apply cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'scripts/Invoke-SyncFactorsWorkerSync.ps1')
                '-ConfigPath'
                $context.configPath
                '-MappingConfigPath'
                $context.mappingConfigPath
                '-WorkerId'
                $workerId
            )

            try {
                Start-Process -FilePath 'pwsh' -ArgumentList $argumentList | Out-Null
                $UiState.statusMessage = "Started single-worker sync for $workerId in a new PowerShell process."
                $UiState.commandOutput = @("Config=$($context.configPath)", "Mapping=$($context.mappingConfigPath)", "WorkerId=$workerId")
            } catch {
                $UiState.statusMessage = 'Failed to start single-worker sync.'
                $UiState.commandOutput = @($_.Exception.Message)
            }
        }
        'FreshSyncReset' {
            if (-not (Confirm-SyncFactorsMonitorWriteAction -Label 'Fresh sync reset' -WriteTarget 'managed AD user objects and local sync state')) {
                $UiState.statusMessage = 'Fresh sync reset cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $freshResetLogPath = Get-SyncFactorsMonitorFreshResetLogPath -ConfigPath $context.configPath
            $freshResetPreviewReportPath = Get-SyncFactorsMonitorFreshResetPreviewReportPath -ConfigPath $context.configPath

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'scripts/Invoke-SyncFactorsFreshSyncReset.ps1')
                '-ConfigPath'
                $context.configPath
                '-LogPath'
                $freshResetLogPath
                '-PreviewReportPath'
                $freshResetPreviewReportPath
            )

            try {
                Start-Process -FilePath 'pwsh' -ArgumentList $argumentList | Out-Null
                $UiState.statusMessage = 'Started fresh sync reset in a new PowerShell process.'
                $UiState.commandOutput = @("Config=$($context.configPath)", "PreviewReport=$freshResetPreviewReportPath", "Log=$freshResetLogPath")
            } catch {
                $UiState.statusMessage = 'Failed to start fresh sync reset.'
                $UiState.commandOutput = @($_.Exception.Message)
            }
        }
        'OpenReport' {
            if (-not $context.reportPath) {
                $UiState.statusMessage = 'Open report unavailable: no selected report path.'
                $UiState.commandOutput = @()
                return
            }

            $UiState.viewMode = 'ReportExplorer'
            $UiState.reportCategoryIndex = 0
            $UiState.reportEntryIndex = 0
            $UiState.statusMessage = 'Report explorer opened. Use [ ] to switch category and j/k to move objects.'
            $UiState.commandOutput = @($context.reportPath)
        }
        'CopyReportPath' {
            if (-not $context.reportPath) {
                $UiState.statusMessage = 'Copy report path unavailable: no selected report path.'
                $UiState.commandOutput = @()
                return
            }

            if (-not (Confirm-SyncFactorsMonitorWriteAction -Label 'Copy report path' -WriteTarget 'the clipboard')) {
                $UiState.statusMessage = 'Copy report path cancelled.'
                $UiState.commandOutput = @()
                return
            }

            if (Get-Command Set-Clipboard -ErrorAction SilentlyContinue) {
                Set-Clipboard -Value $context.reportPath
                $UiState.statusMessage = 'Copied selected report path to clipboard.'
                $UiState.commandOutput = @($context.reportPath)
                return
            }

            $UiState.statusMessage = 'Clipboard command not available. Report path shown below.'
            $UiState.commandOutput = @($context.reportPath)
        }
    }
}

$resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
$resolvedMappingConfigPath = Get-OptionalResolvedPath -Path $MappingConfigPath
$uiState = New-SyncFactorsMonitorUiState
$uiState.autoRefreshEnabled = $false
$uiState.statusMessage = 'Auto-refresh paused. Press t to resume or r to refresh once.'
$lastStatus = $null

do {
    $frame = Show-SyncFactorsMonitorFrame -ResolvedConfigPath $resolvedConfigPath -ResolvedMappingConfigPath $resolvedMappingConfigPath -HistoryDepth $HistoryLimit -UiState $uiState -AsTextOutput:$AsText
    $lastStatus = $frame.Status

    if ($RunOnce -or $AsText) {
        if ($AsText) {
            $frame.Lines -join [Environment]::NewLine
        }
        break
    }

    $quitRequested = $false
    $refreshRequested = $false
    $elapsedSeconds = 0
    while (-not $quitRequested -and -not $refreshRequested) {
        if ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            if ("$($uiState.viewMode)" -eq 'WorkerPreviewDiff') {
                switch ($key.Key) {
                    'Escape' {
                        Clear-SyncFactorsMonitorWorkerPreviewState -UiState $uiState
                        $uiState.statusMessage = 'Returned to dashboard.'
                        $refreshRequested = $true
                        continue
                    }
                }

                switch ($key.KeyChar) {
                    'a' {
                        if ($lastStatus) {
                            Invoke-SyncFactorsMonitorInlineWorkerApply -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath -HistoryDepth $HistoryLimit
                        }
                        $refreshRequested = $true
                        continue
                    }
                    'o' {
                        if ($lastStatus -and $uiState.workerPreviewResult -and $uiState.workerPreviewResult.reportPath) {
                            $runs = @($lastStatus.recentRuns)
                            for ($index = 0; $index -lt $runs.Count; $index += 1) {
                                if ("$($runs[$index].path)" -eq "$($uiState.workerPreviewResult.reportPath)") {
                                    $uiState.selectedRunIndex = $index
                                    break
                                }
                            }
                            $uiState.viewMode = 'Dashboard'
                            Invoke-SyncFactorsMonitorShortcut -Action OpenReport -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                        }
                        $refreshRequested = $true
                        continue
                    }
                }
            }
            if ($uiState.pendingAction -eq 'ApplyWorkerSync') {
                switch ($key.Key) {
                    'A' {
                        if ($lastStatus) {
                            Invoke-SyncFactorsMonitorShortcut -Action ApplyReviewedWorker -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                        }
                        $uiState.pendingAction = $null
                        $uiState.pendingWorkerId = $null
                        $refreshRequested = $true
                        continue
                    }
                    'O' {
                        if ($lastStatus) {
                            $relatedRuns = @(Get-SyncFactorsMonitorWorkerRelatedRuns -Status $lastStatus -WorkerId $uiState.pendingWorkerId)
                            if ($relatedRuns.Count -eq 0) {
                                $uiState.statusMessage = 'No related worker reports were found.'
                            } else {
                                $uiState.pendingAction = 'WorkerReportPicker'
                                $uiState.pendingReportIndex = 0
                                $uiState.statusMessage = 'Choose a worker report to open.'
                            }
                        }
                        $refreshRequested = $true
                        continue
                    }
                    'Escape' {
                        $uiState.pendingAction = $null
                        $uiState.pendingWorkerId = $null
                        $uiState.statusMessage = 'Worker action prompt cancelled.'
                        $refreshRequested = $true
                        continue
                    }
                }
            }
            if ($uiState.pendingAction -eq 'WorkerReportPicker') {
                switch ($key.Key) {
                    'Enter' {
                        if ($lastStatus) {
                            $relatedRuns = @(Get-SyncFactorsMonitorWorkerRelatedRuns -Status $lastStatus -WorkerId $uiState.pendingWorkerId)
                            if ($relatedRuns.Count -gt 0) {
                                $selectedIndex = [math]::Min([math]::Max([int]$uiState.pendingReportIndex, 0), $relatedRuns.Count - 1)
                                $uiState.selectedRunIndex = [int]$relatedRuns[$selectedIndex].RunIndex
                                $uiState.pendingAction = $null
                                Invoke-SyncFactorsMonitorShortcut -Action OpenReport -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            } else {
                                $uiState.statusMessage = 'No related worker reports were found.'
                            }
                        }
                        $refreshRequested = $true
                        continue
                    }
                    'Escape' {
                        $uiState.pendingAction = 'ApplyWorkerSync'
                        $uiState.pendingReportIndex = 0
                        $uiState.statusMessage = 'Returned to worker actions.'
                        $refreshRequested = $true
                        continue
                    }
                }

                switch ($key.KeyChar) {
                    'o' {
                        if ($lastStatus) {
                            $relatedRuns = @(Get-SyncFactorsMonitorWorkerRelatedRuns -Status $lastStatus -WorkerId $uiState.pendingWorkerId)
                            if ($relatedRuns.Count -gt 0) {
                                $selectedIndex = [math]::Min([math]::Max([int]$uiState.pendingReportIndex, 0), $relatedRuns.Count - 1)
                                $uiState.selectedRunIndex = [int]$relatedRuns[$selectedIndex].RunIndex
                                $uiState.pendingAction = $null
                                Invoke-SyncFactorsMonitorShortcut -Action OpenReport -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            } else {
                                $uiState.statusMessage = 'No related worker reports were found.'
                            }
                        }
                        $refreshRequested = $true
                        continue
                    }
                    'j' {
                        $uiState.pendingReportIndex = [int]$uiState.pendingReportIndex + 1
                        $uiState.statusMessage = 'Selected next related worker report.'
                        $refreshRequested = $true
                        continue
                    }
                    'k' {
                        $uiState.pendingReportIndex = [math]::Max([int]$uiState.pendingReportIndex - 1, 0)
                        $uiState.statusMessage = 'Selected previous related worker report.'
                        $refreshRequested = $true
                        continue
                    }
                }
            }
            if ("$($uiState.viewMode)" -eq 'ReportExplorer') {
                switch ($key.Key) {
                    'Escape' {
                        $uiState.viewMode = 'Dashboard'
                        $uiState.statusMessage = 'Returned to dashboard.'
                        $refreshRequested = $true
                        continue
                    }
                }

                switch ($key.KeyChar) {
                    'o' {
                        $uiState.viewMode = 'Dashboard'
                        $uiState.statusMessage = 'Returned to dashboard.'
                        $refreshRequested = $true
                        continue
                    }
                    'j' {
                        $uiState.reportEntryIndex = [int]$uiState.reportEntryIndex + 1
                        $uiState.statusMessage = 'Selected next report object.'
                        $refreshRequested = $true
                        continue
                    }
                    'k' {
                        $uiState.reportEntryIndex = [math]::Max([int]$uiState.reportEntryIndex - 1, 0)
                        $uiState.statusMessage = 'Selected previous report object.'
                        $refreshRequested = $true
                        continue
                    }
                    '[' {
                        $uiState.reportCategoryIndex = [math]::Max([int]$uiState.reportCategoryIndex - 1, 0)
                        $uiState.reportEntryIndex = 0
                        $uiState.statusMessage = 'Selected previous report category.'
                        $refreshRequested = $true
                        continue
                    }
                    ']' {
                        $uiState.reportCategoryIndex = [int]$uiState.reportCategoryIndex + 1
                        $uiState.reportEntryIndex = 0
                        $uiState.statusMessage = 'Selected next report category.'
                        $refreshRequested = $true
                        continue
                    }
                }
            }
            switch ($key.Key) {
                'Q' {
                    $quitRequested = $true
                    break
                }
                'R' {
                    $refreshRequested = $true
                    break
                }
                'T' {
                    $uiState.autoRefreshEnabled = -not $uiState.autoRefreshEnabled
                    $uiState.statusMessage = if ($uiState.autoRefreshEnabled) {
                        "Auto-refresh resumed. Refreshing every $RefreshIntervalSeconds seconds."
                    } else {
                        'Auto-refresh paused. Press t to resume or r to refresh once.'
                    }
                    $refreshRequested = $true
                    break
                }
                'UpArrow' {
                    $uiState.selectedRunIndex = [math]::Max([int]$uiState.selectedRunIndex - 1, 0)
                    $uiState.selectedItemIndex = 0
                    $uiState.focus = 'History'
                    $uiState.statusMessage = 'Selected previous run.'
                    $refreshRequested = $true
                    break
                }
                'DownArrow' {
                    $maxRunIndex = if ($lastStatus) { [math]::Max(@($lastStatus.recentRuns).Count - 1, 0) } else { 0 }
                    $uiState.selectedRunIndex = [math]::Min([int]$uiState.selectedRunIndex + 1, $maxRunIndex)
                    $uiState.selectedItemIndex = 0
                    $uiState.focus = 'History'
                    $uiState.statusMessage = 'Selected next run.'
                    $refreshRequested = $true
                    break
                }
                'LeftArrow' {
                    $uiState.selectedItemIndex = [math]::Max([int]$uiState.selectedItemIndex - 1, 0)
                    $uiState.focus = 'Detail'
                    $uiState.statusMessage = 'Selected previous object.'
                    $refreshRequested = $true
                    break
                }
                'RightArrow' {
                    $maxItemIndex = 0
                    if ($lastStatus) {
                        $bucketSelection = Get-SyncFactorsMonitorSelectedBucket -Status $lastStatus -UiState $uiState
                        $maxItemIndex = [math]::Max(@(Get-SyncFactorsMonitorFilteredBucketItems -BucketSelection $bucketSelection -UiState $uiState).Count - 1, 0)
                    }
                    $uiState.selectedItemIndex = [math]::Min([int]$uiState.selectedItemIndex + 1, $maxItemIndex)
                    $uiState.focus = 'Detail'
                    $uiState.statusMessage = 'Selected next object.'
                    $refreshRequested = $true
                    break
                }
                'Tab' {
                    $focusOrder = @('Overview', 'History', 'Detail')
                    $currentIndex = [array]::IndexOf($focusOrder, $uiState.focus)
                    if ($currentIndex -lt 0) {
                        $currentIndex = 0
                    }
                    $uiState.focus = $focusOrder[($currentIndex + 1) % $focusOrder.Count]
                    $uiState.statusMessage = "Focus: $($uiState.focus)"
                    $refreshRequested = $true
                    break
                }
                'Enter' {
                    $uiState.focus = 'Detail'
                    $uiState.statusMessage = 'Inspecting selected run details.'
                    $refreshRequested = $true
                    break
                }
                default {
                    switch ($key.KeyChar) {
                        'j' {
                            $maxRunIndex = if ($lastStatus) { [math]::Max(@($lastStatus.recentRuns).Count - 1, 0) } else { 0 }
                            $uiState.selectedRunIndex = [math]::Min([int]$uiState.selectedRunIndex + 1, $maxRunIndex)
                            $uiState.selectedItemIndex = 0
                            $uiState.focus = 'History'
                            $uiState.statusMessage = 'Selected next run.'
                            $refreshRequested = $true
                            break
                        }
                        'k' {
                            $uiState.selectedRunIndex = [math]::Max([int]$uiState.selectedRunIndex - 1, 0)
                            $uiState.selectedItemIndex = 0
                            $uiState.focus = 'History'
                            $uiState.statusMessage = 'Selected previous run.'
                            $refreshRequested = $true
                            break
                        }
                        'h' {
                            $uiState.selectedItemIndex = [math]::Max([int]$uiState.selectedItemIndex - 1, 0)
                            $uiState.focus = 'Detail'
                            $uiState.statusMessage = 'Selected previous object.'
                            $refreshRequested = $true
                            break
                        }
                        'l' {
                            $maxItemIndex = 0
                            if ($lastStatus) {
                                $bucketSelection = Get-SyncFactorsMonitorSelectedBucket -Status $lastStatus -UiState $uiState
                                $maxItemIndex = [math]::Max(@(Get-SyncFactorsMonitorFilteredBucketItems -BucketSelection $bucketSelection -UiState $uiState).Count - 1, 0)
                            }
                            $uiState.selectedItemIndex = [math]::Min([int]$uiState.selectedItemIndex + 1, $maxItemIndex)
                            $uiState.focus = 'Detail'
                            $uiState.statusMessage = 'Selected next object.'
                            $refreshRequested = $true
                            break
                        }
                        '[' {
                            $uiState.selectedBucketIndex = [math]::Max([int]$uiState.selectedBucketIndex - 1, 0)
                            $uiState.selectedItemIndex = 0
                            $uiState.focus = 'Detail'
                            $uiState.statusMessage = 'Selected previous detail bucket.'
                            $refreshRequested = $true
                            break
                        }
                        ']' {
                            $bucketMode = if ($lastStatus) { (Get-SyncFactorsMonitorSelectedRun -Status $lastStatus -UiState $uiState).mode } else { $null }
                            $maxBucketIndex = [math]::Max(@(Get-SyncFactorsMonitorBucketDefinitions -Mode $bucketMode).Count - 1, 0)
                            $uiState.selectedBucketIndex = [math]::Min([int]$uiState.selectedBucketIndex + 1, $maxBucketIndex)
                            $uiState.selectedItemIndex = 0
                            $uiState.focus = 'Detail'
                            $uiState.statusMessage = 'Selected next detail bucket.'
                            $refreshRequested = $true
                            break
                        }
                        'p' {
                            if ($lastStatus) {
                                Invoke-SyncFactorsMonitorShortcut -Action Preflight -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'd' {
                            if ($lastStatus) {
                                Invoke-SyncFactorsMonitorShortcut -Action DeltaDryRun -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        's' {
                            if ($lastStatus) {
                                Invoke-SyncFactorsMonitorShortcut -Action DeltaRun -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'f' {
                            if ($lastStatus) {
                                Invoke-SyncFactorsMonitorShortcut -Action FullDryRun -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'a' {
                            if ($lastStatus) {
                                Invoke-SyncFactorsMonitorShortcut -Action FullRun -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'w' {
                            if ($lastStatus) {
                                Invoke-SyncFactorsMonitorShortcut -Action WorkerPreview -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'z' {
                            if ($lastStatus) {
                                Invoke-SyncFactorsMonitorShortcut -Action FreshSyncReset -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'o' {
                            if ($lastStatus) {
                                Invoke-SyncFactorsMonitorShortcut -Action OpenReport -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'v' {
                            if ($lastStatus) {
                                Invoke-SyncFactorsMonitorShortcut -Action ReviewRun -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'g' {
                            if ($lastStatus) {
                                Open-SyncFactorsMonitorSelectedWorkerPreview -Status $lastStatus -UiState $uiState
                            }
                            $refreshRequested = $true
                            break
                        }
                        'y' {
                            if ($lastStatus) {
                                Invoke-SyncFactorsMonitorShortcut -Action CopyReportPath -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        '/' {
                            $uiState.focus = 'Detail'
                            $filterText = Read-SyncFactorsMonitorFilterText -CurrentFilter $uiState.filterText
                            $uiState.filterText = if ([string]::IsNullOrWhiteSpace($filterText)) { '' } else { $filterText.Trim() }
                            $uiState.selectedItemIndex = 0
                            if ([string]::IsNullOrWhiteSpace($uiState.filterText)) {
                                $uiState.statusMessage = 'Cleared detail filter.'
                            } else {
                                $uiState.statusMessage = "Filter applied: $($uiState.filterText)"
                            }
                            $refreshRequested = $true
                            break
                        }
                        'c' {
                            $uiState.filterText = ''
                            $uiState.selectedItemIndex = 0
                            $uiState.statusMessage = 'Cleared detail filter.'
                            $refreshRequested = $true
                            break
                        }
                        'x' {
                            if ($lastStatus) {
                                Export-SyncFactorsMonitorBucketSelection -Status $lastStatus -UiState $uiState
                            }
                            $refreshRequested = $true
                            break
                        }
                    }
                }
            }
        }

        if ($quitRequested -or $refreshRequested) {
            break
        }

        Start-Sleep -Seconds 1
        if ($uiState.autoRefreshEnabled) {
            $elapsedSeconds += 1
            if ($elapsedSeconds -ge $RefreshIntervalSeconds) {
                $refreshRequested = $true
            }
        }
    }
} while (-not $quitRequested)
