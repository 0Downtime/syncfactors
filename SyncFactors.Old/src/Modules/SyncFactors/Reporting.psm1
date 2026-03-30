Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'Config.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'Alerting.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'Persistence.psm1') -Force -DisableNameChecking

function New-SyncFactorsReport {
    [CmdletBinding()]
    param(
        [string]$Mode,
        [switch]$DryRun,
        [string]$ConfigPath,
        [string]$MappingConfigPath,
        [string]$StatePath,
        [string]$ArtifactType = 'SyncReport',
        [AllowNull()]
        [object]$WorkerScope
    )

    return [ordered]@{
        runId = [guid]::NewGuid().Guid
        startedAt = (Get-Date).ToString('o')
        status = 'InProgress'
        mode = $Mode
        artifactType = $ArtifactType
        dryRun = [bool]$DryRun
        configPath = $ConfigPath
        mappingConfigPath = $MappingConfigPath
        statePath = $StatePath
        workerScope = $WorkerScope
        completedAt = $null
        failedAt = $null
        errorMessage = $null
        reviewSummary = $null
        operations = @()
        operationSequence = 0
        creates = @()
        updates = @()
        enables = @()
        disables = @()
        graveyardMoves = @()
        deletions = @()
        quarantined = @()
        conflicts = @()
        guardrailFailures = @()
        manualReview = @()
        unchanged = @()
    }
}

function Add-SyncFactorsReportEntry {
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

function Add-SyncFactorsReportOperation {
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

function Save-SyncFactorsReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [string]$Directory,
        [Parameter(Mandatory)]
        [string]$Mode
    )

    $Report['completedAt'] = (Get-Date).ToString('o')
    [void]$Report.Remove('operationSequence')
    $reportReference = Save-SyncFactorsReportToSqlite -Report $Report

    $configPath = if ($Report.Contains('configPath')) { "$($Report['configPath'])" } else { $null }
    if (-not [string]::IsNullOrWhiteSpace($configPath) -and (Test-Path -Path $configPath -PathType Leaf)) {
        try {
            $config = Get-SyncFactorsConfig -Path $configPath
            [void](Send-SyncFactorsRunAlert -Config $config -Report ([pscustomobject]$Report) -ReportReference $reportReference)
        } catch {
            Write-Warning "SyncFactors alert delivery failed: $($_.Exception.Message)"
        }
    }

    return $reportReference
}

Export-ModuleMember -Function New-SyncFactorsReport, Add-SyncFactorsReportEntry, Add-SyncFactorsReportOperation, Save-SyncFactorsReport
