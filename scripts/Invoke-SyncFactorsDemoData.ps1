[CmdletBinding()]
param(
    [string]$ConfigPath = './config/sample.mock-successfactors.real-ad.sync-config.json',
    [string]$MappingConfigPath = './config/sample.syncfactors.mapping-config.json',
    [string]$OutputDirectory = './reports/demo',
    [ValidateSet('MixedHistory')]
    [string]$Preset = 'MixedHistory',
    [ValidateRange(10, 50000)]
    [int]$UserCount = 250,
    [ValidateRange(0, 50)]
    [int]$RunCount = 0,
    [bool]$IncludeActiveRun = $true,
    [switch]$Force,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ResolvedPathOrJoin {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path -Path $BasePath -ChildPath $Path
}

function Initialize-SyncFactorsDemoOutputDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [switch]$Force
    )

    if (-not (Test-Path -Path $Path -PathType Container)) {
        New-Item -Path $Path -ItemType Directory -Force | Out-Null
        return
    }

    $existing = @(Get-ChildItem -Path $Path -Force)
    if ($existing.Count -eq 0) {
        return
    }

    if (-not $Force) {
        throw "Output directory '$Path' already contains files. Use -Force to replace the existing demo artifacts."
    }

    foreach ($item in $existing) {
        Remove-Item -Path $item.FullName -Recurse -Force
    }
}

function Get-SyncFactorsDemoWorkerSlice {
    param(
        [Parameter(Mandatory)]
        [object[]]$Workers,
        [Parameter(Mandatory)]
        [int[]]$Indexes
    )

    return @(
        foreach ($index in $Indexes) {
            if ($index -ge 0 -and $index -lt $Workers.Count) {
                $Workers[$index]
            }
        }
    )
}

function Set-SyncFactorsDemoReportTiming {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [datetime]$CompletedAt,
        [ValidateRange(1, 240)]
        [int]$DurationMinutes = 12
    )

    $startedAt = $CompletedAt.AddMinutes(-1 * $DurationMinutes)
    $Report['startedAt'] = $startedAt.ToString('o')
    $Report['completedAt'] = $CompletedAt.ToString('o')
    if ("$($Report['status'])" -eq 'Failed') {
        $Report['failedAt'] = $CompletedAt.ToString('o')
    } else {
        $Report['status'] = 'Succeeded'
        $Report['failedAt'] = $null
        $Report['errorMessage'] = $null
    }
}

function Save-SyncFactorsDemoReport {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [string]$Directory,
        [Parameter(Mandatory)]
        [string]$Mode,
        [Parameter(Mandatory)]
        [datetime]$CompletedAt
    )

    if (-not (Test-Path -Path $Directory -PathType Container)) {
        New-Item -Path $Directory -ItemType Directory -Force | Out-Null
    }

    $path = Join-Path -Path $Directory -ChildPath ("syncfactors-{0}-{1}.json" -f $Mode, $CompletedAt.ToString('yyyyMMdd-HHmmss'))
    [void]$Report.Remove('operationSequence')
    $Report | ConvertTo-Json -Depth 30 | Set-Content -Path $path
    return $path
}

function New-SyncFactorsDemoCreateEntry {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Worker
    )

    $profile = Get-SyncFactorsSyntheticWorkerProfile -Worker $Worker
    return @{
        workerId = $profile.workerId
        samAccountName = $profile.samAccountName
        targetOu = $profile.targetOu
        proposedEnable = $true
        matchedExistingUser = $false
        reviewCategory = 'NewUser'
        attributeRows = @(New-SyncFactorsSyntheticAttributeRows -ChangedAttributeDetails @(
                [pscustomobject]@{
                    sourceField = 'firstName'
                    targetAttribute = 'GivenName'
                    currentAdValue = $null
                    proposedValue = "$($Worker.firstName)"
                },
                [pscustomobject]@{
                    sourceField = 'lastName'
                    targetAttribute = 'Surname'
                    currentAdValue = $null
                    proposedValue = "$($Worker.lastName)"
                }
            ))
    }
}

function New-SyncFactorsDemoUpdateEntry {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Worker,
        [string]$Scenario = 'DepartmentTransfer',
        [switch]$IncludeReviewMetadata
    )

    $profile = Get-SyncFactorsSyntheticWorkerProfile -Worker $Worker
    $changes = @(New-SyncFactorsSyntheticChangedAttributeDetails -Worker $Worker -Scenario $Scenario)
    $entry = @{
        workerId = $profile.workerId
        samAccountName = $profile.samAccountName
        targetOu = $profile.targetOu
        currentDistinguishedName = $profile.distinguishedName
        currentEnabled = $true
        proposedEnable = $true
        matchedExistingUser = $true
        changedAttributeDetails = $changes
    }

    if ($IncludeReviewMetadata) {
        $entry['reviewCategory'] = 'ExistingUserChanges'
        $entry['reviewCaseType'] = 'RehireCase'
        $entry['operatorActionSummary'] = 'Confirm how this rehire should reuse or restore the existing AD identity.'
        $entry['operatorActions'] = @(New-SyncFactorsSyntheticOperatorActions -ReviewCaseType 'RehireCase')
    }

    return $entry
}

function Add-SyncFactorsDemoCreateOperation {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [pscustomobject]$Worker
    )

    $profile = Get-SyncFactorsSyntheticWorkerProfile -Worker $Worker
    Add-SyncFactorsReportOperation `
        -Report $Report `
        -OperationType 'CreateUser' `
        -WorkerId $profile.workerId `
        -Bucket 'creates' `
        -Target @{ samAccountName = $profile.samAccountName } `
        -Before $null `
        -After ([pscustomobject]@{
            samAccountName = $profile.samAccountName
            userPrincipalName = $profile.userPrincipalName
            targetOu = $profile.targetOu
            enabled = $true
        }) | Out-Null
}

function Add-SyncFactorsDemoUpdateOperation {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [pscustomobject]$Worker,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$ChangedAttributeDetails
    )

    $profile = Get-SyncFactorsSyntheticWorkerProfile -Worker $Worker
    $before = [ordered]@{}
    $after = [ordered]@{}
    foreach ($change in $ChangedAttributeDetails) {
        $before[$change.targetAttribute] = $change.currentAdValue
        $after[$change.targetAttribute] = $change.proposedValue
    }

    Add-SyncFactorsReportOperation `
        -Report $Report `
        -OperationType 'UpdateAttributes' `
        -WorkerId $profile.workerId `
        -Bucket 'updates' `
        -Target @{ samAccountName = $profile.samAccountName } `
        -Before ([pscustomobject]$before) `
        -After ([pscustomobject]$after) | Out-Null
}

$projectRoot = Split-Path -Path $PSScriptRoot -Parent
$moduleRoot = Join-Path -Path $projectRoot -ChildPath 'src/Modules/SyncFactors'
Import-Module (Join-Path $moduleRoot 'SyntheticHarness.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'Reporting.psm1') -Force -DisableNameChecking
$stateModule = Import-Module (Join-Path $moduleRoot 'State.psm1') -Force -DisableNameChecking -PassThru
$monitoringModule = Import-Module (Join-Path $moduleRoot 'Monitoring.psm1') -Force -DisableNameChecking -PassThru
$persistenceModule = Import-Module (Join-Path $moduleRoot 'Persistence.psm1') -Force -DisableNameChecking -PassThru
$saveSyncFactorsState = $stateModule.ExportedFunctions['Save-SyncFactorsState']
$getSyncFactorsRuntimeStatusPath = $monitoringModule.ExportedFunctions['Get-SyncFactorsRuntimeStatusPath']
$newSyncFactorsIdleRuntimeStatus = $monitoringModule.ExportedFunctions['New-SyncFactorsIdleRuntimeStatus']
$newSyncFactorsRuntimeStatusSnapshot = $monitoringModule.ExportedFunctions['New-SyncFactorsRuntimeStatusSnapshot']
$saveSyncFactorsRuntimeStatusSnapshot = $monitoringModule.ExportedFunctions['Save-SyncFactorsRuntimeStatusSnapshot']
$importSyncFactorsJsonArtifactsToSqlite = $persistenceModule.ExportedFunctions['Import-SyncFactorsJsonArtifactsToSqlite']

$resolvedConfigPath = (Resolve-Path -Path (Get-ResolvedPathOrJoin -Path $ConfigPath -BasePath $projectRoot)).Path
$resolvedMappingConfigPath = (Resolve-Path -Path (Get-ResolvedPathOrJoin -Path $MappingConfigPath -BasePath $projectRoot)).Path
$resolvedOutputDirectory = Get-ResolvedPathOrJoin -Path $OutputDirectory -BasePath $projectRoot

Initialize-SyncFactorsDemoOutputDirectory -Path $resolvedOutputDirectory -Force:$Force

$effectiveRunCount = if ($RunCount -gt 0) { $RunCount } else { 5 }
if ($Preset -eq 'MixedHistory' -and $effectiveRunCount -lt 5) {
    throw 'Preset MixedHistory requires at least 5 runs to seed the expected dashboard coverage.'
}

$reportDirectory = Join-Path -Path $resolvedOutputDirectory -ChildPath 'reports/output'
$reviewDirectory = Join-Path -Path $resolvedOutputDirectory -ChildPath 'reports/review'
$statePath = Join-Path -Path $resolvedOutputDirectory -ChildPath 'state/demo-sync-state.json'
$demoConfigDirectory = Join-Path -Path $resolvedOutputDirectory -ChildPath 'config'
$demoConfigPath = Join-Path -Path $demoConfigDirectory -ChildPath 'demo.mock-sync-config.json'

foreach ($directory in @($reportDirectory, $reviewDirectory, (Split-Path -Path $statePath -Parent), $demoConfigDirectory)) {
    if (-not (Test-Path -Path $directory -PathType Container)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }
}

$demoConfig = Get-Content -Path $resolvedConfigPath -Raw | ConvertFrom-Json -Depth 30
$demoConfig.state.path = $statePath
$demoConfig.reporting.outputDirectory = $reportDirectory
$demoConfig.reporting | Add-Member -MemberType NoteProperty -Name reviewOutputDirectory -Value $reviewDirectory -Force
$demoConfig | ConvertTo-Json -Depth 30 | Set-Content -Path $demoConfigPath

$inactiveCount = [math]::Min([math]::Max([int][math]::Ceiling($UserCount * 0.08), 6), $UserCount - 1)
$managerCount = [math]::Min([math]::Max([int][math]::Ceiling($UserCount / 12), 8), 50)
$syntheticDirectory = New-SyncFactorsSyntheticWorkers -UserCount $UserCount -InactiveCount $inactiveCount -ManagerCount $managerCount
$workers = @($syntheticDirectory.workers)

$focusWorker = $workers[1]
$previewWorker = $workers[2]
$quarantineWorker = $workers[3]
$guardrailWorker = $workers[4]
$conflictWorker = $workers[5]
$pendingDeletionWorker = $workers[$workers.Count - 1]

$stateWorkers = [ordered]@{}
foreach ($worker in $workers) {
    $workerId = "$($worker.personIdExternal)"
    if ($workerId -eq "$($previewWorker.personIdExternal)") {
        $stateWorkers[$workerId] = New-SyncFactorsSyntheticTrackedWorkerState -Worker $worker -Suppressed
        continue
    }

    if ($workerId -eq "$($pendingDeletionWorker.personIdExternal)") {
        $stateWorkers[$workerId] = New-SyncFactorsSyntheticTrackedWorkerState -Worker $worker -Suppressed -PendingDeletion
        continue
    }

    $stateWorkers[$workerId] = New-SyncFactorsSyntheticTrackedWorkerState -Worker $worker
}

$latestCheckpoint = (Get-Date).AddMinutes(-38).ToString('o')
$state = [pscustomobject]@{
    checkpoint = $latestCheckpoint
    workers = [pscustomobject]$stateWorkers
}
& $saveSyncFactorsState -State $state -Path $statePath
$state | ConvertTo-Json -Depth 30 | Set-Content -Path $statePath

$runBaseTime = (Get-Date).AddHours(-5)
$reportPaths = [System.Collections.Generic.List[string]]::new()

$fullRun = New-SyncFactorsReport -Mode 'Full' -DryRun -ConfigPath $demoConfigPath -MappingConfigPath $resolvedMappingConfigPath -StatePath $statePath -ArtifactType 'WorkerSync'
$createWorkers = Get-SyncFactorsDemoWorkerSlice -Workers $workers -Indexes (6..45)
foreach ($worker in $createWorkers) {
    Add-SyncFactorsReportEntry -Report $fullRun -Bucket 'creates' -Entry (New-SyncFactorsDemoCreateEntry -Worker $worker)
}
foreach ($worker in ($createWorkers | Select-Object -First 8)) {
    Add-SyncFactorsDemoCreateOperation -Report $fullRun -Worker $worker
}

$fullRunUpdateWorkers = @($focusWorker, $previewWorker) + (Get-SyncFactorsDemoWorkerSlice -Workers $workers -Indexes (46..53))
foreach ($worker in $fullRunUpdateWorkers) {
    $entry = New-SyncFactorsDemoUpdateEntry -Worker $worker -Scenario 'DepartmentTransfer'
    Add-SyncFactorsReportEntry -Report $fullRun -Bucket 'updates' -Entry $entry
    Add-SyncFactorsDemoUpdateOperation -Report $fullRun -Worker $worker -ChangedAttributeDetails @($entry.changedAttributeDetails)
}

foreach ($worker in (Get-SyncFactorsDemoWorkerSlice -Workers $workers -Indexes (54..59))) {
    $profile = Get-SyncFactorsSyntheticWorkerProfile -Worker $worker
    Add-SyncFactorsReportEntry -Report $fullRun -Bucket 'enables' -Entry @{
        workerId = $profile.workerId
        samAccountName = $profile.samAccountName
        currentEnabled = $false
        proposedEnable = $true
        licensingGroups = @('CN=Lab-M365,OU=Groups,DC=example,DC=com')
    }
}

foreach ($worker in (Get-SyncFactorsDemoWorkerSlice -Workers $workers -Indexes (0..20))) {
    $profile = Get-SyncFactorsSyntheticWorkerProfile -Worker $worker
    Add-SyncFactorsReportEntry -Report $fullRun -Bucket 'unchanged' -Entry @{
        workerId = $profile.workerId
        samAccountName = $profile.samAccountName
        currentDistinguishedName = $profile.distinguishedName
        currentEnabled = $true
        proposedEnable = $true
        matchedExistingUser = $true
    }
}

$fullRun['reviewSummary'] = $null
Set-SyncFactorsDemoReportTiming -Report $fullRun -CompletedAt $runBaseTime -DurationMinutes 18
$reportPaths.Add((Save-SyncFactorsDemoReport -Report $fullRun -Directory $reportDirectory -Mode 'Full' -CompletedAt $runBaseTime))

$conflictRunCompletedAt = $runBaseTime.AddMinutes(55)
$conflictRun = New-SyncFactorsReport -Mode 'Delta' -DryRun -ConfigPath $demoConfigPath -MappingConfigPath $resolvedMappingConfigPath -StatePath $statePath -ArtifactType 'WorkerSync'
$conflictProfile = Get-SyncFactorsSyntheticWorkerProfile -Worker $conflictWorker
Add-SyncFactorsReportEntry -Report $conflictRun -Bucket 'conflicts' -Entry @{
    workerId = $conflictProfile.workerId
    samAccountName = $conflictProfile.samAccountName
    reason = 'DuplicateWorkerId'
    occurrences = 2
    reviewCaseType = 'ConflictCase'
    operatorActionSummary = 'Duplicate worker identities were found in the source export.'
    operatorActions = @(New-SyncFactorsSyntheticOperatorActions -ReviewCaseType 'ConflictCase')
}
Add-SyncFactorsReportEntry -Report $conflictRun -Bucket 'conflicts' -Entry @{
    workerId = "$($previewWorker.personIdExternal)"
    samAccountName = (Get-SyncFactorsSyntheticWorkerProfile -Worker $previewWorker).samAccountName
    reason = 'DuplicateAdIdentityMatch'
    occurrences = 2
    reviewCaseType = 'ConflictCase'
    operatorActionSummary = 'Two AD records matched the worker identity and require operator review.'
    operatorActions = @(New-SyncFactorsSyntheticOperatorActions -ReviewCaseType 'ConflictCase')
}
$conflictUpdate = New-SyncFactorsDemoUpdateEntry -Worker $focusWorker -Scenario 'ManagerRefresh'
Add-SyncFactorsReportEntry -Report $conflictRun -Bucket 'updates' -Entry $conflictUpdate
Add-SyncFactorsDemoUpdateOperation -Report $conflictRun -Worker $focusWorker -ChangedAttributeDetails @($conflictUpdate.changedAttributeDetails)
Set-SyncFactorsDemoReportTiming -Report $conflictRun -CompletedAt $conflictRunCompletedAt -DurationMinutes 11
$reportPaths.Add((Save-SyncFactorsDemoReport -Report $conflictRun -Directory $reportDirectory -Mode 'Delta' -CompletedAt $conflictRunCompletedAt))

$guardrailRunCompletedAt = $conflictRunCompletedAt.AddMinutes(55)
$guardrailRun = New-SyncFactorsReport -Mode 'Full' -DryRun -ConfigPath $demoConfigPath -MappingConfigPath $resolvedMappingConfigPath -StatePath $statePath -ArtifactType 'WorkerSync'
$guardrailRun['status'] = 'Failed'
$guardrailRun['errorMessage'] = 'Create guardrail exceeded the configured threshold.'
$guardrailProfile = Get-SyncFactorsSyntheticWorkerProfile -Worker $guardrailWorker
Add-SyncFactorsReportEntry -Report $guardrailRun -Bucket 'guardrailFailures' -Entry @{
    workerId = $guardrailProfile.workerId
    samAccountName = $guardrailProfile.samAccountName
    reason = 'CreateThresholdExceeded'
    threshold = 'maxCreatesPerRun'
    attemptedCount = 41
    configuredLimit = 25
    reviewCaseType = 'GuardrailCase'
    operatorActionSummary = 'The run stopped before creating additional accounts.'
}
foreach ($worker in (Get-SyncFactorsDemoWorkerSlice -Workers $workers -Indexes (60..63))) {
    Add-SyncFactorsReportEntry -Report $guardrailRun -Bucket 'creates' -Entry (New-SyncFactorsDemoCreateEntry -Worker $worker)
}
Set-SyncFactorsDemoReportTiming -Report $guardrailRun -CompletedAt $guardrailRunCompletedAt -DurationMinutes 9
$reportPaths.Add((Save-SyncFactorsDemoReport -Report $guardrailRun -Directory $reportDirectory -Mode 'Full' -CompletedAt $guardrailRunCompletedAt))

$reviewRunCompletedAt = $guardrailRunCompletedAt.AddMinutes(55)
$reviewRun = New-SyncFactorsReport -Mode 'Review' -ConfigPath $demoConfigPath -MappingConfigPath $resolvedMappingConfigPath -StatePath $statePath -ArtifactType 'FirstSyncReview'
$previewProfile = Get-SyncFactorsSyntheticWorkerProfile -Worker $previewWorker
Add-SyncFactorsReportEntry -Report $reviewRun -Bucket 'manualReview' -Entry @{
    workerId = $previewProfile.workerId
    samAccountName = $previewProfile.samAccountName
    reason = 'RehireDetected'
    reviewCategory = 'ExistingUserChanges'
    reviewCaseType = 'RehireCase'
    operatorActionSummary = 'Confirm how this rehire should reuse or restore the existing AD identity.'
    operatorActions = @(New-SyncFactorsSyntheticOperatorActions -ReviewCaseType 'RehireCase')
    currentDistinguishedName = $previewProfile.distinguishedName
    currentEnabled = $false
    proposedEnable = $true
    matchedExistingUser = $true
    targetOu = $previewProfile.targetOu
}
$quarantineProfile = Get-SyncFactorsSyntheticWorkerProfile -Worker $quarantineWorker
Add-SyncFactorsReportEntry -Report $reviewRun -Bucket 'quarantined' -Entry @{
    workerId = $quarantineProfile.workerId
    samAccountName = $quarantineProfile.samAccountName
    reason = 'MissingRequiredData'
    fields = @('email', 'managerEmployeeId')
    reviewCaseType = 'QuarantineCase'
    operatorActionSummary = 'The worker is missing required source data and must be reviewed before sync can continue.'
    operatorActions = @(New-SyncFactorsSyntheticOperatorActions -ReviewCaseType 'QuarantineCase')
    matchedExistingUser = $false
    attributeRows = @(New-SyncFactorsSyntheticAttributeRows -ChangedAttributeDetails @(
            [pscustomobject]@{
                sourceField = 'email'
                targetAttribute = 'mail'
                currentAdValue = $null
                proposedValue = $null
            }
        ))
}
Add-SyncFactorsReportEntry -Report $reviewRun -Bucket 'manualReview' -Entry @{
    workerId = "$($focusWorker.personIdExternal)"
    samAccountName = (Get-SyncFactorsSyntheticWorkerProfile -Worker $focusWorker).samAccountName
    reason = 'ManagerNotFound'
    reviewCategory = 'ExistingUserChanges'
    reviewCaseType = 'ManagerResolutionCase'
    operatorActionSummary = 'Source manager data did not resolve to a managed AD user.'
    operatorActions = @(New-SyncFactorsSyntheticOperatorActions -ReviewCaseType 'ManagerResolutionCase')
    matchedExistingUser = $true
}
$reviewRun['reviewSummary'] = [pscustomobject]@{
    existingUsersMatched = 12
    existingUsersWithAttributeChanges = 4
    proposedCreates = 18
    proposedOffboarding = 2
    quarantined = 1
    conflicts = 0
    operatorActionCases = [pscustomobject]@{
        quarantinedWorkers = 1
        unresolvedManagers = 1
        rehireCases = 1
    }
}
Set-SyncFactorsDemoReportTiming -Report $reviewRun -CompletedAt $reviewRunCompletedAt -DurationMinutes 14
$reportPaths.Add((Save-SyncFactorsDemoReport -Report $reviewRun -Directory $reviewDirectory -Mode 'Review' -CompletedAt $reviewRunCompletedAt))

$previewRunCompletedAt = $reviewRunCompletedAt.AddMinutes(55)
$workerPreview = New-SyncFactorsReport -Mode 'Review' -ConfigPath $demoConfigPath -MappingConfigPath $resolvedMappingConfigPath -StatePath $statePath -ArtifactType 'WorkerPreview' -WorkerScope ([pscustomobject]@{
        identityField = 'personIdExternal'
        workerId = $previewProfile.workerId
    })
$previewUpdate = New-SyncFactorsDemoUpdateEntry -Worker $previewWorker -Scenario 'RehireCleanup' -IncludeReviewMetadata
Add-SyncFactorsReportEntry -Report $workerPreview -Bucket 'updates' -Entry $previewUpdate
Add-SyncFactorsDemoUpdateOperation -Report $workerPreview -Worker $previewWorker -ChangedAttributeDetails @($previewUpdate.changedAttributeDetails)
$workerPreview['reviewSummary'] = [pscustomobject]@{
    existingUsersMatched = 1
    existingUsersWithAttributeChanges = 1
    proposedCreates = 0
    proposedOffboarding = 0
    quarantined = 0
    conflicts = 0
}
Set-SyncFactorsDemoReportTiming -Report $workerPreview -CompletedAt $previewRunCompletedAt -DurationMinutes 6
$reportPaths.Add((Save-SyncFactorsDemoReport -Report $workerPreview -Directory $reviewDirectory -Mode 'Review' -CompletedAt $previewRunCompletedAt))

if ($effectiveRunCount -gt 5) {
    $extraRunTime = $runBaseTime.AddMinutes(20)
    for ($runIndex = 6; $runIndex -le $effectiveRunCount; $runIndex++) {
        $extraRun = New-SyncFactorsReport -Mode 'Delta' -DryRun -ConfigPath $demoConfigPath -MappingConfigPath $resolvedMappingConfigPath -StatePath $statePath -ArtifactType 'WorkerSync'
        $extraIndexes = @(
            ($runIndex + 10)
            ($runIndex + 20)
            ($runIndex + 30)
        )
        foreach ($worker in (Get-SyncFactorsDemoWorkerSlice -Workers $workers -Indexes $extraIndexes)) {
            $profile = Get-SyncFactorsSyntheticWorkerProfile -Worker $worker
            Add-SyncFactorsReportEntry -Report $extraRun -Bucket 'unchanged' -Entry @{
                workerId = $profile.workerId
                samAccountName = $profile.samAccountName
                currentDistinguishedName = $profile.distinguishedName
                currentEnabled = $true
                proposedEnable = $true
                matchedExistingUser = $true
            }
        }

        $extraWorker = $workers[[math]::Min($runIndex + 1, $workers.Count - 1)]
        $extraUpdate = New-SyncFactorsDemoUpdateEntry -Worker $extraWorker -Scenario 'TitleRefresh'
        Add-SyncFactorsReportEntry -Report $extraRun -Bucket 'updates' -Entry $extraUpdate
        Add-SyncFactorsDemoUpdateOperation -Report $extraRun -Worker $extraWorker -ChangedAttributeDetails @($extraUpdate.changedAttributeDetails)

        Set-SyncFactorsDemoReportTiming -Report $extraRun -CompletedAt $extraRunTime -DurationMinutes 7
        $reportPaths.Add((Save-SyncFactorsDemoReport -Report $extraRun -Directory $reportDirectory -Mode 'Delta' -CompletedAt $extraRunTime))
        $extraRunTime = $extraRunTime.AddMinutes(22)
    }
}

if ($IncludeActiveRun) {
    $activeReport = New-SyncFactorsReport -Mode 'Delta' -DryRun -ConfigPath $demoConfigPath -MappingConfigPath $resolvedMappingConfigPath -StatePath $statePath -ArtifactType 'WorkerSync'
    $activeReport['startedAt'] = (Get-Date).AddMinutes(-8).ToString('o')
    Add-SyncFactorsReportEntry -Report $activeReport -Bucket 'updates' -Entry (New-SyncFactorsDemoUpdateEntry -Worker $focusWorker -Scenario 'LocationCorrection')
    Add-SyncFactorsReportEntry -Report $activeReport -Bucket 'manualReview' -Entry @{
        workerId = $previewProfile.workerId
        samAccountName = $previewProfile.samAccountName
        reason = 'RehireDetected'
        reviewCaseType = 'RehireCase'
        operatorActionSummary = 'Confirm how this rehire should reuse or restore the existing AD identity.'
        operatorActions = @(New-SyncFactorsSyntheticOperatorActions -ReviewCaseType 'RehireCase')
        matchedExistingUser = $true
    }

    $runtimeSnapshot = & $newSyncFactorsRuntimeStatusSnapshot `
        -Report $activeReport `
        -StatePath $statePath `
        -Stage 'ProcessingWorkers' `
        -Status 'InProgress' `
        -ProcessedWorkers 67 `
        -TotalWorkers $UserCount `
        -CurrentWorkerId "$($quarantineWorker.personIdExternal)" `
        -LastAction "Evaluating worker $($quarantineWorker.personIdExternal)." `
        -CompletedAt $null `
        -ErrorMessage $null
    & $saveSyncFactorsRuntimeStatusSnapshot -Snapshot $runtimeSnapshot -StatePath $statePath | Out-Null
    $runtimeSnapshot | ConvertTo-Json -Depth 30 | Set-Content -Path (& $getSyncFactorsRuntimeStatusPath -StatePath $statePath)
} else {
    $idleSnapshot = & $newSyncFactorsIdleRuntimeStatus -StatePath $statePath
    & $saveSyncFactorsRuntimeStatusSnapshot -Snapshot $idleSnapshot -StatePath $statePath | Out-Null
    $idleSnapshot | ConvertTo-Json -Depth 30 | Set-Content -Path (& $getSyncFactorsRuntimeStatusPath -StatePath $statePath)
}

[void](& $importSyncFactorsJsonArtifactsToSqlite -Config $demoConfig)

$result = [pscustomobject]@{
    preset = $Preset
    configPath = $demoConfigPath
    mappingConfigPath = $resolvedMappingConfigPath
    outputDirectory = $resolvedOutputDirectory
    reportDirectory = $reportDirectory
    reviewReportDirectory = $reviewDirectory
    statePath = $statePath
    runtimeStatusPath = & $getSyncFactorsRuntimeStatusPath -StatePath $statePath
    reportCount = @($reportPaths).Count
    includeActiveRun = $IncludeActiveRun
    userCount = $UserCount
    trackedWorkers = @($workers).Count
    reportPaths = @($reportPaths | Sort-Object)
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 20
    return
}

Write-Host 'SyncFactors Demo Data'
Write-Host "Preset: $($result.preset)"
Write-Host "Users generated: $($result.userCount)"
Write-Host "Reports generated: $($result.reportCount)"
Write-Host "Demo config: $($result.configPath)"
Write-Host "Report directory: $($result.reportDirectory)"
Write-Host "Review report directory: $($result.reviewReportDirectory)"
Write-Host "State path: $($result.statePath)"
Write-Host "Runtime status: $($result.runtimeStatusPath)"
