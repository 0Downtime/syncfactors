Set-StrictMode -Version Latest

$moduleRoot = $PSScriptRoot
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'State.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'Reporting.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'Monitoring.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'Mapping.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'SuccessFactors.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'ActiveDirectorySync.psm1') -Force -DisableNameChecking

function Write-SyncFactorsLog {
    [CmdletBinding()]
    param(
        [string]$Level = 'INFO',
        [string]$Message
    )

    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Host "[$timestamp][$Level] $Message"
}

function Update-SyncFactorsRuntimeStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [string]$StatePath,
        [Parameter(Mandatory)]
        [string]$Stage,
        [string]$Status = 'InProgress',
        [int]$ProcessedWorkers = 0,
        [int]$TotalWorkers = 0,
        [string]$CurrentWorkerId,
        [string]$LastAction,
        [string]$CompletedAt,
        [string]$ErrorMessage
    )

    Write-SyncFactorsRuntimeStatusSnapshot -Report $Report -StatePath $StatePath -Stage $Stage -Status $Status -ProcessedWorkers $ProcessedWorkers -TotalWorkers $TotalWorkers -CurrentWorkerId $CurrentWorkerId -LastAction $LastAction -CompletedAt $CompletedAt -ErrorMessage $ErrorMessage | Out-Null
}

function Convert-ToSyncFactorsSerializable {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $result[$key] = Convert-ToSyncFactorsSerializable -Value $Value[$key]
        }
        return [pscustomobject]$result
    }

    if ($Value -is [System.Array]) {
        return @($Value | ForEach-Object { Convert-ToSyncFactorsSerializable -Value $_ })
    }

    if ($Value -is [datetime]) {
        return $Value.ToString('o')
    }

    $properties = @($Value.PSObject.Properties)
    if ($properties.Count -gt 0 -and -not ($Value -is [string])) {
        $result = [ordered]@{}
        foreach ($property in $properties) {
            $result[$property.Name] = Convert-ToSyncFactorsSerializable -Value $property.Value
        }
        return [pscustomobject]$result
    }

    return $Value
}

function Test-SyncFactorsWorkerIsActive {
    [CmdletBinding()]
    param([pscustomobject]$Worker)
    $status = Get-SyncFactorsWorkerStatusValue -Worker $Worker
    return @('active', 'enabled', 'a', 'u') -contains "$status".ToLowerInvariant()
}

function Get-SyncFactorsWorkerStatusValue {
    [CmdletBinding()]
    param([pscustomobject]$Worker)

    foreach ($path in @(
            'status',
            'employmentNav[0].jobInfoNav[0].emplStatus',
            'employmentNav[0].userNav.status',
            'userNav.status'
        )) {
        $value = Get-NestedValue -InputObject $Worker -Path $path
        if (-not [string]::IsNullOrWhiteSpace("$value")) {
            return "$value"
        }
    }

    return $null
}

function Get-SyncFactorsWorkerStartDateValue {
    [CmdletBinding()]
    param([pscustomobject]$Worker)

    foreach ($path in @(
            'startDate',
            'employmentNav[0].startDate'
        )) {
        $value = Get-NestedValue -InputObject $Worker -Path $path
        if (-not [string]::IsNullOrWhiteSpace("$value")) {
            return "$value"
        }
    }

    return $null
}

function Get-SyncFactorsWorkerIdentityValue {
    [CmdletBinding()]
    param(
        [pscustomobject]$Worker,
        [pscustomobject]$Config
    )

    $identityField = $Config.successFactors.query.identityField
    return "$($Worker.PSObject.Properties[$identityField].Value)"
}

function Test-SyncFactorsWorkerIsPrehireEligible {
    [CmdletBinding()]
    param(
        [pscustomobject]$Worker,
        [int]$EnableBeforeDays
    )

    $startDateValue = Get-SyncFactorsWorkerStartDateValue -Worker $Worker
    if (-not $startDateValue) {
        return $false
    }

    $startDate = Get-Date $startDateValue
    return $startDate.Date -le (Get-Date).Date.AddDays($EnableBeforeDays)
}

function Get-SyncFactorsAttributeBeforeValues {
    [CmdletBinding()]
    param(
        [pscustomobject]$User,
        [hashtable]$Changes
    )

    $before = [ordered]@{}
    foreach ($key in $Changes.Keys) {
        $property = $User.PSObject.Properties[$key]
        $before[$key] = if ($property) { Convert-ToSyncFactorsSerializable -Value $property.Value } else { $null }
    }

    return [pscustomobject]$before
}

function Get-SyncFactorsUserTargetDescriptor {
    [CmdletBinding()]
    param(
        [string]$WorkerId,
        [pscustomobject]$User
    )

    $objectGuidProperty = if ($User) { $User.PSObject.Properties['ObjectGuid'] } else { $null }
    $distinguishedNameProperty = if ($User) { $User.PSObject.Properties['DistinguishedName'] } else { $null }
    $samAccountNameProperty = if ($User) { $User.PSObject.Properties['SamAccountName'] } else { $null }

    return @{
        workerId = $WorkerId
        objectGuid = if ($objectGuidProperty -and $objectGuidProperty.Value) { $objectGuidProperty.Value.Guid } else { $null }
        samAccountName = if ($samAccountNameProperty) { $samAccountNameProperty.Value } else { $null }
        distinguishedName = if ($distinguishedNameProperty) { $distinguishedNameProperty.Value } else { $null }
    }
}

function Get-SyncFactorsWorkerStateSnapshot {
    [CmdletBinding()]
    param(
        [pscustomobject]$State,
        [string]$WorkerId
    )

    $workerState = Get-SyncFactorsWorkerState -State $State -WorkerId $WorkerId
    if (-not $workerState) {
        return $null
    }

    return Convert-ToSyncFactorsSerializable -Value $workerState
}

function Get-SyncFactorsCollectionCount {
    [CmdletBinding()]
    param($Value)

    if ($null -eq $Value) {
        return 0
    }

    if ($Value -is [System.Array]) {
        return @($Value).Count
    }

    return 1
}

function Get-SyncFactorsSingleResult {
    [CmdletBinding()]
    param($Value)

    if ($Value -is [System.Array]) {
        return @($Value)[0]
    }

    return $Value
}

function Test-SyncFactorsReviewMode {
    [CmdletBinding()]
    param(
        [string]$Mode
    )

    return "$Mode" -eq 'Review'
}

function Get-SyncFactorsArtifactType {
    [CmdletBinding()]
    param(
        [string]$Mode,
        [string]$WorkerId
    )

    if (Test-SyncFactorsReviewMode -Mode $Mode) {
        if (-not [string]::IsNullOrWhiteSpace($WorkerId)) {
            return 'WorkerPreview'
        }

        return 'FirstSyncReview'
    }

    if (-not [string]::IsNullOrWhiteSpace($WorkerId)) {
        return 'WorkerSync'
    }

    return 'SyncReport'
}

function Get-SyncFactorsChangedMappingRows {
    [CmdletBinding()]
    param($Rows)

    return @(
        @($Rows) | Where-Object { $_.changed } | ForEach-Object {
            [pscustomobject]@{
                sourceField = $_.sourceField
                targetAttribute = $_.targetAttribute
                transform = $_.transform
                required = $_.required
                currentAdValue = Convert-ToSyncFactorsSerializable -Value $_.currentAdValue
                proposedValue = Convert-ToSyncFactorsSerializable -Value $_.proposedValue
            }
        }
    )
}

function Get-SyncFactorsReviewAttributeRows {
    [CmdletBinding()]
    param($Rows)

    return @(
        @($Rows) | ForEach-Object {
            [pscustomobject]@{
                sourceField = $_.sourceField
                targetAttribute = $_.targetAttribute
                transform = $_.transform
                required = $_.required
                sourceValue = Convert-ToSyncFactorsSerializable -Value $_.sourceValue
                currentAdValue = Convert-ToSyncFactorsSerializable -Value $_.currentAdValue
                proposedValue = Convert-ToSyncFactorsSerializable -Value $_.proposedValue
                changed = [bool]$_.changed
            }
        }
    )
}

function New-SyncFactorsOperatorAction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Code,
        [Parameter(Mandatory)]
        [string]$Label,
        [Parameter(Mandatory)]
        [string]$Description
    )

    return [pscustomobject]@{
        code = $Code
        label = $Label
        description = $Description
    }
}

function Get-SyncFactorsManualReviewMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ReviewCaseType,
        [string]$Reason,
        [string]$ManagerEmployeeId,
        [AllowNull()]
        [string[]]$Fields,
        [string]$DistinguishedName,
        [string[]]$ApprovalActions
    )

    $actions = [System.Collections.Generic.List[object]]::new()
    $operatorActionSummary = $null

    switch ($ReviewCaseType) {
        'UnresolvedManager' {
            $operatorActionSummary = 'Resolve the worker manager before applying AD changes.'
            $actions.Add((New-SyncFactorsOperatorAction -Code 'ResolveManagerIdentity' -Label 'Resolve manager identity' -Description $(if ([string]::IsNullOrWhiteSpace($ManagerEmployeeId)) { 'Confirm the worker has a valid manager employee ID in SuccessFactors.' } else { "Find or create the manager account for employee ID $ManagerEmployeeId before retrying the worker sync." })))
            $actions.Add((New-SyncFactorsOperatorAction -Code 'CorrectManagerReference' -Label 'Correct manager reference' -Description 'Fix the worker manager reference in SuccessFactors if it points to the wrong or retired record.'))
            $actions.Add((New-SyncFactorsOperatorAction -Code 'RerunWorkerPreview' -Label 'Rerun worker preview' -Description 'Run a one-worker review again after the manager resolves, then apply the worker sync.'))
        }
        'RehireCase' {
            $operatorActionSummary = 'Confirm how this rehire should reuse or restore the existing AD identity.'
            $actions.Add((New-SyncFactorsOperatorAction -Code 'ConfirmAccountReuse' -Label 'Confirm account reuse' -Description $(if ([string]::IsNullOrWhiteSpace($DistinguishedName)) { 'Decide whether the rehire should reuse the prior AD account or be handled another way.' } else { "Review the prior AD account at $DistinguishedName and confirm whether it should be reused." })))
            $actions.Add((New-SyncFactorsOperatorAction -Code 'RestoreOrUnsuppress' -Label 'Restore or unsuppress account' -Description 'Restore the existing AD account from suppression or pending deletion before continuing lifecycle changes.'))
            $actions.Add((New-SyncFactorsOperatorAction -Code 'RerunWorkerSync' -Label 'Rerun worker sync' -Description 'Run the worker preview or worker sync again after the rehire decision is recorded.'))
        }
        'ApprovalRequired' {
            $approvalLabels = @($ApprovalActions | Where-Object { -not [string]::IsNullOrWhiteSpace("$_") } | ForEach-Object {
                switch ("$_") {
                    'DisableUser' { 'disable the account' }
                    'DeleteUser' { 'delete the account' }
                    'MoveToGraveyardOu' { 'move the account to the graveyard OU' }
                    default { "$_" }
                }
            })
            $actionSummary = if ($approvalLabels.Count -gt 0) { $approvalLabels -join ', ' } else { 'apply the pending lifecycle change' }
            $operatorActionSummary = "Approve or reject the request to $actionSummary."
            $actions.Add((New-SyncFactorsOperatorAction -Code 'ReviewPendingLifecycleChange' -Label 'Review pending change' -Description 'Inspect the worker history, mapped attributes, and existing AD account state before approving the pending lifecycle action.'))
            $actions.Add((New-SyncFactorsOperatorAction -Code 'RunWorkerPreview' -Label 'Run worker preview' -Description 'Run a one-worker preview to confirm the exact lifecycle effect and supporting mapping details.'))
            $actions.Add((New-SyncFactorsOperatorAction -Code 'RunApprovedWorkerSync' -Label 'Run approved worker sync' -Description 'After approval, run the scoped one-worker sync to apply the change outside the broad sync batch.'))
        }
        default {
            $fieldList = @($Fields | Where-Object { -not [string]::IsNullOrWhiteSpace("$_") })
            $operatorActionSummary = 'Fix the worker data issue before allowing this record back into the normal sync flow.'
            $actions.Add((New-SyncFactorsOperatorAction -Code 'InspectWorkerRecord' -Label 'Inspect worker record' -Description 'Review the SuccessFactors worker payload and the mapped AD identity values for this quarantined worker.'))
            $actions.Add((New-SyncFactorsOperatorAction -Code 'CorrectWorkerData' -Label 'Correct worker data' -Description $(if ($Reason -eq 'MissingEmployeeId') { 'Populate the authoritative employee identifier so the worker can be matched safely.' } elseif ($Reason -eq 'MissingRequiredData' -and $fieldList.Count -gt 0) { "Populate the required fields: $($fieldList -join ', ')." } else { 'Correct the source data or mapping inputs that caused the worker to be quarantined.' })))
            $actions.Add((New-SyncFactorsOperatorAction -Code 'RerunWorkerPreview' -Label 'Rerun worker preview' -Description 'Run the one-worker review again and confirm the worker exits quarantine before applying changes.'))
        }
    }

    return @{
        reviewCaseType = $ReviewCaseType
        operatorActionSummary = $operatorActionSummary
        operatorActions = @($actions)
    }
}

function Get-SyncFactorsRequiredApprovalActions {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config
    )

    $approvalProperty = $Config.PSObject.Properties['approval']
    if (-not $approvalProperty -or $null -eq $approvalProperty.Value) {
        return @()
    }

    $approval = $approvalProperty.Value
    $enabledProperty = $approval.PSObject.Properties['enabled']
    $enabled = $enabledProperty -and [bool]$enabledProperty.Value
    if (-not $enabled) {
        return @()
    }

    $configuredActions = @()
    if ($approval.PSObject.Properties['requireFor']) {
        $configuredActions = @($approval.requireFor | Where-Object { -not [string]::IsNullOrWhiteSpace("$_") } | ForEach-Object { "$_" })
    }

    if ($configuredActions.Count -eq 0) {
        return @('DisableUser', 'DeleteUser', 'MoveToGraveyardOu')
    }

    return @($configuredActions | Select-Object -Unique)
}

function Test-SyncFactorsApprovalRequired {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [string]$Action,
        [switch]$BypassApprovalMode
    )

    if ($BypassApprovalMode) {
        return $false
    }

    return @((Get-SyncFactorsRequiredApprovalActions -Config $Config)) -contains $Action
}

function Add-SyncFactorsApprovalManualReviewEntry {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [string]$WorkerId,
        [Parameter(Mandatory)]
        [string[]]$ApprovalActions,
        [AllowNull()]
        [pscustomobject]$User,
        [string]$Reason = 'ApprovalRequired',
        [string]$TargetOu,
        [bool]$MatchedExistingUser = $true
    )

    $entry = @{
        workerId = $WorkerId
        reason = $Reason
        approvalActions = @($ApprovalActions | Select-Object -Unique)
        matchedExistingUser = [bool]$MatchedExistingUser
    }

    if ($User) {
        $entry['samAccountName'] = $User.SamAccountName
        $entry['currentDistinguishedName'] = $User.DistinguishedName
        $entry['currentEnabled'] = [bool]$User.Enabled
        if ($User.DistinguishedName) {
            $entry['distinguishedName'] = $User.DistinguishedName
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($TargetOu)) {
        $entry['targetOu'] = $TargetOu
    }

    foreach ($property in (Get-SyncFactorsManualReviewMetadata -ReviewCaseType 'ApprovalRequired' -Reason $Reason -ApprovalActions $ApprovalActions -DistinguishedName $(if ($User) { $User.DistinguishedName } else { $null })).GetEnumerator()) {
        $entry[$property.Key] = $property.Value
    }

    Add-SyncFactorsReportEntry -Report $Report -Bucket 'manualReview' -Entry $entry
}

function Get-SyncFactorsReportOutputDirectory {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [string]$Mode
    )

    if (Test-SyncFactorsReviewMode -Mode $Mode) {
        return $Config.reporting.reviewOutputDirectory
    }

    return $Config.reporting.outputDirectory
}

function Set-SyncFactorsReviewSummary {
    [CmdletBinding()]
    param(
        [System.Collections.IDictionary]$Report,
        [pscustomobject]$MappingConfig,
        [switch]$DeletionPassSkipped
    )

    $uniqueWorkers = {
        param($Items)
        return @(
            @($Items) |
                Where-Object { $_ -and $_.PSObject.Properties.Name -contains 'workerId' -and -not [string]::IsNullOrWhiteSpace("$($_.workerId)") } |
                ForEach-Object { "$($_.workerId)" } |
                Sort-Object -Unique
        )
    }

    $Report['reviewSummary'] = [pscustomobject]@{
        updates = @($Report['updates']).Count
        unchanged = @($Report['unchanged']).Count
        creates = @($Report['creates']).Count
        enables = @($Report['enables']).Count
        disables = @($Report['disables']).Count
        graveyardMoves = @($Report['graveyardMoves']).Count
        quarantined = @($Report['quarantined']).Count
        conflicts = @($Report['conflicts']).Count
        manualReview = @($Report['manualReview']).Count
        guardrailFailures = @($Report['guardrailFailures']).Count
        existingUsersMatched = @(& $uniqueWorkers (@($Report['updates']) + @($Report['unchanged']) + @($Report['enables']) + @($Report['disables']) + @($Report['graveyardMoves']) + @($Report['manualReview']) + @(@($Report['quarantined']) | Where-Object { $_.PSObject.Properties.Name -contains 'matchedExistingUser' -and $_.matchedExistingUser }))).Count
        existingUsersWithAttributeChanges = @(& $uniqueWorkers $Report['updates']).Count
        existingUsersWithoutAttributeChanges = @(& $uniqueWorkers $Report['unchanged']).Count
        proposedCreates = @(& $uniqueWorkers $Report['creates']).Count
        proposedOffboarding = @(& $uniqueWorkers @($Report['disables']) + @($Report['graveyardMoves'])).Count
        mappingCount = @($MappingConfig.mappings).Count
        deletionPassSkipped = [bool]$DeletionPassSkipped
        operatorActionCases = [pscustomobject]@{
            quarantinedWorkers = @(@($Report['quarantined']) | Where-Object { $_.PSObject.Properties.Name -contains 'reviewCaseType' -and "$($_.reviewCaseType)" -eq 'QuarantinedWorker' }).Count
            unresolvedManagers = @(@($Report['quarantined']) | Where-Object { $_.PSObject.Properties.Name -contains 'reviewCaseType' -and "$($_.reviewCaseType)" -eq 'UnresolvedManager' }).Count
            rehireCases = @(@($Report['manualReview']) | Where-Object { $_.PSObject.Properties.Name -contains 'reviewCaseType' -and "$($_.reviewCaseType)" -eq 'RehireCase' }).Count
        }
    }
}

function Set-SyncFactorsTrackedWorkerState {
    [CmdletBinding()]
    param(
        [pscustomobject]$State,
        [System.Collections.IDictionary]$Report,
        [string]$WorkerId,
        [pscustomobject]$WorkerState
    )

    $before = Get-SyncFactorsWorkerStateSnapshot -State $State -WorkerId $WorkerId
    Set-SyncFactorsWorkerState -State $State -WorkerId $WorkerId -WorkerState $WorkerState
    $after = Get-SyncFactorsWorkerStateSnapshot -State $State -WorkerId $WorkerId
    Add-SyncFactorsReportOperation -Report $Report -OperationType 'SetWorkerState' -WorkerId $WorkerId -TargetType 'SyncState' -Target @{ workerId = $WorkerId } -Before $before -After $after | Out-Null
}

function Set-SyncFactorsTrackedCheckpoint {
    [CmdletBinding()]
    param(
        [pscustomobject]$State,
        [System.Collections.IDictionary]$Report,
        [string]$Checkpoint
    )

    $before = Convert-ToSyncFactorsSerializable -Value $State.checkpoint
    $State.checkpoint = $Checkpoint
    Add-SyncFactorsReportOperation -Report $Report -OperationType 'SetCheckpoint' -WorkerId '__checkpoint__' -TargetType 'SyncState' -Target @{ key = 'checkpoint' } -Before $before -After $Checkpoint | Out-Null
}

function Set-SyncFactorsManagerAttributeIfPossible {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [pscustomobject]$Worker,
        [hashtable]$Changes,
        [System.Collections.IDictionary]$Report,
        [string]$WorkerId,
        [switch]$MatchedExistingUser
    )

    $managerProperty = $Worker.PSObject.Properties['managerEmployeeId']
    if (-not $managerProperty -or [string]::IsNullOrWhiteSpace("$($managerProperty.Value)")) {
        return $true
    }

    $managerEmployeeId = "$($managerProperty.Value)"
    $manager = Get-SyncFactorsTargetUser -Config $Config -WorkerId $managerEmployeeId
    if ($manager) {
        $Changes['manager'] = $manager.DistinguishedName
        return $true
    }

    $entry = @{
        workerId = $WorkerId
        reason = 'ManagerNotResolved'
        managerEmployeeId = $managerEmployeeId
        matchedExistingUser = [bool]$MatchedExistingUser
    }
    foreach ($property in (Get-SyncFactorsManualReviewMetadata -ReviewCaseType 'UnresolvedManager' -Reason 'ManagerNotResolved' -ManagerEmployeeId $managerEmployeeId).GetEnumerator()) {
        $entry[$property.Key] = $property.Value
    }
    Add-SyncFactorsReportEntry -Report $Report -Bucket 'quarantined' -Entry $entry
    return $false
}

function Assert-SyncFactorsSafetyThreshold {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [string]$ThresholdName,
        [Parameter(Mandatory)]
        [int]$CurrentCount,
        [Parameter(Mandatory)]
        [hashtable]$Entry
    )

    if (-not $Config.safety) {
        return
    }

    $limit = $Config.safety.$ThresholdName
    if ($null -eq $limit -or "$limit" -eq '') {
        return
    }

    if (($CurrentCount + 1) -le [int]$limit) {
        return
    }

    Add-SyncFactorsReportEntry -Report $Report -Bucket 'guardrailFailures' -Entry $Entry
    throw "Safety threshold '$ThresholdName' exceeded."
}

function Test-SyncFactorsCreateConflicts {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [string]$WorkerId,
        [hashtable]$Changes,
        [System.Collections.IDictionary]$Report
    )

    $conflicts = @()

    if ($Changes.ContainsKey('SamAccountName')) {
        $samMatches = Get-SyncFactorsUserBySamAccountName -Config $Config -SamAccountName $Changes['SamAccountName']
        if ((Get-SyncFactorsCollectionCount -Value $samMatches) -gt 0) {
            $conflicts += [pscustomobject]@{
                workerId = $WorkerId
                reason = 'SamAccountNameCollision'
                value = $Changes['SamAccountName']
            }
        }
    }

    if ($Changes.ContainsKey('UserPrincipalName')) {
        $upnMatches = Get-SyncFactorsUserByUserPrincipalName -Config $Config -UserPrincipalName $Changes['UserPrincipalName']
        if ((Get-SyncFactorsCollectionCount -Value $upnMatches) -gt 0) {
            $conflicts += [pscustomobject]@{
                workerId = $WorkerId
                reason = 'UserPrincipalNameCollision'
                value = $Changes['UserPrincipalName']
            }
        }
    }

    foreach ($conflict in $conflicts) {
        Add-SyncFactorsReportEntry -Report $Report -Bucket 'conflicts' -Entry @{
            workerId = $conflict.workerId
            reason = $conflict.reason
            value = $conflict.value
        }
    }

    return ($conflicts.Count -gt 0)
}

function Invoke-SyncFactorsOffboarding {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [pscustomobject]$User,
        [pscustomobject]$Worker,
        [pscustomobject]$State,
        [System.Collections.IDictionary]$Report,
        [switch]$DryRun,
        [switch]$ReviewMode,
        [switch]$BypassApprovalMode
    )

    $workerId = Get-SyncFactorsWorkerIdentityValue -Worker $Worker -Config $Config
    $userTarget = Get-SyncFactorsUserTargetDescriptor -WorkerId $workerId -User $User
    $approvalActions = [System.Collections.Generic.List[string]]::new()
    if (Test-SyncFactorsApprovalRequired -Config $Config -Action 'DisableUser' -BypassApprovalMode:$BypassApprovalMode) {
        $approvalActions.Add('DisableUser')
    }
    if (
        $User.DistinguishedName -notlike "*$($Config.ad.graveyardOu)" -and
        (Test-SyncFactorsApprovalRequired -Config $Config -Action 'MoveToGraveyardOu' -BypassApprovalMode:$BypassApprovalMode)
    ) {
        $approvalActions.Add('MoveToGraveyardOu')
    }

    if (-not $DryRun -and -not $ReviewMode -and $approvalActions.Count -gt 0) {
        Add-SyncFactorsApprovalManualReviewEntry -Config $Config -Report $Report -WorkerId $workerId -ApprovalActions @($approvalActions) -User $User -TargetOu $Config.ad.graveyardOu
        return 'ApprovalQueued'
    }

    Assert-SyncFactorsSafetyThreshold -Config $Config -Report $Report -ThresholdName 'maxDisablesPerRun' -CurrentCount @($Report.disables).Count -Entry @{
        workerId = $workerId
        threshold = 'maxDisablesPerRun'
        attemptedCount = @($Report.disables).Count + 1
    }

    $disableBefore = [pscustomobject]@{ enabled = [bool]$User.Enabled }
    Disable-SyncFactorsUser -Config $Config -User $User -DryRun:$DryRun
    Add-SyncFactorsReportEntry -Report $Report -Bucket 'disables' -Entry @{
        workerId = $workerId
        samAccountName = $User.SamAccountName
        currentEnabled = [bool]$User.Enabled
        currentDistinguishedName = $User.DistinguishedName
        reviewCategory = if ($ReviewMode) { 'ExistingUserOffboarding' } else { $null }
    }
    Add-SyncFactorsReportOperation -Report $Report -OperationType 'DisableUser' -WorkerId $workerId -Bucket 'disables' -Target $userTarget -Before $disableBefore -After ([pscustomobject]@{ enabled = $false }) | Out-Null

    $currentUser = $User
    if (-not $DryRun -and $User.ObjectGuid) {
        $currentUser = Get-SyncFactorsUserByObjectGuid -Config $Config -ObjectGuid $User.ObjectGuid.Guid
    }

    if ($currentUser.DistinguishedName -notlike "*$($Config.ad.graveyardOu)") {
        $moveBefore = [pscustomobject]@{
            distinguishedName = $currentUser.DistinguishedName
            parentOu = Get-SyncFactorsParentOuFromDistinguishedName -DistinguishedName $currentUser.DistinguishedName
        }
        Move-SyncFactorsUser -Config $Config -User $currentUser -TargetOu $Config.ad.graveyardOu -DryRun:$DryRun
        Add-SyncFactorsReportEntry -Report $Report -Bucket 'graveyardMoves' -Entry @{
            workerId = $workerId
            samAccountName = $currentUser.SamAccountName
            targetOu = $Config.ad.graveyardOu
            currentDistinguishedName = $currentUser.DistinguishedName
            reviewCategory = if ($ReviewMode) { 'ExistingUserOffboarding' } else { $null }
        }
        Add-SyncFactorsReportOperation -Report $Report -OperationType 'MoveUser' -WorkerId $workerId -Bucket 'graveyardMoves' -Target (Get-SyncFactorsUserTargetDescriptor -WorkerId $workerId -User $currentUser) -Before $moveBefore -After ([pscustomobject]@{ targetOu = $Config.ad.graveyardOu }) | Out-Null
    }

    if (-not $ReviewMode) {
        Set-SyncFactorsTrackedWorkerState -State $State -Report $Report -WorkerId $workerId -WorkerState ([pscustomobject]@{
            adObjectGuid = $User.ObjectGuid.Guid
            distinguishedName = $User.DistinguishedName
            suppressed = $true
            firstDisabledAt = (Get-Date).ToString('o')
            deleteAfter = (Get-Date).AddDays([int]$Config.sync.deletionRetentionDays).ToString('o')
            lastSeenStatus = Get-SyncFactorsWorkerStatusValue -Worker $Worker
        })
    }

    return 'Applied'
}

function Invoke-SyncFactorsDeletionPass {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [pscustomobject]$State,
        [System.Collections.IDictionary]$Report,
        [switch]$DryRun,
        [switch]$ReviewMode,
        [switch]$BypassApprovalMode
    )

    if ($ReviewMode) {
        return
    }

    Ensure-ActiveDirectoryModule
    foreach ($property in @(Get-SyncFactorsWorkerEntries -Workers $State.workers)) {
        $workerState = $property.Value
        $isSuppressed = $workerState.PSObject.Properties.Name -contains 'suppressed' -and [bool]$workerState.suppressed
        $deleteAfter = if ($workerState.PSObject.Properties.Name -contains 'deleteAfter') { $workerState.deleteAfter } else { $null }

        if (-not $isSuppressed -or -not $deleteAfter) {
            continue
        }

        if ((Get-Date $deleteAfter) -gt (Get-Date)) {
            continue
        }

        Assert-SyncFactorsSafetyThreshold -Config $Config -Report $Report -ThresholdName 'maxDeletionsPerRun' -CurrentCount @($Report.deletions).Count -Entry @{
            workerId = $property.Name
            threshold = 'maxDeletionsPerRun'
            attemptedCount = @($Report.deletions).Count + 1
        }

        $user = Get-SyncFactorsUserByObjectGuid -Config $Config -ObjectGuid $workerState.adObjectGuid
        if (-not $user) {
            continue
        }

        $latestWorker = Get-SfWorkerById -Config $Config -WorkerId $property.Name
        if ($latestWorker -and (Test-SyncFactorsWorkerIsActive -Worker $latestWorker)) {
            $manualReviewEntry = @{
                workerId = $property.Name
                reason = 'RehireDetectedBeforeDelete'
                distinguishedName = $user.DistinguishedName
            }
            foreach ($propertyMetadata in (Get-SyncFactorsManualReviewMetadata -ReviewCaseType 'RehireCase' -Reason 'RehireDetectedBeforeDelete' -DistinguishedName $user.DistinguishedName).GetEnumerator()) {
                $manualReviewEntry[$propertyMetadata.Key] = $propertyMetadata.Value
            }
            Add-SyncFactorsReportEntry -Report $Report -Bucket 'manualReview' -Entry $manualReviewEntry
            continue
        }

        if (Test-SyncFactorsApprovalRequired -Config $Config -Action 'DeleteUser' -BypassApprovalMode:$BypassApprovalMode) {
            Add-SyncFactorsApprovalManualReviewEntry -Config $Config -Report $Report -WorkerId $property.Name -ApprovalActions @('DeleteUser') -User $user
            continue
        }

        $snapshot = if (-not $DryRun) { Get-SyncFactorsUserSnapshot -Config $Config -User $user } else { [pscustomobject]@{ samAccountName = $user.SamAccountName; objectGuid = $workerState.adObjectGuid } }
        Remove-SyncFactorsUser -Config $Config -User $user -DryRun:$DryRun
        Add-SyncFactorsReportEntry -Report $Report -Bucket 'deletions' -Entry @{
            workerId = $property.Name
            samAccountName = $user.SamAccountName
        }
        Add-SyncFactorsReportOperation -Report $Report -OperationType 'DeleteUser' -WorkerId $property.Name -Bucket 'deletions' -Target (Get-SyncFactorsUserTargetDescriptor -WorkerId $property.Name -User $user) -Before $snapshot -After $null | Out-Null
    }
}

function Test-SyncFactorsPreflight {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath,
        [Parameter(Mandatory)]
        [string]$MappingConfigPath
    )

    $resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
    $resolvedMappingConfigPath = (Resolve-Path -Path $MappingConfigPath).Path
    $config = Get-SyncFactorsConfig -Path $resolvedConfigPath
    $mapping = Get-SyncFactorsMappingConfig -Path $resolvedMappingConfigPath
    Ensure-ActiveDirectoryModule

    return [pscustomobject]@{
        success = $true
        configPath = $resolvedConfigPath
        mappingConfigPath = $resolvedMappingConfigPath
        identityField = $config.successFactors.query.identityField
        identityAttribute = $config.ad.identityAttribute
        statePath = $config.state.path
        stateDirectoryExists = Test-Path -Path (Split-Path -Path $config.state.path -Parent) -PathType Container
        reportDirectory = $config.reporting.outputDirectory
        reportDirectoryExists = Test-Path -Path $config.reporting.outputDirectory -PathType Container
        mappingCount = @($mapping.mappings).Count
    }
}

function Invoke-SyncFactorsRun {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath,
        [Parameter(Mandatory)]
        [string]$MappingConfigPath,
        [ValidateSet('Delta','Full','Review')]
        [string]$Mode = 'Delta',
        [switch]$DryRun,
        [string]$WorkerId,
        [switch]$BypassApprovalMode
    )

    if (-not [string]::IsNullOrWhiteSpace($WorkerId) -and $Mode -notin @('Full', 'Review')) {
        throw '-WorkerId is only supported with -Mode Full or -Mode Review.'
    }

    $resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
    $resolvedMappingConfigPath = (Resolve-Path -Path $MappingConfigPath).Path
    $config = Get-SyncFactorsConfig -Path $resolvedConfigPath
    $mappingConfig = Get-SyncFactorsMappingConfig -Path $resolvedMappingConfigPath
    $isReviewMode = Test-SyncFactorsReviewMode -Mode $Mode
    $isScopedWorkerPreview = -not [string]::IsNullOrWhiteSpace($WorkerId)
    $effectiveDryRun = ($DryRun -or $isReviewMode)
    $workerFetchMode = if ($isReviewMode) { 'Full' } else { $Mode }
    $reportOutputDirectory = Get-SyncFactorsReportOutputDirectory -Config $config -Mode $Mode
    $report = New-SyncFactorsReport `
        -Mode $Mode `
        -DryRun:$effectiveDryRun `
        -ConfigPath $resolvedConfigPath `
        -MappingConfigPath $resolvedMappingConfigPath `
        -StatePath $config.state.path `
        -ArtifactType (Get-SyncFactorsArtifactType -Mode $Mode -WorkerId $WorkerId) `
        -WorkerScope $(if ($isScopedWorkerPreview) {
            [pscustomobject]@{
                identityField = $config.successFactors.query.identityField
                workerId = $WorkerId
            }
        } else {
            $null
        })
    Update-SyncFactorsRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'Initializing' -LastAction 'Starting sync run.'
    Update-SyncFactorsRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'LoadingState' -LastAction 'Loading sync state.'
    $state = Get-SyncFactorsState -Path $config.state.path
    $checkpoint = if ($workerFetchMode -eq 'Delta') { $state.checkpoint } else { $null }
    $reportPath = $null
    $workers = @()
    $processedWorkers = 0
    $totalWorkers = 0

    try {
        $fetchDescription = if ($isScopedWorkerPreview) {
            "Fetching SuccessFactors worker $WorkerId by $($config.successFactors.query.identityField)."
        } else {
            "Fetching SuccessFactors workers in $workerFetchMode mode."
        }
        Update-SyncFactorsRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'FetchingWorkers' -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -LastAction $fetchDescription
        Write-SyncFactorsLog -Message $fetchDescription
        if ($isScopedWorkerPreview) {
            $worker = Get-SfWorkerById -Config $config -WorkerId $WorkerId
            if (-not $worker) {
                throw "Worker '$WorkerId' was not found in SuccessFactors using identity field '$($config.successFactors.query.identityField)'."
            }

            $workers = @($worker)
        } else {
            $workers = @(Get-SfWorkers -Config $config -Mode $workerFetchMode -Checkpoint $checkpoint)
        }
        $totalWorkers = @($workers).Count
        Update-SyncFactorsRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'ProcessingWorkers' -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -LastAction "Fetched $totalWorkers workers."
        $workerCountsByIdentity = @{}
        foreach ($worker in $workers) {
            $candidateWorkerId = Get-SyncFactorsWorkerIdentityValue -Worker $worker -Config $config
            if ([string]::IsNullOrWhiteSpace($candidateWorkerId)) {
                continue
            }

            if (-not $workerCountsByIdentity.ContainsKey($candidateWorkerId)) {
                $workerCountsByIdentity[$candidateWorkerId] = 0
            }
            $workerCountsByIdentity[$candidateWorkerId] += 1
        }

        foreach ($worker in $workers) {
            $workerId = Get-SyncFactorsWorkerIdentityValue -Worker $worker -Config $config
            $currentWorkerId = if ([string]::IsNullOrWhiteSpace($workerId)) { $null } else { $workerId }
            $lastAction = if ($currentWorkerId) { "Evaluating worker $currentWorkerId." } else { 'Evaluating worker with missing identity.' }
            Update-SyncFactorsRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'ProcessingWorkers' -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -CurrentWorkerId $currentWorkerId -LastAction $lastAction

            try {
                if ([string]::IsNullOrWhiteSpace($workerId)) {
                    $quarantineEntry = @{
                        workerId = $null
                        reason = 'MissingEmployeeId'
                    }
                    foreach ($property in (Get-SyncFactorsManualReviewMetadata -ReviewCaseType 'QuarantinedWorker' -Reason 'MissingEmployeeId').GetEnumerator()) {
                        $quarantineEntry[$property.Key] = $property.Value
                    }
                    Add-SyncFactorsReportEntry -Report $report -Bucket 'quarantined' -Entry $quarantineEntry
                    $lastAction = 'Quarantined worker with missing employee ID.'
                    continue
                }

                if ($workerCountsByIdentity[$workerId] -gt 1) {
                    Add-SyncFactorsReportEntry -Report $report -Bucket 'conflicts' -Entry @{
                        workerId = $workerId
                        reason = 'DuplicateWorkerId'
                        occurrences = $workerCountsByIdentity[$workerId]
                    }
                    $lastAction = "Detected duplicate worker identity for $workerId."
                    continue
                }

                $existingUserMatches = Get-SyncFactorsTargetUser -Config $config -WorkerId $workerId
                if ((Get-SyncFactorsCollectionCount -Value $existingUserMatches) -gt 1) {
                    Add-SyncFactorsReportEntry -Report $report -Bucket 'conflicts' -Entry @{
                        workerId = $workerId
                        reason = 'DuplicateAdIdentityMatch'
                        occurrences = Get-SyncFactorsCollectionCount -Value $existingUserMatches
                    }
                    $lastAction = "Detected duplicate AD identity match for $workerId."
                    continue
                }

                $existingUser = Get-SyncFactorsSingleResult -Value $existingUserMatches
                $workerState = Get-SyncFactorsWorkerState -State $state -WorkerId $workerId
                $workerStateIsSuppressed = $workerState -and $workerState.PSObject.Properties.Name -contains 'suppressed' -and [bool]$workerState.suppressed

                if ($workerStateIsSuppressed -and (Test-SyncFactorsWorkerIsActive -Worker $worker)) {
                    $manualReviewEntry = @{
                        workerId = $workerId
                        reason = 'RehireDetected'
                        distinguishedName = $workerState.distinguishedName
                    }
                    foreach ($property in (Get-SyncFactorsManualReviewMetadata -ReviewCaseType 'RehireCase' -Reason 'RehireDetected' -DistinguishedName $workerState.distinguishedName).GetEnumerator()) {
                        $manualReviewEntry[$property.Key] = $property.Value
                    }
                    if ($isReviewMode) {
                        $manualReviewEntry['matchedExistingUser'] = $true
                    }
                    Add-SyncFactorsReportEntry -Report $report -Bucket 'manualReview' -Entry $manualReviewEntry
                    $lastAction = "Queued rehire for manual review for $workerId."
                    continue
                }

                if (-not (Test-SyncFactorsWorkerIsActive -Worker $worker)) {
                    if ($existingUser) {
                        $offboardingResult = Invoke-SyncFactorsOffboarding -Config $config -User $existingUser -Worker $worker -State $state -Report $report -DryRun:$effectiveDryRun -ReviewMode:$isReviewMode -BypassApprovalMode:$BypassApprovalMode
                        $lastAction = if ("$offboardingResult" -eq 'ApprovalQueued') {
                            "Queued offboarding approval for $workerId."
                        } else {
                            "Offboarded inactive worker $workerId."
                        }
                    } else {
                        $lastAction = "Skipped inactive worker $workerId with no matching AD user."
                    }
                    continue
                }

                $attributeResult = Get-SyncFactorsAttributeChanges -Worker $worker -ExistingUser $existingUser -MappingConfig $mappingConfig
                $reviewEvaluation = if ($isReviewMode) { Get-SyncFactorsMappingEvaluation -Worker $worker -ExistingUser $existingUser -MappingConfig $mappingConfig } else { $null }
                if ($attributeResult.MissingRequired.Count -gt 0) {
                    $quarantineEntry = @{
                        workerId = $workerId
                        reason = 'MissingRequiredData'
                        fields = $attributeResult.MissingRequired
                    }
                    foreach ($property in (Get-SyncFactorsManualReviewMetadata -ReviewCaseType 'QuarantinedWorker' -Reason 'MissingRequiredData' -Fields $attributeResult.MissingRequired).GetEnumerator()) {
                        $quarantineEntry[$property.Key] = $property.Value
                    }
                    if ($isReviewMode -and $reviewEvaluation) {
                        $quarantineEntry['attributeRows'] = Get-SyncFactorsReviewAttributeRows -Rows $reviewEvaluation.Rows
                        $quarantineEntry['matchedExistingUser'] = [bool]$existingUser
                    }
                    Add-SyncFactorsReportEntry -Report $report -Bucket 'quarantined' -Entry $quarantineEntry
                    $lastAction = "Quarantined worker $workerId for missing required data."
                    continue
                }

                $changes = @{}
                foreach ($key in $attributeResult.Changes.Keys) {
                    $changes[$key] = $attributeResult.Changes[$key]
                }

                $existingIdentityValue = $null
                if ($existingUser) {
                    $existingIdentityProperty = $existingUser.PSObject.Properties[$config.ad.identityAttribute]
                    if ($existingIdentityProperty) {
                        $existingIdentityValue = "$($existingIdentityProperty.Value)"
                    }
                }

                if (-not $existingUser -or $existingIdentityValue -ne $workerId) {
                    $changes[$config.ad.identityAttribute] = $workerId
                }
                if (-not (Set-SyncFactorsManagerAttributeIfPossible -Config $config -Worker $worker -Changes $changes -Report $report -WorkerId $workerId -MatchedExistingUser:([bool]$existingUser))) {
                    $lastAction = "Quarantined worker $workerId because the manager could not be resolved."
                    continue
                }
                $targetOu = Resolve-SyncFactorsTargetOu -Config $config -Worker $worker
                $reviewAttributeRows = if ($isReviewMode -and $reviewEvaluation) { @(Get-SyncFactorsReviewAttributeRows -Rows $reviewEvaluation.Rows) } else { @() }
                $reviewChangedRows = if ($isReviewMode -and $reviewEvaluation) { @(Get-SyncFactorsChangedMappingRows -Rows $reviewEvaluation.Rows) } else { @() }

                if (-not $existingUser) {
                    if (Test-SyncFactorsCreateConflicts -Config $config -WorkerId $workerId -Changes $changes -Report $report) {
                        $lastAction = "Skipped create for $workerId because of a conflict."
                        continue
                    }

                    Assert-SyncFactorsSafetyThreshold -Config $config -Report $report -ThresholdName 'maxCreatesPerRun' -CurrentCount @($report.creates).Count -Entry @{
                        workerId = $workerId
                        threshold = 'maxCreatesPerRun'
                        attemptedCount = @($report.creates).Count + 1
                    }

                    $createdUser = New-SyncFactorsUser -Config $config -Worker $worker -WorkerId $workerId -Attributes $changes -DryRun:$effectiveDryRun
                    $createEntry = @{
                        workerId = $workerId
                        samAccountName = if ($createdUser.SamAccountName) { $createdUser.SamAccountName } else { $workerId }
                    }
                    if ($isReviewMode) {
                        $createEntry['reviewCategory'] = 'NewUser'
                        $createEntry['targetOu'] = $targetOu
                        $createEntry['attributeRows'] = $reviewAttributeRows
                        $createEntry['changedAttributeDetails'] = $reviewChangedRows
                        $createEntry['proposedAttributes'] = Convert-ToSyncFactorsSerializable -Value $changes
                        $createEntry['proposedEnable'] = [bool](Test-SyncFactorsWorkerIsPrehireEligible -Worker $worker -EnableBeforeDays $config.sync.enableBeforeStartDays)
                    }
                    Add-SyncFactorsReportEntry -Report $report -Bucket 'creates' -Entry $createEntry

                    if (-not $effectiveDryRun -and $createdUser) {
                        try {
                            $createAfter = Get-SyncFactorsUserSnapshot -Config $config -User $createdUser
                        } catch {
                            $createAfter = Convert-ToSyncFactorsSerializable -Value $createdUser
                        }
                    } else {
                        $createAfter = Convert-ToSyncFactorsSerializable -Value $createdUser
                    }
                    Add-SyncFactorsReportOperation -Report $report -OperationType 'CreateUser' -WorkerId $workerId -Bucket 'creates' -Target (Get-SyncFactorsUserTargetDescriptor -WorkerId $workerId -User $createdUser) -Before $null -After $createAfter | Out-Null

                    if (-not $effectiveDryRun -and $createdUser) {
                        if (Test-SyncFactorsWorkerIsPrehireEligible -Worker $worker -EnableBeforeDays $config.sync.enableBeforeStartDays) {
                            Enable-SyncFactorsUser -Config $config -User $createdUser -DryRun:$effectiveDryRun
                            Add-SyncFactorsReportEntry -Report $report -Bucket 'enables' -Entry @{
                                workerId = $workerId
                                samAccountName = $createdUser.SamAccountName
                                licensingGroups = @()
                            }
                            Add-SyncFactorsReportOperation -Report $report -OperationType 'EnableUser' -WorkerId $workerId -Bucket 'enables' -Target (Get-SyncFactorsUserTargetDescriptor -WorkerId $workerId -User $createdUser) -Before ([pscustomobject]@{ enabled = $false }) -After ([pscustomobject]@{ enabled = $true }) | Out-Null

                            $licensedGroups = @(Add-SyncFactorsUserToConfiguredGroups -Config $config -User $createdUser -DryRun:$effectiveDryRun)
                            if ($licensedGroups.Count -gt 0) {
                                $report.enables[-1].licensingGroups = $licensedGroups
                                Add-SyncFactorsReportOperation -Report $report -OperationType 'AddGroupMembership' -WorkerId $workerId -Bucket 'enables' -Target (Get-SyncFactorsUserTargetDescriptor -WorkerId $workerId -User $createdUser) -Before ([pscustomobject]@{ groupsAdded = @() }) -After ([pscustomobject]@{ groupsAdded = $licensedGroups }) | Out-Null
                            }
                        }

                        if (-not $isReviewMode) {
                            Set-SyncFactorsTrackedWorkerState -State $state -Report $report -WorkerId $workerId -WorkerState ([pscustomobject]@{
                                adObjectGuid = $createdUser.ObjectGuid.Guid
                                distinguishedName = $createdUser.DistinguishedName
                                suppressed = $false
                                firstDisabledAt = $null
                                deleteAfter = $null
                                lastSeenStatus = Get-SyncFactorsWorkerStatusValue -Worker $worker
                            })
                        }
                    }

                    $lastAction = "Created user for worker $workerId."
                    continue
                }

                if ($changes.Count -gt 0) {
                    $beforeAttributes = Get-SyncFactorsAttributeBeforeValues -User $existingUser -Changes $changes
                    Set-SyncFactorsUserAttributes -Config $config -User $existingUser -Changes $changes -DryRun:$effectiveDryRun | Out-Null
                    $updateEntry = @{
                        workerId = $workerId
                        samAccountName = $existingUser.SamAccountName
                        changedAttributes = $changes.Keys
                    }
                    if ($isReviewMode) {
                        $updateEntry['reviewCategory'] = 'ExistingUserChanges'
                        $updateEntry['attributeRows'] = $reviewAttributeRows
                        $updateEntry['changedAttributeDetails'] = $reviewChangedRows
                        $updateEntry['targetOu'] = $targetOu
                        $updateEntry['currentDistinguishedName'] = $existingUser.DistinguishedName
                        $updateEntry['currentEnabled'] = [bool]$existingUser.Enabled
                    }
                    Add-SyncFactorsReportEntry -Report $report -Bucket 'updates' -Entry $updateEntry
                    Add-SyncFactorsReportOperation -Report $report -OperationType 'UpdateAttributes' -WorkerId $workerId -Bucket 'updates' -Target (Get-SyncFactorsUserTargetDescriptor -WorkerId $workerId -User $existingUser) -Before $beforeAttributes -After (Convert-ToSyncFactorsSerializable -Value $changes) | Out-Null
                    $lastAction = "Updated attributes for worker $workerId."
                } else {
                    $unchangedEntry = @{
                        workerId = $workerId
                        samAccountName = $existingUser.SamAccountName
                    }
                    if ($isReviewMode) {
                        $unchangedEntry['reviewCategory'] = 'ExistingUserAligned'
                        $unchangedEntry['attributeRows'] = $reviewAttributeRows
                        $unchangedEntry['noChangeReason'] = 'Mapped attributes already align with AD.'
                        $unchangedEntry['targetOu'] = $targetOu
                        $unchangedEntry['currentDistinguishedName'] = $existingUser.DistinguishedName
                        $unchangedEntry['currentEnabled'] = [bool]$existingUser.Enabled
                    }
                    Add-SyncFactorsReportEntry -Report $report -Bucket 'unchanged' -Entry $unchangedEntry
                    $lastAction = "No attribute changes for worker $workerId."
                }

                $currentUser = $existingUser
                if ($currentUser.DistinguishedName -notlike "*$targetOu") {
                    $moveBefore = [pscustomobject]@{
                        distinguishedName = $currentUser.DistinguishedName
                        parentOu = Get-SyncFactorsParentOuFromDistinguishedName -DistinguishedName $currentUser.DistinguishedName
                    }
                    Move-SyncFactorsUser -Config $config -User $currentUser -TargetOu $targetOu -DryRun:$effectiveDryRun
                    $moveEntry = @{
                        workerId = $workerId
                        samAccountName = $currentUser.SamAccountName
                        targetOu = $targetOu
                    }
                    if ($isReviewMode) {
                        $moveEntry['reviewCategory'] = 'ExistingUserPlacement'
                        $moveEntry['currentDistinguishedName'] = $currentUser.DistinguishedName
                    }
                    Add-SyncFactorsReportEntry -Report $report -Bucket 'graveyardMoves' -Entry $moveEntry
                    Add-SyncFactorsReportOperation -Report $report -OperationType 'MoveUser' -WorkerId $workerId -Bucket 'graveyardMoves' -Target (Get-SyncFactorsUserTargetDescriptor -WorkerId $workerId -User $currentUser) -Before $moveBefore -After ([pscustomobject]@{ targetOu = $targetOu }) | Out-Null
                    if (-not $effectiveDryRun) {
                        $currentUser = Get-SyncFactorsUserByObjectGuid -Config $config -ObjectGuid $currentUser.ObjectGuid.Guid
                    }
                    $lastAction = "Moved worker $workerId to target OU."
                }

                if (-not $currentUser.Enabled -and (Test-SyncFactorsWorkerIsPrehireEligible -Worker $worker -EnableBeforeDays $config.sync.enableBeforeStartDays)) {
                    Enable-SyncFactorsUser -Config $config -User $currentUser -DryRun:$effectiveDryRun
                    $licensedGroups = @(Add-SyncFactorsUserToConfiguredGroups -Config $config -User $currentUser -DryRun:$effectiveDryRun)
                    $enableEntry = @{
                        workerId = $workerId
                        samAccountName = $currentUser.SamAccountName
                        licensingGroups = $licensedGroups
                    }
                    if ($isReviewMode) {
                        $enableEntry['reviewCategory'] = 'ExistingUserEnable'
                        $enableEntry['currentEnabled'] = [bool]$currentUser.Enabled
                    }
                    Add-SyncFactorsReportEntry -Report $report -Bucket 'enables' -Entry $enableEntry
                    Add-SyncFactorsReportOperation -Report $report -OperationType 'EnableUser' -WorkerId $workerId -Bucket 'enables' -Target (Get-SyncFactorsUserTargetDescriptor -WorkerId $workerId -User $currentUser) -Before ([pscustomobject]@{ enabled = $false }) -After ([pscustomobject]@{ enabled = $true }) | Out-Null
                    if ($licensedGroups.Count -gt 0) {
                        Add-SyncFactorsReportOperation -Report $report -OperationType 'AddGroupMembership' -WorkerId $workerId -Bucket 'enables' -Target (Get-SyncFactorsUserTargetDescriptor -WorkerId $workerId -User $currentUser) -Before ([pscustomobject]@{ groupsAdded = @() }) -After ([pscustomobject]@{ groupsAdded = $licensedGroups }) | Out-Null
                    }
                    $lastAction = "Enabled worker $workerId."
                }

                if (-not $isReviewMode) {
                    Set-SyncFactorsTrackedWorkerState -State $state -Report $report -WorkerId $workerId -WorkerState ([pscustomobject]@{
                        adObjectGuid = $currentUser.ObjectGuid.Guid
                        distinguishedName = $currentUser.DistinguishedName
                        suppressed = $false
                        firstDisabledAt = $null
                        deleteAfter = $null
                        lastSeenStatus = Get-SyncFactorsWorkerStatusValue -Worker $worker
                    })
                    if ($lastAction -eq "No attribute changes for worker $workerId.") {
                        $lastAction = "Refreshed tracked state for worker $workerId."
                    }
                }
            } finally {
                $processedWorkers += 1
                Update-SyncFactorsRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'ProcessingWorkers' -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -CurrentWorkerId $currentWorkerId -LastAction $lastAction
            }
        }

        if (-not $isReviewMode) {
            Update-SyncFactorsRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'DeletionPass' -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -LastAction 'Running deletion pass.'
            Invoke-SyncFactorsDeletionPass -Config $config -State $state -Report $report -DryRun:$effectiveDryRun -ReviewMode:$isReviewMode -BypassApprovalMode:$BypassApprovalMode
        }

        Update-SyncFactorsRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'SavingState' -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -LastAction 'Persisting checkpoint and state.'
        if (-not $isReviewMode) {
            Set-SyncFactorsTrackedCheckpoint -State $state -Report $report -Checkpoint ((Get-Date).ToString('yyyy-MM-ddTHH:mm:ss'))
        }
        if (-not $effectiveDryRun -and -not $isReviewMode) {
            Save-SyncFactorsState -State $state -Path $config.state.path
        }

        if ($isReviewMode) {
            Set-SyncFactorsReviewSummary -Report $report -MappingConfig $mappingConfig -DeletionPassSkipped
        }
        $report.status = 'Succeeded'
    } catch {
        $report.status = 'Failed'
        $report.errorMessage = $_.Exception.Message
        $report.failedAt = (Get-Date).ToString('o')
        throw
    } finally {
        Update-SyncFactorsRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'WritingReport' -Status $report.status -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -LastAction 'Writing sync report.' -ErrorMessage $report.errorMessage
        $reportPath = Save-SyncFactorsReport -Report $report -Directory $reportOutputDirectory -Mode $Mode
        $finalStage = if ($report.status -eq 'Succeeded') { 'Completed' } else { 'Failed' }
        Update-SyncFactorsRuntimeStatus -Report $report -StatePath $config.state.path -Stage $finalStage -Status $report.status -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -LastAction "Run $($report.status)." -CompletedAt $report.completedAt -ErrorMessage $report.errorMessage
    }

    Write-SyncFactorsLog -Message "Run completed. Report written to $reportPath"
    return $reportPath
}

Export-ModuleMember -Function Write-SyncFactorsLog, Test-SyncFactorsWorkerIsActive, Get-SyncFactorsWorkerIdentityValue, Test-SyncFactorsWorkerIsPrehireEligible, Test-SyncFactorsPreflight, Invoke-SyncFactorsRun
