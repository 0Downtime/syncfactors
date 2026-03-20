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

$moduleRoot = Join-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -ChildPath 'src/Modules/SfAdSync'
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force
Import-Module (Join-Path $moduleRoot 'State.psm1') -Force
Import-Module (Join-Path $moduleRoot 'Monitoring.psm1') -Force

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

function Show-SfAdMonitorFrame {
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
        $status = Get-SfAdMonitorStatus -ConfigPath $ResolvedConfigPath -HistoryLimit $HistoryDepth
        $rawLines = if ($AsTextOutput) {
            @(Format-SfAdMonitorView -Status $status)
        } else {
            @(Format-SfAdMonitorDashboardView -Status $status -UiState $UiState)
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
    Write-SfAdStyledMonitorFrame -Lines $lines

    return [pscustomobject]@{
        Status = $status
        Lines = $lines
    }
}

function Write-SfAdStyledMonitorFrame {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
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

function Read-SfAdMonitorFilterText {
    [CmdletBinding()]
    param(
        [string]$CurrentFilter
    )

    Write-Host ''
    Write-Host "Filter current bucket entries. Leave blank to clear the filter." -ForegroundColor Cyan
    $prompt = if ([string]::IsNullOrWhiteSpace($CurrentFilter)) { 'Filter' } else { "Filter [$CurrentFilter]" }
    return Read-Host -Prompt $prompt
}

function Read-SfAdMonitorWorkerId {
    [CmdletBinding()]
    param()

    Write-Host ''
    Write-Host 'Start a single-worker preview by SuccessFactors identity field value.' -ForegroundColor Cyan
    return Read-Host -Prompt 'WorkerId'
}

function Read-SfAdMonitorWorkerPreviewMode {
    [CmdletBinding()]
    param()

    Write-Host 'Choose query scope for the worker preview. Enter minimal or full.' -ForegroundColor Cyan
    $response = Read-Host -Prompt 'PreviewMode [minimal]'
    if ([string]::IsNullOrWhiteSpace($response)) {
        return 'Minimal'
    }

    $normalized = "$response".Trim().ToLowerInvariant()
    switch ($normalized) {
        'm' { return 'Minimal' }
        'minimal' { return 'Minimal' }
        'f' { return 'Full' }
        'full' { return 'Full' }
        default { return $null }
    }
}

function Confirm-SfAdMonitorWriteAction {
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

function Export-SfAdMonitorBucketSelection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState
    )

    $bucketSelection = Get-SfAdMonitorSelectedBucket -Status $Status -UiState $UiState
    $items = @(Get-SfAdMonitorFilteredBucketItems -BucketSelection $bucketSelection -UiState $UiState)
    if ($items.Count -eq 0) {
        $UiState.statusMessage = 'Export skipped: no filtered bucket entries to export.'
        $UiState.commandOutput = @()
        return
    }

    if (-not (Confirm-SfAdMonitorWriteAction -Label 'Bucket export' -WriteTarget 'a JSON file in the temp directory')) {
        $UiState.statusMessage = 'Bucket export cancelled.'
        $UiState.commandOutput = @()
        return
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $path = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath "sf-ad-sync-monitor-$($bucketSelection.Bucket.Name)-$timestamp.json"
    $items | ConvertTo-Json -Depth 20 | Set-Content -Path $path
    $UiState.statusMessage = "Exported filtered bucket to $path"
    $UiState.commandOutput = @($path)
}

function Invoke-SfAdMonitorShortcut {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Preflight','DeltaDryRun','DeltaRun','FullDryRun','FullRun','ReviewRun','WorkerPreview','ApplyReviewedWorker','FreshSyncReset','OpenReport','CopyReportPath')]
        [string]$Action,
        [Parameter(Mandatory)]
        [pscustomobject]$Status,
        [Parameter(Mandatory)]
        [pscustomobject]$UiState,
        [string]$ResolvedMappingConfigPath
    )

    $context = Get-SfAdMonitorActionContext -Status $Status -UiState $UiState -MappingConfigPath $ResolvedMappingConfigPath
    $projectRoot = Split-Path -Path $PSScriptRoot -Parent

    switch ($Action) {
        'Preflight' {
            if (-not $context.mappingConfigPath) {
                $UiState.statusMessage = 'Preflight unavailable: no mapping config path was provided and none could be inferred from recent runs.'
                $UiState.commandOutput = @()
                return
            }

            try {
                $output = & pwsh -NoLogo -NoProfile -File (Join-Path $projectRoot 'scripts/Invoke-SfAdPreflight.ps1') -ConfigPath $context.configPath -MappingConfigPath $context.mappingConfigPath 2>&1
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

            if (-not (Confirm-SfAdMonitorWriteAction -Label 'Delta dry-run' -WriteTarget 'runtime status and report files')) {
                $UiState.statusMessage = 'Delta dry-run cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'src/Invoke-SfAdSync.ps1')
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

            if (-not (Confirm-SfAdMonitorWriteAction -Label 'Delta sync' -WriteTarget 'AD objects, sync state, runtime status, and report files')) {
                $UiState.statusMessage = 'Delta sync cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'src/Invoke-SfAdSync.ps1')
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

            if (-not (Confirm-SfAdMonitorWriteAction -Label 'Full dry-run' -WriteTarget 'runtime status and report files')) {
                $UiState.statusMessage = 'Full dry-run cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'src/Invoke-SfAdSync.ps1')
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

            if (-not (Confirm-SfAdMonitorWriteAction -Label 'Full sync' -WriteTarget 'AD objects, sync state, runtime status, and report files')) {
                $UiState.statusMessage = 'Full sync cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'src/Invoke-SfAdSync.ps1')
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

            if (-not (Confirm-SfAdMonitorWriteAction -Label 'First-sync review' -WriteTarget 'runtime status and review report files')) {
                $UiState.statusMessage = 'First-sync review cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'scripts/Invoke-SfAdFirstSyncReview.ps1')
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
            if (-not $context.mappingConfigPath) {
                $UiState.statusMessage = 'Worker preview unavailable: no mapping config path was provided and none could be inferred from recent runs.'
                $UiState.commandOutput = @()
                return
            }

            $workerId = Read-SfAdMonitorWorkerId
            if ([string]::IsNullOrWhiteSpace($workerId)) {
                $UiState.statusMessage = 'Worker preview cancelled: no worker ID was provided.'
                $UiState.commandOutput = @()
                return
            }

            $previewMode = Read-SfAdMonitorWorkerPreviewMode
            if ([string]::IsNullOrWhiteSpace($previewMode)) {
                $UiState.statusMessage = 'Worker preview cancelled: preview mode must be minimal or full.'
                $UiState.commandOutput = @()
                return
            }

            if (-not (Confirm-SfAdMonitorWriteAction -Label "Worker preview for $($workerId.Trim())" -WriteTarget 'runtime status and review report files')) {
                $UiState.statusMessage = 'Worker preview cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'scripts/Invoke-SfAdWorkerPreview.ps1')
                '-ConfigPath'
                $context.configPath
                '-MappingConfigPath'
                $context.mappingConfigPath
                '-WorkerId'
                $workerId.Trim()
                '-PreviewMode'
                $previewMode
            )

            try {
                Start-Process -FilePath 'pwsh' -ArgumentList $argumentList | Out-Null
                $UiState.statusMessage = "Started $($previewMode.ToLowerInvariant()) worker preview for $($workerId.Trim()) in a new PowerShell process."
                $UiState.commandOutput = @("Config=$($context.configPath)", "Mapping=$($context.mappingConfigPath)", "WorkerId=$($workerId.Trim())", "PreviewMode=$($previewMode.ToLowerInvariant())")
            } catch {
                $UiState.statusMessage = 'Failed to start worker preview.'
                $UiState.commandOutput = @($_.Exception.Message)
            }
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

            if (-not (Confirm-SfAdMonitorWriteAction -Label "Apply worker sync for $workerId" -WriteTarget 'AD and sync state')) {
                $UiState.statusMessage = 'Worker apply cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'scripts/Invoke-SfAdWorkerSync.ps1')
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
            if (-not (Confirm-SfAdMonitorWriteAction -Label 'Fresh sync reset' -WriteTarget 'managed AD user objects and local sync state')) {
                $UiState.statusMessage = 'Fresh sync reset cancelled.'
                $UiState.commandOutput = @()
                return
            }

            $argumentList = @(
                '-NoLogo'
                '-NoProfile'
                '-File'
                (Join-Path $projectRoot 'scripts/Invoke-SfAdFreshSyncReset.ps1')
                '-ConfigPath'
                $context.configPath
            )

            try {
                Start-Process -FilePath 'pwsh' -ArgumentList $argumentList | Out-Null
                $UiState.statusMessage = 'Started fresh sync reset in a new PowerShell process.'
                $UiState.commandOutput = @("Config=$($context.configPath)")
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

            if (-not (Confirm-SfAdMonitorWriteAction -Label 'Copy report path' -WriteTarget 'the clipboard')) {
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
$uiState = New-SfAdMonitorUiState
$uiState.autoRefreshEnabled = $false
$uiState.statusMessage = 'Auto-refresh paused. Press t to resume or r to refresh once.'
$lastStatus = $null

do {
    $frame = Show-SfAdMonitorFrame -ResolvedConfigPath $resolvedConfigPath -ResolvedMappingConfigPath $resolvedMappingConfigPath -HistoryDepth $HistoryLimit -UiState $uiState -AsTextOutput:$AsText
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
            if ($uiState.pendingAction -eq 'ApplyWorkerSync') {
                switch ($key.Key) {
                    'A' {
                        if ($lastStatus) {
                            Invoke-SfAdMonitorShortcut -Action ApplyReviewedWorker -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                        }
                        $uiState.pendingAction = $null
                        $uiState.pendingWorkerId = $null
                        $refreshRequested = $true
                        continue
                    }
                    'O' {
                        if ($lastStatus) {
                            Invoke-SfAdMonitorShortcut -Action OpenReport -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                        }
                        $uiState.pendingAction = $null
                        $uiState.pendingWorkerId = $null
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
                        $bucketSelection = Get-SfAdMonitorSelectedBucket -Status $lastStatus -UiState $uiState
                        $maxItemIndex = [math]::Max(@(Get-SfAdMonitorFilteredBucketItems -BucketSelection $bucketSelection -UiState $uiState).Count - 1, 0)
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
                                $bucketSelection = Get-SfAdMonitorSelectedBucket -Status $lastStatus -UiState $uiState
                                $maxItemIndex = [math]::Max(@(Get-SfAdMonitorFilteredBucketItems -BucketSelection $bucketSelection -UiState $uiState).Count - 1, 0)
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
                            $bucketMode = if ($lastStatus) { (Get-SfAdMonitorSelectedRun -Status $lastStatus -UiState $uiState).mode } else { $null }
                            $maxBucketIndex = [math]::Max(@(Get-SfAdMonitorBucketDefinitions -Mode $bucketMode).Count - 1, 0)
                            $uiState.selectedBucketIndex = [math]::Min([int]$uiState.selectedBucketIndex + 1, $maxBucketIndex)
                            $uiState.selectedItemIndex = 0
                            $uiState.focus = 'Detail'
                            $uiState.statusMessage = 'Selected next detail bucket.'
                            $refreshRequested = $true
                            break
                        }
                        'p' {
                            if ($lastStatus) {
                                Invoke-SfAdMonitorShortcut -Action Preflight -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'd' {
                            if ($lastStatus) {
                                Invoke-SfAdMonitorShortcut -Action DeltaDryRun -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        's' {
                            if ($lastStatus) {
                                Invoke-SfAdMonitorShortcut -Action DeltaRun -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'f' {
                            if ($lastStatus) {
                                Invoke-SfAdMonitorShortcut -Action FullDryRun -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'a' {
                            if ($lastStatus) {
                                Invoke-SfAdMonitorShortcut -Action FullRun -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'w' {
                            if ($lastStatus) {
                                Invoke-SfAdMonitorShortcut -Action WorkerPreview -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'z' {
                            if ($lastStatus) {
                                Invoke-SfAdMonitorShortcut -Action FreshSyncReset -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'o' {
                            if ($lastStatus) {
                                Invoke-SfAdMonitorShortcut -Action OpenReport -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'v' {
                            if ($lastStatus) {
                                Invoke-SfAdMonitorShortcut -Action ReviewRun -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        'g' {
                            if ($lastStatus -and (Test-SfAdMonitorSelectedRunIsWorkerPreview -Status $lastStatus -UiState $uiState)) {
                                $workerId = Get-SfAdMonitorSelectedRunWorkerId -Status $lastStatus -UiState $uiState
                                if (-not [string]::IsNullOrWhiteSpace($workerId)) {
                                    $uiState.pendingAction = 'ApplyWorkerSync'
                                    $uiState.pendingWorkerId = $workerId
                                    $uiState.statusMessage = "Choose an action for reviewed worker $workerId."
                                } else {
                                    $uiState.statusMessage = 'Worker apply unavailable: the selected review is missing worker scope.'
                                }
                            } else {
                                $uiState.statusMessage = 'Worker apply is only available on a selected single-worker review run.'
                            }
                            $refreshRequested = $true
                            break
                        }
                        'y' {
                            if ($lastStatus) {
                                Invoke-SfAdMonitorShortcut -Action CopyReportPath -Status $lastStatus -UiState $uiState -ResolvedMappingConfigPath $resolvedMappingConfigPath
                            }
                            $refreshRequested = $true
                            break
                        }
                        '/' {
                            $uiState.focus = 'Detail'
                            $filterText = Read-SfAdMonitorFilterText -CurrentFilter $uiState.filterText
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
                                Export-SfAdMonitorBucketSelection -Status $lastStatus -UiState $uiState
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
