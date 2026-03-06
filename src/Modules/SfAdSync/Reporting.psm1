Set-StrictMode -Version Latest

function New-SfAdSyncReport {
    [CmdletBinding()]
    param(
        [string]$Mode,
        [switch]$DryRun,
        [string]$ConfigPath,
        [string]$MappingConfigPath,
        [string]$StatePath
    )

    return [ordered]@{
        runId = [guid]::NewGuid().Guid
        startedAt = (Get-Date).ToString('o')
        mode = $Mode
        dryRun = [bool]$DryRun
        configPath = $ConfigPath
        mappingConfigPath = $MappingConfigPath
        statePath = $StatePath
        operations = @()
        operationSequence = 0
        creates = @()
        updates = @()
        enables = @()
        disables = @()
        graveyardMoves = @()
        deletions = @()
        quarantined = @()
        manualReview = @()
        unchanged = @()
    }
}

function Add-SfAdReportEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [string]$Bucket,
        [Parameter(Mandatory)]
        [hashtable]$Entry
    )

    if (-not $Report.Contains($Bucket)) {
        throw "Unknown report bucket '$Bucket'."
    }

    $Report[$Bucket] = @($Report[$Bucket]) + [pscustomobject]$Entry
}

function Add-SfAdReportOperation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [string]$OperationType,
        [Parameter(Mandatory)]
        [string]$WorkerId,
        [string]$Bucket,
        [string]$TargetType = 'ADUser',
        [hashtable]$Target,
        [object]$Before,
        [object]$After,
        [string]$Status = 'Applied',
        [string]$ErrorMessage
    )

    $Report['operationSequence'] = [int]$Report['operationSequence'] + 1
    $entry = [pscustomobject]@{
        operationId = "$($Report['runId'])-$('{0:d4}' -f $Report['operationSequence'])"
        sequence = [int]$Report['operationSequence']
        timestamp = (Get-Date).ToString('o')
        operationType = $OperationType
        workerId = $WorkerId
        bucket = $Bucket
        targetType = $TargetType
        target = if ($Target) { [pscustomobject]$Target } else { $null }
        before = $Before
        after = $After
        status = $Status
        errorMessage = $ErrorMessage
    }

    $operations = @($Report['operations'])
    $operations += $entry
    $Report['operations'] = $operations
    return $entry
}

function Save-SfAdSyncReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [string]$Directory,
        [Parameter(Mandatory)]
        [string]$Mode
    )

    if (-not (Test-Path -Path $Directory -PathType Container)) {
        New-Item -Path $Directory -ItemType Directory -Force | Out-Null
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $path = Join-Path -Path $Directory -ChildPath "sf-ad-sync-$Mode-$timestamp.json"
    $Report['completedAt'] = (Get-Date).ToString('o')
    [void]$Report.Remove('operationSequence')
    $Report | ConvertTo-Json -Depth 20 | Set-Content -Path $path
    return $path
}

Export-ModuleMember -Function New-SfAdSyncReport, Add-SfAdReportEntry, Add-SfAdReportOperation, Save-SfAdSyncReport
