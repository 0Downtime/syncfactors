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

function Get-SfAdWorkerPreviewEntries {
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

function Get-SfAdWorkerPreviewChangedAttributes {
    param(
        [Parameter(Mandatory)]
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

function Get-SfAdWorkerPreviewValue {
    param(
        [Parameter(Mandatory)]
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

function ConvertTo-SfAdWorkerPreviewInlineText {
    param($Value)

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace("$Value")) {
        return '(unset)'
    }

    if ($Value -is [System.Array]) {
        return (@($Value) | ForEach-Object { ConvertTo-SfAdWorkerPreviewInlineText -Value $_ }) -join ', '
    }

    $properties = @($Value.PSObject.Properties)
    if ($properties.Count -gt 0 -and -not ($Value -is [string])) {
        return (($properties | ForEach-Object { "$($_.Name)=$(ConvertTo-SfAdWorkerPreviewInlineText -Value $_.Value)" }) -join '; ')
    }

    return "$Value"
}

function Get-SfAdWorkerPreviewOperationLines {
    param(
        [Parameter(Mandatory)]
        [object[]]$Operations
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($operation in $Operations) {
        $details = [System.Collections.Generic.List[string]]::new()
        $beforeMap = @{}
        foreach ($property in @($operation.before.PSObject.Properties)) {
            $beforeMap[$property.Name] = ConvertTo-SfAdWorkerPreviewInlineText -Value $property.Value
        }

        $afterMap = @{}
        foreach ($property in @($operation.after.PSObject.Properties)) {
            $afterMap[$property.Name] = ConvertTo-SfAdWorkerPreviewInlineText -Value $property.Value
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

$projectRoot = Split-Path -Path $PSScriptRoot -Parent
$moduleRoot = Join-Path -Path $projectRoot -ChildPath 'src/Modules/SfAdSync'
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking

$resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
$resolvedMappingConfigPath = (Resolve-Path -Path $MappingConfigPath).Path
$effectiveConfigPath = $resolvedConfigPath
$resolvedConfig = Get-SfAdSyncConfig -Path $resolvedConfigPath
$successFactorsAuth = Get-SfAdSuccessFactorsAuthSummary -Config $resolvedConfig
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
                        $config.successFactors.query.identityField,
                        'firstName',
                        'lastName'
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
    $overlayPath = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ("sf-ad-sync-worker-preview-config-{0}.json" -f ([guid]::NewGuid().Guid))
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

$invokePath = Join-Path -Path $projectRoot -ChildPath 'src/Invoke-SfAdSync.ps1'
$reportPath = & $invokePath -ConfigPath $effectiveConfigPath -MappingConfigPath $resolvedMappingConfigPath -Mode Review -WorkerId $WorkerId
$report = Get-Content -Path $reportPath -Raw | ConvertFrom-Json -Depth 30
$entries = @(Get-SfAdWorkerPreviewEntries -Report $report -WorkerId $WorkerId)
$changedAttributes = @(Get-SfAdWorkerPreviewChangedAttributes -Entries $entries)
$operations = @($report.operations | Where-Object { "$($_.workerId)" -eq $WorkerId })
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
    runId = $report.runId
    mode = $report.mode
    status = $report.status
    artifactType = $report.artifactType
    successFactorsAuth = $successFactorsAuth
    previewMode = $PreviewMode.ToLowerInvariant()
    workerScope = $report.workerScope
    reviewSummary = $report.reviewSummary
    preview = [pscustomobject]@{
        workerId = $WorkerId
        buckets = $bucketNames
        matchedExistingUser = $matchedExistingUser
        reviewCategory = Get-SfAdWorkerPreviewValue -Entries $entries -PropertyName 'reviewCategory'
        reason = Get-SfAdWorkerPreviewValue -Entries $entries -PropertyName 'reason'
        samAccountName = Get-SfAdWorkerPreviewValue -Entries $entries -PropertyName 'samAccountName'
        targetOu = Get-SfAdWorkerPreviewValue -Entries $entries -PropertyName 'targetOu'
        currentDistinguishedName = Get-SfAdWorkerPreviewValue -Entries $entries -PropertyName 'currentDistinguishedName'
        currentEnabled = Get-SfAdWorkerPreviewValue -Entries $entries -PropertyName 'currentEnabled'
        proposedEnable = Get-SfAdWorkerPreviewValue -Entries $entries -PropertyName 'proposedEnable'
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
if ($result.preview.reason) {
    Write-Host "Reason: $($result.preview.reason)"
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
Write-Host 'Changed Attributes'
if ($changedAttributes.Count -eq 0) {
    Write-Host 'none'
} else {
    foreach ($row in $changedAttributes) {
        Write-Host ("- {0}: {1} -> {2}" -f `
                $row.targetAttribute, `
                (ConvertTo-SfAdWorkerPreviewInlineText -Value $row.currentAdValue), `
                (ConvertTo-SfAdWorkerPreviewInlineText -Value $row.proposedValue))
    }
}

Write-Host ''
Write-Host 'Operations'
$operationLines = @(Get-SfAdWorkerPreviewOperationLines -Operations $operations)
if ($operationLines.Count -eq 0) {
    Write-Host 'none'
} else {
    foreach ($line in $operationLines) {
        Write-Host $line
    }
}
