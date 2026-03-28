[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [Parameter(Mandatory)]
    [string]$MappingConfigPath,
    [Parameter(Mandatory)]
    [string]$WorkerId,
    [ValidateSet('Configured','Minimal','Full')]
    [string]$PreviewMode = 'Configured',
    [string]$OutputDirectory,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SyncFactorsWorkerPreviewEntries {
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

function Get-SyncFactorsWorkerPreviewChangedAttributes {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Entries
    )

    foreach ($entry in $Entries) {
        if ($entry.item.PSObject.Properties.Name -contains 'changedAttributeDetails') {
            return @($entry.item.changedAttributeDetails)
        }

        if ($entry.item.PSObject.Properties.Name -contains 'attributeRows') {
            return @($entry.item.attributeRows | Where-Object { $_.changed })
        }
    }

    return @()
}

function Get-SyncFactorsWorkerPreviewValue {
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
            if ($null -eq $value) {
                continue
            }

            if ($value -is [string]) {
                if (-not [string]::IsNullOrWhiteSpace($value)) {
                    return $value
                }
                continue
            }

            if ($value -is [System.Array]) {
                if (@($value).Count -gt 0) {
                    return $value
                }
                continue
            }

            if ($value -is [System.ValueType]) {
                return $value
            }

            if ($value -is [System.Collections.IEnumerable]) {
                if (@($value).Count -gt 0) {
                    return $value
                }
                continue
            }

            if ($value.PSObject -and @($value.PSObject.Properties).Count -gt 0) {
                return $value
            }
        }
    }

    return $null
}

function ConvertTo-SyncFactorsWorkerPreviewInlineText {
    param($Value)

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace("$Value")) {
        return '(unset)'
    }

    if ($Value -is [System.Array]) {
        return (@($Value) | ForEach-Object { ConvertTo-SyncFactorsWorkerPreviewInlineText -Value $_ }) -join ', '
    }

    $properties = @($Value.PSObject.Properties)
    if ($properties.Count -gt 0 -and -not ($Value -is [string])) {
        return (($properties | ForEach-Object { "$($_.Name)=$(ConvertTo-SyncFactorsWorkerPreviewInlineText -Value $_.Value)" }) -join '; ')
    }

    return "$Value"
}

function Get-SyncFactorsWorkerPreviewOperationLines {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Operations
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($operation in $Operations) {
        $details = [System.Collections.Generic.List[string]]::new()
        $beforeMap = @{}
        foreach ($property in @($operation.before.PSObject.Properties)) {
            $beforeMap[$property.Name] = ConvertTo-SyncFactorsWorkerPreviewInlineText -Value $property.Value
        }

        $afterMap = @{}
        foreach ($property in @($operation.after.PSObject.Properties)) {
            $afterMap[$property.Name] = ConvertTo-SyncFactorsWorkerPreviewInlineText -Value $property.Value
        }

        foreach ($key in @($beforeMap.Keys + $afterMap.Keys | Sort-Object -Unique)) {
            $beforeValue = if ($beforeMap.ContainsKey($key)) { $beforeMap[$key] } else { '(unset)' }
            $afterValue = if ($afterMap.ContainsKey($key)) { $afterMap[$key] } else { '(unset)' }
            $details.Add("${key}: $beforeValue -> $afterValue")
        }

        if ($details.Count -eq 0) {
            $lines.Add("- $($operation.operationType)")
        } else {
            $lines.Add("- $($operation.operationType): $($details -join '; ')")
        }
    }

    return @($lines)
}

function Get-SyncFactorsWorkerPreviewOperatorActionLine {
    param($Action)

    if ($null -eq $Action) {
        return '- Action'
    }

    $label = if ($Action.PSObject.Properties.Name -contains 'label' -and -not [string]::IsNullOrWhiteSpace("$($Action.label)")) {
        "$($Action.label)"
    } elseif ($Action.PSObject.Properties.Name -contains 'code' -and -not [string]::IsNullOrWhiteSpace("$($Action.code)")) {
        "$($Action.code)"
    } elseif ($Action -is [string] -and -not [string]::IsNullOrWhiteSpace($Action)) {
        $Action
    } else {
        'Action'
    }

    $description = if ($Action.PSObject.Properties.Name -contains 'description' -and -not [string]::IsNullOrWhiteSpace("$($Action.description)")) {
        "$($Action.description)"
    } else {
        ''
    }

    if ([string]::IsNullOrWhiteSpace($description)) {
        return "- $label"
    }

    return ("- {0}: {1}" -f $label, $description)
}

$projectRoot = Split-Path -Path $PSScriptRoot -Parent
$moduleRoot = Join-Path -Path $projectRoot -ChildPath 'src/Modules/SyncFactors'
$configModule = Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking -PassThru
$monitoringModule = Import-Module (Join-Path $moduleRoot 'Monitoring.psm1') -Force -DisableNameChecking -PassThru
$persistenceModule = Import-Module (Join-Path $moduleRoot 'Persistence.psm1') -Force -DisableNameChecking -PassThru

$getSyncFactorsConfig = $configModule.ExportedFunctions['Get-SyncFactorsConfig']
$getSyncFactorsSuccessFactorsAuthSummary = $configModule.ExportedFunctions['Get-SyncFactorsSuccessFactorsAuthSummary']
$getSyncFactorsRuntimeStatusSnapshot = $monitoringModule.ExportedFunctions['Get-SyncFactorsRuntimeStatusSnapshot']
$newSyncFactorsReportReference = $persistenceModule.ExportedFunctions['New-SyncFactorsReportReference']
$getSyncFactorsReportFromReference = $persistenceModule.ExportedFunctions['Get-SyncFactorsReportFromReference']

$resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
$resolvedMappingConfigPath = (Resolve-Path -Path $MappingConfigPath).Path
$effectiveConfigPath = $resolvedConfigPath
$resolvedConfig = & $getSyncFactorsConfig -Path $resolvedConfigPath
$successFactorsAuth = & $getSyncFactorsSuccessFactorsAuthSummary -Config $resolvedConfig
$config = $resolvedConfig

switch ($PreviewMode) {
    'Full' {
        if ($config.successFactors.PSObject.Properties.Name -contains 'previewQuery') {
            $config.successFactors.PSObject.Properties.Remove('previewQuery')
        }
    }
    'Minimal' {
        if (-not ($config.successFactors.PSObject.Properties.Name -contains 'previewQuery') -or $null -eq $config.successFactors.previewQuery) {
            $config.successFactors | Add-Member -MemberType NoteProperty -Name 'previewQuery' -Value ([pscustomobject]@{
                    select = @(
                        $config.successFactors.query.identityField
                    )
                    expand = @()
                }) -Force
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $config.reporting.reviewOutputDirectory = (Resolve-Path -Path (New-Item -Path $OutputDirectory -ItemType Directory -Force)).Path
}

if ($PreviewMode -ne 'Configured' -or -not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $overlayPath = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ("syncfactors-worker-preview-config-{0}.json" -f ([guid]::NewGuid().Guid))
    try {
        $config | ConvertTo-Json -Depth 30 | Set-Content -Path $overlayPath
        $effectiveConfigPath = $overlayPath
    } catch {
        if (Test-Path -Path $overlayPath -PathType Leaf) {
            Remove-Item -Path $overlayPath -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

if ($PreviewMode -eq 'Minimal') {
    $worker = Get-SfWorkerById -Config $config -WorkerId $WorkerId
    if (-not $worker) {
        throw "Worker '$WorkerId' was not found in SuccessFactors using identity field '$($config.successFactors.query.identityField)'."
    }

    $rawPropertyNames = @($worker.PSObject.Properties | ForEach-Object { $_.Name } | Sort-Object)
    $result = [pscustomobject]@{
        reportPath = $null
        runId = $null
        mode = 'Review'
        status = 'Succeeded'
        artifactType = 'WorkerFetchPreview'
        successFactorsAuth = $successFactorsAuth
        previewMode = $PreviewMode.ToLowerInvariant()
        workerScope = [pscustomobject]@{
            identityField = $config.successFactors.query.identityField
            workerId = $WorkerId
        }
        reviewSummary = $null
        preview = [pscustomobject]@{
            workerId = $WorkerId
            buckets = @()
            matchedExistingUser = $null
            reviewCategory = $null
            reason = 'MinimalPreview'
            samAccountName = $null
            targetOu = $null
            currentDistinguishedName = $null
            currentEnabled = $null
            proposedEnable = $null
        }
        changedAttributes = @()
        operations = @()
        entries = @()
        rawWorker = $worker
        rawPropertyNames = $rawPropertyNames
    }

    if ($AsJson) {
        $result | ConvertTo-Json -Depth 20
        return
    }

    Write-Host 'SuccessFactors Worker Preview'
    Write-Host 'Artifact: WorkerFetchPreview'
    Write-Host "Status: $($result.status)"
    Write-Host "SuccessFactors auth: $($result.successFactorsAuth)"
    Write-Host "Preview mode: $($result.previewMode)"
    Write-Host "Worker: $WorkerId"
    Write-Host "Identity field: $($result.workerScope.identityField)"
    Write-Host "Returned properties: $(if ($rawPropertyNames.Count -gt 0) { $rawPropertyNames -join ', ' } else { '(none)' })"
    Write-Host ''
    Write-Host 'Raw Worker'
    $worker | ConvertTo-Json -Depth 20
    return
}

$invokePath = Join-Path -Path $projectRoot -ChildPath 'src/Invoke-SyncFactors.ps1'
$reportPath = $null
$report = $null
$errorMessage = $null

try {
    $reportPath = & $invokePath -ConfigPath $effectiveConfigPath -MappingConfigPath $resolvedMappingConfigPath -Mode Review -WorkerId $WorkerId
    $report = & $getSyncFactorsReportFromReference -Reference $reportPath -StatePath $config.state.path
} catch {
    $errorMessage = $_.Exception.Message
    $runtimeStatus = & $getSyncFactorsRuntimeStatusSnapshot -StatePath $config.state.path
    if (
        $runtimeStatus -and
        $runtimeStatus.PSObject.Properties.Name -contains 'runId' -and
        -not [string]::IsNullOrWhiteSpace("$($runtimeStatus.runId)")
    ) {
        $reportPath = & $newSyncFactorsReportReference -RunId "$($runtimeStatus.runId)"
        $report = & $getSyncFactorsReportFromReference -Reference $reportPath -StatePath $config.state.path
    }

    if (-not $AsJson) {
        throw
    }
}

$entries = @(if ($report) { Get-SyncFactorsWorkerPreviewEntries -Report $report -WorkerId $WorkerId } else { @() })
$changedAttributes = @(Get-SyncFactorsWorkerPreviewChangedAttributes -Entries $entries)
$operations = @(if ($report -and $report.PSObject.Properties.Name -contains 'operations') { $report.operations | Where-Object { "$($_.workerId)" -eq $WorkerId } } else { @() })
$bucketNames = @($entries | ForEach-Object { $_.bucket } | Select-Object -Unique)
$matchedExistingUser = if (@($entries | Where-Object { $_.item.PSObject.Properties.Name -contains 'matchedExistingUser' -and [bool]$_.item.matchedExistingUser }).Count -gt 0) {
    $true
} elseif (@($bucketNames | Where-Object { $_ -in @('updates', 'unchanged', 'enables', 'disables', 'graveyardMoves', 'deletions') }).Count -gt 0) {
    $true
} elseif (@($bucketNames | Where-Object { $_ -eq 'creates' }).Count -gt 0) {
    $false
} else {
    $null
}

$result = [pscustomobject]@{
    reportPath = $reportPath
    runId = if ($report -and $report.PSObject.Properties.Name -contains 'runId') { $report.runId } else { $null }
    mode = if ($report -and $report.PSObject.Properties.Name -contains 'mode') { $report.mode } else { 'Review' }
    status = if ($report -and $report.PSObject.Properties.Name -contains 'status') { $report.status } else { 'Failed' }
    errorMessage = if ($report -and $report.PSObject.Properties.Name -contains 'errorMessage' -and -not [string]::IsNullOrWhiteSpace("$($report.errorMessage)")) { $report.errorMessage } else { $errorMessage }
    artifactType = if ($report -and $report.PSObject.Properties.Name -contains 'artifactType') { $report.artifactType } else { 'WorkerPreview' }
    successFactorsAuth = $successFactorsAuth
    previewMode = $PreviewMode.ToLowerInvariant()
    workerScope = if ($report) { $report.workerScope } else { [pscustomobject]@{ identityField = $config.successFactors.query.identityField; workerId = $WorkerId } }
    reviewSummary = if ($report) { $report.reviewSummary } else { $null }
    preview = [pscustomobject]@{
        workerId = $WorkerId
        buckets = $bucketNames
        matchedExistingUser = $matchedExistingUser
        reviewCategory = Get-SyncFactorsWorkerPreviewValue -Entries $entries -PropertyName 'reviewCategory'
        reviewCaseType = Get-SyncFactorsWorkerPreviewValue -Entries $entries -PropertyName 'reviewCaseType'
        reason = Get-SyncFactorsWorkerPreviewValue -Entries $entries -PropertyName 'reason'
        operatorActionSummary = Get-SyncFactorsWorkerPreviewValue -Entries $entries -PropertyName 'operatorActionSummary'
        operatorActions = @(Get-SyncFactorsWorkerPreviewValue -Entries $entries -PropertyName 'operatorActions')
        samAccountName = Get-SyncFactorsWorkerPreviewValue -Entries $entries -PropertyName 'samAccountName'
        targetOu = Get-SyncFactorsWorkerPreviewValue -Entries $entries -PropertyName 'targetOu'
        currentDistinguishedName = Get-SyncFactorsWorkerPreviewValue -Entries $entries -PropertyName 'currentDistinguishedName'
        currentEnabled = Get-SyncFactorsWorkerPreviewValue -Entries $entries -PropertyName 'currentEnabled'
        proposedEnable = Get-SyncFactorsWorkerPreviewValue -Entries $entries -PropertyName 'proposedEnable'
    }
    changedAttributes = $changedAttributes
    operations = $operations
    entries = @($entries | ForEach-Object {
            [pscustomobject]@{
                bucket = $_.bucket
                item = $_.item
            }
        })
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 20
    return
}

Write-Host 'SuccessFactors Worker Preview'
Write-Host "Report: $($result.reportPath)"
Write-Host "Run ID: $($result.runId)"
Write-Host "Status: $($result.status)"
Write-Host "SuccessFactors auth: $($result.successFactorsAuth)"
Write-Host "Preview mode: $($result.previewMode)"
Write-Host "Worker: $WorkerId"
if ($result.workerScope) {
    Write-Host "Identity field: $($result.workerScope.identityField)"
}
Write-Host "Artifact: $($result.artifactType)"
Write-Host "Buckets: $(if ($bucketNames.Count -gt 0) { $bucketNames -join ', ' } else { 'none' })"
if ($null -ne $result.preview.matchedExistingUser) {
    Write-Host "Matched existing user: $($result.preview.matchedExistingUser)"
}
if ($result.preview.reviewCategory) {
    Write-Host "Category: $($result.preview.reviewCategory)"
}
if ($result.preview.reviewCaseType) {
    Write-Host "Review case: $($result.preview.reviewCaseType)"
}
if ($result.preview.reason) {
    Write-Host "Reason: $($result.preview.reason)"
}
if ($result.preview.operatorActionSummary) {
    Write-Host "Operator summary: $($result.preview.operatorActionSummary)"
}
if ($result.preview.samAccountName) {
    Write-Host "SamAccountName: $($result.preview.samAccountName)"
}
if ($result.preview.targetOu) {
    Write-Host "Target OU: $($result.preview.targetOu)"
}
if ($result.preview.currentDistinguishedName) {
    Write-Host "Current DN: $($result.preview.currentDistinguishedName)"
}
if ($null -ne $result.preview.currentEnabled) {
    Write-Host "Current enabled: $($result.preview.currentEnabled)"
}
if ($null -ne $result.preview.proposedEnable) {
    Write-Host "Proposed enable: $($result.preview.proposedEnable)"
}

Write-Host ''
Write-Host 'Operator Actions'
if (@($result.preview.operatorActions).Count -eq 0) {
    Write-Host 'none'
} else {
    foreach ($action in @($result.preview.operatorActions)) {
        Write-Host (Get-SyncFactorsWorkerPreviewOperatorActionLine -Action $action)
    }
}

Write-Host ''
Write-Host 'Changed Attributes'
if ($changedAttributes.Count -eq 0) {
    Write-Host 'none'
} else {
    foreach ($row in $changedAttributes) {
        Write-Host ("- {0}: {1} -> {2}" -f `
                $row.targetAttribute, `
                (ConvertTo-SyncFactorsWorkerPreviewInlineText -Value $row.currentAdValue), `
                (ConvertTo-SyncFactorsWorkerPreviewInlineText -Value $row.proposedValue))
    }
}

Write-Host ''
Write-Host 'Operations'
$operationLines = @(Get-SyncFactorsWorkerPreviewOperationLines -Operations $operations)
if ($operationLines.Count -eq 0) {
    Write-Host 'none'
} else {
    foreach ($line in $operationLines) {
        Write-Host $line
    }
}
