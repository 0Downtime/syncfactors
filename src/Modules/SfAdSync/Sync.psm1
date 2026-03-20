Set-StrictMode -Version Latest

$moduleRoot = $PSScriptRoot
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'State.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'Reporting.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'Monitoring.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'Mapping.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'SuccessFactors.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'ActiveDirectorySync.psm1') -Force -DisableNameChecking

function Write-SfAdSyncLog {
    [CmdletBinding()]
    param(
        [string]$Level = 'INFO',
        [string]$Message
    )

    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Host "[$timestamp][$Level] $Message"
}

function Update-SfAdRuntimeStatus {
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

    Write-SfAdRuntimeStatusSnapshot -Report $Report -StatePath $StatePath -Stage $Stage -Status $Status -ProcessedWorkers $ProcessedWorkers -TotalWorkers $TotalWorkers -CurrentWorkerId $CurrentWorkerId -LastAction $LastAction -CompletedAt $CompletedAt -ErrorMessage $ErrorMessage | Out-Null
}

function Convert-ToSfAdSerializable {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $result[$key] = Convert-ToSfAdSerializable -Value $Value[$key]
        }
        return [pscustomobject]$result
    }

    if ($Value -is [System.Array]) {
        return @($Value | ForEach-Object { Convert-ToSfAdSerializable -Value $_ })
    }

    if ($Value -is [datetime]) {
        return $Value.ToString('o')
    }

    $properties = @($Value.PSObject.Properties)
    if ($properties.Count -gt 0 -and -not ($Value -is [string])) {
        $result = [ordered]@{}
        foreach ($property in $properties) {
            $result[$property.Name] = Convert-ToSfAdSerializable -Value $property.Value
        }
        return [pscustomobject]$result
    }

    return $Value
}

function Test-SfAdWorkerIsActive {
    [CmdletBinding()]
    param([pscustomobject]$Worker)
    return @('active','enabled','a') -contains "$($Worker.status)".ToLowerInvariant()
}

function Get-SfAdWorkerIdentityValue {
    [CmdletBinding()]
    param(
        [pscustomobject]$Worker,
        [pscustomobject]$Config
    )

    $identityField = $Config.successFactors.query.identityField
    return "$($Worker.PSObject.Properties[$identityField].Value)"
}

function Test-SfAdWorkerIsPrehireEligible {
    [CmdletBinding()]
    param(
        [pscustomobject]$Worker,
        [int]$EnableBeforeDays
    )

    if (-not $Worker.startDate) {
        return $false
    }

    $startDate = Get-Date $Worker.startDate
    return $startDate.Date -le (Get-Date).Date.AddDays($EnableBeforeDays)
}

function Get-SfAdAttributeBeforeValues {
    [CmdletBinding()]
    param(
        [pscustomobject]$User,
        [hashtable]$Changes
    )

    $before = [ordered]@{}
    foreach ($key in $Changes.Keys) {
        $property = $User.PSObject.Properties[$key]
        $before[$key] = if ($property) { Convert-ToSfAdSerializable -Value $property.Value } else { $null }
    }

    return [pscustomobject]$before
}

function Get-SfAdUserTargetDescriptor {
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

function Get-SfAdWorkerStateSnapshot {
    [CmdletBinding()]
    param(
        [pscustomobject]$State,
        [string]$WorkerId
    )

    $workerState = Get-SfAdWorkerState -State $State -WorkerId $WorkerId
    if (-not $workerState) {
        return $null
    }

    return Convert-ToSfAdSerializable -Value $workerState
}

function Get-SfAdCollectionCount {
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

function Get-SfAdSingleResult {
    [CmdletBinding()]
    param($Value)

    if ($Value -is [System.Array]) {
        return @($Value)[0]
    }

    return $Value
}

function Test-SfAdReviewMode {
    [CmdletBinding()]
    param(
        [string]$Mode
    )

    return "$Mode" -eq 'Review'
}

function Get-SfAdArtifactType {
    [CmdletBinding()]
    param(
        [string]$Mode,
        [string]$WorkerId
    )

    if (Test-SfAdReviewMode -Mode $Mode) {
        if (-not [string]::IsNullOrWhiteSpace($WorkerId)) {
            return 'WorkerPreview'
        }

        return 'FirstSyncReview'
    }

    return 'SyncReport'
}

function Get-SfAdChangedMappingRows {
    [CmdletBinding()]
    param($Rows)

    return @(
        @($Rows) | Where-Object { $_.changed } | ForEach-Object {
            [pscustomobject]@{
                sourceField = $_.sourceField
                targetAttribute = $_.targetAttribute
                transform = $_.transform
                required = $_.required
                currentAdValue = Convert-ToSfAdSerializable -Value $_.currentAdValue
                proposedValue = Convert-ToSfAdSerializable -Value $_.proposedValue
            }
        }
    )
}

function Get-SfAdReviewAttributeRows {
    [CmdletBinding()]
    param($Rows)

    return @(
        @($Rows) | ForEach-Object {
            [pscustomobject]@{
                sourceField = $_.sourceField
                targetAttribute = $_.targetAttribute
                transform = $_.transform
                required = $_.required
                sourceValue = Convert-ToSfAdSerializable -Value $_.sourceValue
                currentAdValue = Convert-ToSfAdSerializable -Value $_.currentAdValue
                proposedValue = Convert-ToSfAdSerializable -Value $_.proposedValue
                changed = [bool]$_.changed
            }
        }
    )
}

function Get-SfAdReportOutputDirectory {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [string]$Mode
    )

    if (Test-SfAdReviewMode -Mode $Mode) {
        return $Config.reporting.reviewOutputDirectory
    }

    return $Config.reporting.outputDirectory
}

function Set-SfAdReviewSummary {
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
    }
}

function Set-SfAdTrackedWorkerState {
    [CmdletBinding()]
    param(
        [pscustomobject]$State,
        [System.Collections.IDictionary]$Report,
        [string]$WorkerId,
        [pscustomobject]$WorkerState
    )

    $before = Get-SfAdWorkerStateSnapshot -State $State -WorkerId $WorkerId
    Set-SfAdWorkerState -State $State -WorkerId $WorkerId -WorkerState $WorkerState
    $after = Get-SfAdWorkerStateSnapshot -State $State -WorkerId $WorkerId
    Add-SfAdReportOperation -Report $Report -OperationType 'SetWorkerState' -WorkerId $WorkerId -TargetType 'SyncState' -Target @{ workerId = $WorkerId } -Before $before -After $after | Out-Null
}

function Set-SfAdTrackedCheckpoint {
    [CmdletBinding()]
    param(
        [pscustomobject]$State,
        [System.Collections.IDictionary]$Report,
        [string]$Checkpoint
    )

    $before = Convert-ToSfAdSerializable -Value $State.checkpoint
    $State.checkpoint = $Checkpoint
    Add-SfAdReportOperation -Report $Report -OperationType 'SetCheckpoint' -WorkerId '__checkpoint__' -TargetType 'SyncState' -Target @{ key = 'checkpoint' } -Before $before -After $Checkpoint | Out-Null
}

function Set-SfAdManagerAttributeIfPossible {
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
    $manager = Get-SfAdTargetUser -Config $Config -WorkerId $managerEmployeeId
    if ($manager) {
        $Changes['manager'] = $manager.DistinguishedName
        return $true
    }

    Add-SfAdReportEntry -Report $Report -Bucket 'quarantined' -Entry @{
        workerId = $WorkerId
        reason = 'ManagerNotResolved'
        managerEmployeeId = $managerEmployeeId
        matchedExistingUser = [bool]$MatchedExistingUser
    }
    return $false
}

function Assert-SfAdSafetyThreshold {
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

    Add-SfAdReportEntry -Report $Report -Bucket 'guardrailFailures' -Entry $Entry
    throw "Safety threshold '$ThresholdName' exceeded."
}

function Test-SfAdCreateConflicts {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [string]$WorkerId,
        [hashtable]$Changes,
        [System.Collections.IDictionary]$Report
    )

    $conflicts = @()

    if ($Changes.ContainsKey('SamAccountName')) {
        $samMatches = Get-SfAdUserBySamAccountName -Config $Config -SamAccountName $Changes['SamAccountName']
        if ((Get-SfAdCollectionCount -Value $samMatches) -gt 0) {
            $conflicts += [pscustomobject]@{
                workerId = $WorkerId
                reason = 'SamAccountNameCollision'
                value = $Changes['SamAccountName']
            }
        }
    }

    if ($Changes.ContainsKey('UserPrincipalName')) {
        $upnMatches = Get-SfAdUserByUserPrincipalName -Config $Config -UserPrincipalName $Changes['UserPrincipalName']
        if ((Get-SfAdCollectionCount -Value $upnMatches) -gt 0) {
            $conflicts += [pscustomobject]@{
                workerId = $WorkerId
                reason = 'UserPrincipalNameCollision'
                value = $Changes['UserPrincipalName']
            }
        }
    }

    foreach ($conflict in $conflicts) {
        Add-SfAdReportEntry -Report $Report -Bucket 'conflicts' -Entry @{
            workerId = $conflict.workerId
            reason = $conflict.reason
            value = $conflict.value
        }
    }

    return ($conflicts.Count -gt 0)
}

function Invoke-SfAdOffboarding {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [pscustomobject]$User,
        [pscustomobject]$Worker,
        [pscustomobject]$State,
        [System.Collections.IDictionary]$Report,
        [switch]$DryRun,
        [switch]$ReviewMode
    )

    $workerId = Get-SfAdWorkerIdentityValue -Worker $Worker -Config $Config
    $userTarget = Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $User

    Assert-SfAdSafetyThreshold -Config $Config -Report $Report -ThresholdName 'maxDisablesPerRun' -CurrentCount @($Report.disables).Count -Entry @{
        workerId = $workerId
        threshold = 'maxDisablesPerRun'
        attemptedCount = @($Report.disables).Count + 1
    }

    $disableBefore = [pscustomobject]@{ enabled = [bool]$User.Enabled }
    Disable-SfAdUser -Config $Config -User $User -DryRun:$DryRun
    Add-SfAdReportEntry -Report $Report -Bucket 'disables' -Entry @{
        workerId = $workerId
        samAccountName = $User.SamAccountName
        currentEnabled = [bool]$User.Enabled
        currentDistinguishedName = $User.DistinguishedName
        reviewCategory = if ($ReviewMode) { 'ExistingUserOffboarding' } else { $null }
    }
    Add-SfAdReportOperation -Report $Report -OperationType 'DisableUser' -WorkerId $workerId -Bucket 'disables' -Target $userTarget -Before $disableBefore -After ([pscustomobject]@{ enabled = $false }) | Out-Null

    $currentUser = $User
    if (-not $DryRun -and $User.ObjectGuid) {
        $currentUser = Get-SfAdUserByObjectGuid -Config $Config -ObjectGuid $User.ObjectGuid.Guid
    }

    if ($currentUser.DistinguishedName -notlike "*$($Config.ad.graveyardOu)") {
        $moveBefore = [pscustomobject]@{
            distinguishedName = $currentUser.DistinguishedName
            parentOu = Get-SfAdParentOuFromDistinguishedName -DistinguishedName $currentUser.DistinguishedName
        }
        Move-SfAdUser -Config $Config -User $currentUser -TargetOu $Config.ad.graveyardOu -DryRun:$DryRun
        Add-SfAdReportEntry -Report $Report -Bucket 'graveyardMoves' -Entry @{
            workerId = $workerId
            samAccountName = $currentUser.SamAccountName
            targetOu = $Config.ad.graveyardOu
            currentDistinguishedName = $currentUser.DistinguishedName
            reviewCategory = if ($ReviewMode) { 'ExistingUserOffboarding' } else { $null }
        }
        Add-SfAdReportOperation -Report $Report -OperationType 'MoveUser' -WorkerId $workerId -Bucket 'graveyardMoves' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $currentUser) -Before $moveBefore -After ([pscustomobject]@{ targetOu = $Config.ad.graveyardOu }) | Out-Null
    }

    if (-not $ReviewMode) {
        Set-SfAdTrackedWorkerState -State $State -Report $Report -WorkerId $workerId -WorkerState ([pscustomobject]@{
            adObjectGuid = $User.ObjectGuid.Guid
            distinguishedName = $User.DistinguishedName
            suppressed = $true
            firstDisabledAt = (Get-Date).ToString('o')
            deleteAfter = (Get-Date).AddDays([int]$Config.sync.deletionRetentionDays).ToString('o')
            lastSeenStatus = $Worker.status
        })
    }
}

function Invoke-SfAdDeletionPass {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [pscustomobject]$State,
        [System.Collections.IDictionary]$Report,
        [switch]$DryRun,
        [switch]$ReviewMode
    )

    if ($ReviewMode) {
        return
    }

    Ensure-ActiveDirectoryModule
    foreach ($property in @(Get-SfAdWorkerEntries -Workers $State.workers)) {
        $workerState = $property.Value
        $isSuppressed = $workerState.PSObject.Properties.Name -contains 'suppressed' -and [bool]$workerState.suppressed
        $deleteAfter = if ($workerState.PSObject.Properties.Name -contains 'deleteAfter') { $workerState.deleteAfter } else { $null }

        if (-not $isSuppressed -or -not $deleteAfter) {
            continue
        }

        if ((Get-Date $deleteAfter) -gt (Get-Date)) {
            continue
        }

        Assert-SfAdSafetyThreshold -Config $Config -Report $Report -ThresholdName 'maxDeletionsPerRun' -CurrentCount @($Report.deletions).Count -Entry @{
            workerId = $property.Name
            threshold = 'maxDeletionsPerRun'
            attemptedCount = @($Report.deletions).Count + 1
        }

        $user = Get-SfAdUserByObjectGuid -Config $Config -ObjectGuid $workerState.adObjectGuid
        if (-not $user) {
            continue
        }

        $latestWorker = Get-SfWorkerById -Config $Config -WorkerId $property.Name
        if ($latestWorker -and (Test-SfAdWorkerIsActive -Worker $latestWorker)) {
            Add-SfAdReportEntry -Report $Report -Bucket 'manualReview' -Entry @{
                workerId = $property.Name
                reason = 'RehireDetectedBeforeDelete'
                distinguishedName = $user.DistinguishedName
            }
            continue
        }

        $snapshot = if (-not $DryRun) { Get-SfAdUserSnapshot -Config $Config -User $user } else { [pscustomobject]@{ samAccountName = $user.SamAccountName; objectGuid = $workerState.adObjectGuid } }
        Remove-SfAdUser -Config $Config -User $user -DryRun:$DryRun
        Add-SfAdReportEntry -Report $Report -Bucket 'deletions' -Entry @{
            workerId = $property.Name
            samAccountName = $user.SamAccountName
        }
        Add-SfAdReportOperation -Report $Report -OperationType 'DeleteUser' -WorkerId $property.Name -Bucket 'deletions' -Target (Get-SfAdUserTargetDescriptor -WorkerId $property.Name -User $user) -Before $snapshot -After $null | Out-Null
    }
}

function Test-SfAdSyncPreflight {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath,
        [Parameter(Mandatory)]
        [string]$MappingConfigPath
    )

    $resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
    $resolvedMappingConfigPath = (Resolve-Path -Path $MappingConfigPath).Path
    $config = Get-SfAdSyncConfig -Path $resolvedConfigPath
    $mapping = Get-SfAdSyncMappingConfig -Path $resolvedMappingConfigPath
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

function Invoke-SfAdSyncRun {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath,
        [Parameter(Mandatory)]
        [string]$MappingConfigPath,
        [ValidateSet('Delta','Full','Review')]
        [string]$Mode = 'Delta',
        [switch]$DryRun,
        [string]$WorkerId
    )

    if (-not [string]::IsNullOrWhiteSpace($WorkerId) -and -not (Test-SfAdReviewMode -Mode $Mode)) {
        throw '-WorkerId is only supported with -Mode Review.'
    }

    $resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
    $resolvedMappingConfigPath = (Resolve-Path -Path $MappingConfigPath).Path
    $config = Get-SfAdSyncConfig -Path $resolvedConfigPath
    $mappingConfig = Get-SfAdSyncMappingConfig -Path $resolvedMappingConfigPath
    $isReviewMode = Test-SfAdReviewMode -Mode $Mode
    $isScopedWorkerPreview = -not [string]::IsNullOrWhiteSpace($WorkerId)
    $effectiveDryRun = ($DryRun -or $isReviewMode)
    $workerFetchMode = if ($isReviewMode) { 'Full' } else { $Mode }
    $reportOutputDirectory = Get-SfAdReportOutputDirectory -Config $config -Mode $Mode
    $report = New-SfAdSyncReport `
        -Mode $Mode `
        -DryRun:$effectiveDryRun `
        -ConfigPath $resolvedConfigPath `
        -MappingConfigPath $resolvedMappingConfigPath `
        -StatePath $config.state.path `
        -ArtifactType (Get-SfAdArtifactType -Mode $Mode -WorkerId $WorkerId) `
        -WorkerScope $(if ($isScopedWorkerPreview) {
            [pscustomobject]@{
                identityField = $config.successFactors.query.identityField
                workerId = $WorkerId
            }
        } else {
            $null
        })
    Update-SfAdRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'Initializing' -LastAction 'Starting sync run.'
    Update-SfAdRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'LoadingState' -LastAction 'Loading sync state.'
    $state = Get-SfAdSyncState -Path $config.state.path
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
        Update-SfAdRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'FetchingWorkers' -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -LastAction $fetchDescription
        Write-SfAdSyncLog -Message $fetchDescription
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
        Update-SfAdRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'ProcessingWorkers' -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -LastAction "Fetched $totalWorkers workers."
        $workerCountsByIdentity = @{}
        foreach ($worker in $workers) {
            $candidateWorkerId = Get-SfAdWorkerIdentityValue -Worker $worker -Config $config
            if ([string]::IsNullOrWhiteSpace($candidateWorkerId)) {
                continue
            }

            if (-not $workerCountsByIdentity.ContainsKey($candidateWorkerId)) {
                $workerCountsByIdentity[$candidateWorkerId] = 0
            }
            $workerCountsByIdentity[$candidateWorkerId] += 1
        }

        foreach ($worker in $workers) {
            $workerId = Get-SfAdWorkerIdentityValue -Worker $worker -Config $config
            $currentWorkerId = if ([string]::IsNullOrWhiteSpace($workerId)) { $null } else { $workerId }
            $lastAction = if ($currentWorkerId) { "Evaluating worker $currentWorkerId." } else { 'Evaluating worker with missing identity.' }
            Update-SfAdRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'ProcessingWorkers' -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -CurrentWorkerId $currentWorkerId -LastAction $lastAction

            try {
                if ([string]::IsNullOrWhiteSpace($workerId)) {
                    Add-SfAdReportEntry -Report $report -Bucket 'quarantined' -Entry @{
                        workerId = $null
                        reason = 'MissingEmployeeId'
                    }
                    $lastAction = 'Quarantined worker with missing employee ID.'
                    continue
                }

                if ($workerCountsByIdentity[$workerId] -gt 1) {
                    Add-SfAdReportEntry -Report $report -Bucket 'conflicts' -Entry @{
                        workerId = $workerId
                        reason = 'DuplicateWorkerId'
                        occurrences = $workerCountsByIdentity[$workerId]
                    }
                    $lastAction = "Detected duplicate worker identity for $workerId."
                    continue
                }

                $existingUserMatches = Get-SfAdTargetUser -Config $config -WorkerId $workerId
                if ((Get-SfAdCollectionCount -Value $existingUserMatches) -gt 1) {
                    Add-SfAdReportEntry -Report $report -Bucket 'conflicts' -Entry @{
                        workerId = $workerId
                        reason = 'DuplicateAdIdentityMatch'
                        occurrences = Get-SfAdCollectionCount -Value $existingUserMatches
                    }
                    $lastAction = "Detected duplicate AD identity match for $workerId."
                    continue
                }

                $existingUser = Get-SfAdSingleResult -Value $existingUserMatches
                $workerState = Get-SfAdWorkerState -State $state -WorkerId $workerId
                $workerStateIsSuppressed = $workerState -and $workerState.PSObject.Properties.Name -contains 'suppressed' -and [bool]$workerState.suppressed

                if ($workerStateIsSuppressed -and (Test-SfAdWorkerIsActive -Worker $worker)) {
                    $manualReviewEntry = @{
                        workerId = $workerId
                        reason = 'RehireDetected'
                        distinguishedName = $workerState.distinguishedName
                    }
                    if ($isReviewMode) {
                        $manualReviewEntry['matchedExistingUser'] = $true
                    }
                    Add-SfAdReportEntry -Report $report -Bucket 'manualReview' -Entry $manualReviewEntry
                    $lastAction = "Queued rehire for manual review for $workerId."
                    continue
                }

                if (-not (Test-SfAdWorkerIsActive -Worker $worker)) {
                    if ($existingUser) {
                        Invoke-SfAdOffboarding -Config $config -User $existingUser -Worker $worker -State $state -Report $report -DryRun:$effectiveDryRun -ReviewMode:$isReviewMode
                        $lastAction = "Offboarded inactive worker $workerId."
                    } else {
                        $lastAction = "Skipped inactive worker $workerId with no matching AD user."
                    }
                    continue
                }

                $attributeResult = Get-SfAdAttributeChanges -Worker $worker -ExistingUser $existingUser -MappingConfig $mappingConfig
                $reviewEvaluation = if ($isReviewMode) { Get-SfAdMappingEvaluation -Worker $worker -ExistingUser $existingUser -MappingConfig $mappingConfig } else { $null }
                if ($attributeResult.MissingRequired.Count -gt 0) {
                    $quarantineEntry = @{
                        workerId = $workerId
                        reason = 'MissingRequiredData'
                        fields = $attributeResult.MissingRequired
                    }
                    if ($isReviewMode -and $reviewEvaluation) {
                        $quarantineEntry['attributeRows'] = Get-SfAdReviewAttributeRows -Rows $reviewEvaluation.Rows
                        $quarantineEntry['matchedExistingUser'] = [bool]$existingUser
                    }
                    Add-SfAdReportEntry -Report $report -Bucket 'quarantined' -Entry $quarantineEntry
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
                if (-not (Set-SfAdManagerAttributeIfPossible -Config $config -Worker $worker -Changes $changes -Report $report -WorkerId $workerId -MatchedExistingUser:([bool]$existingUser))) {
                    $lastAction = "Quarantined worker $workerId because the manager could not be resolved."
                    continue
                }
                $targetOu = Resolve-SfAdTargetOu -Config $config -Worker $worker
                $reviewAttributeRows = if ($isReviewMode -and $reviewEvaluation) { @(Get-SfAdReviewAttributeRows -Rows $reviewEvaluation.Rows) } else { @() }
                $reviewChangedRows = if ($isReviewMode -and $reviewEvaluation) { @(Get-SfAdChangedMappingRows -Rows $reviewEvaluation.Rows) } else { @() }

                if (-not $existingUser) {
                    if (Test-SfAdCreateConflicts -Config $config -WorkerId $workerId -Changes $changes -Report $report) {
                        $lastAction = "Skipped create for $workerId because of a conflict."
                        continue
                    }

                    Assert-SfAdSafetyThreshold -Config $config -Report $report -ThresholdName 'maxCreatesPerRun' -CurrentCount @($report.creates).Count -Entry @{
                        workerId = $workerId
                        threshold = 'maxCreatesPerRun'
                        attemptedCount = @($report.creates).Count + 1
                    }

                    $createdUser = New-SfAdUser -Config $config -Worker $worker -WorkerId $workerId -Attributes $changes -DryRun:$effectiveDryRun
                    $createEntry = @{
                        workerId = $workerId
                        samAccountName = if ($createdUser.SamAccountName) { $createdUser.SamAccountName } else { $workerId }
                    }
                    if ($isReviewMode) {
                        $createEntry['reviewCategory'] = 'NewUser'
                        $createEntry['targetOu'] = $targetOu
                        $createEntry['attributeRows'] = $reviewAttributeRows
                        $createEntry['changedAttributeDetails'] = $reviewChangedRows
                        $createEntry['proposedAttributes'] = Convert-ToSfAdSerializable -Value $changes
                        $createEntry['proposedEnable'] = [bool](Test-SfAdWorkerIsPrehireEligible -Worker $worker -EnableBeforeDays $config.sync.enableBeforeStartDays)
                    }
                    Add-SfAdReportEntry -Report $report -Bucket 'creates' -Entry $createEntry

                    if (-not $effectiveDryRun -and $createdUser) {
                        try {
                            $createAfter = Get-SfAdUserSnapshot -Config $config -User $createdUser
                        } catch {
                            $createAfter = Convert-ToSfAdSerializable -Value $createdUser
                        }
                    } else {
                        $createAfter = Convert-ToSfAdSerializable -Value $createdUser
                    }
                    Add-SfAdReportOperation -Report $report -OperationType 'CreateUser' -WorkerId $workerId -Bucket 'creates' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $createdUser) -Before $null -After $createAfter | Out-Null

                    if (-not $effectiveDryRun -and $createdUser) {
                        if (Test-SfAdWorkerIsPrehireEligible -Worker $worker -EnableBeforeDays $config.sync.enableBeforeStartDays) {
                            Enable-SfAdUser -Config $config -User $createdUser -DryRun:$effectiveDryRun
                            Add-SfAdReportEntry -Report $report -Bucket 'enables' -Entry @{
                                workerId = $workerId
                                samAccountName = $createdUser.SamAccountName
                                licensingGroups = @()
                            }
                            Add-SfAdReportOperation -Report $report -OperationType 'EnableUser' -WorkerId $workerId -Bucket 'enables' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $createdUser) -Before ([pscustomobject]@{ enabled = $false }) -After ([pscustomobject]@{ enabled = $true }) | Out-Null

                            $licensedGroups = @(Add-SfAdUserToConfiguredGroups -Config $config -User $createdUser -DryRun:$effectiveDryRun)
                            if ($licensedGroups.Count -gt 0) {
                                $report.enables[-1].licensingGroups = $licensedGroups
                                Add-SfAdReportOperation -Report $report -OperationType 'AddGroupMembership' -WorkerId $workerId -Bucket 'enables' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $createdUser) -Before ([pscustomobject]@{ groupsAdded = @() }) -After ([pscustomobject]@{ groupsAdded = $licensedGroups }) | Out-Null
                            }
                        }

                        if (-not $isReviewMode) {
                            Set-SfAdTrackedWorkerState -State $state -Report $report -WorkerId $workerId -WorkerState ([pscustomobject]@{
                                adObjectGuid = $createdUser.ObjectGuid.Guid
                                distinguishedName = $createdUser.DistinguishedName
                                suppressed = $false
                                firstDisabledAt = $null
                                deleteAfter = $null
                                lastSeenStatus = $worker.status
                            })
                        }
                    }

                    $lastAction = "Created user for worker $workerId."
                    continue
                }

                if ($changes.Count -gt 0) {
                    $beforeAttributes = Get-SfAdAttributeBeforeValues -User $existingUser -Changes $changes
                    Set-SfAdUserAttributes -Config $config -User $existingUser -Changes $changes -DryRun:$effectiveDryRun | Out-Null
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
                    Add-SfAdReportEntry -Report $report -Bucket 'updates' -Entry $updateEntry
                    Add-SfAdReportOperation -Report $report -OperationType 'UpdateAttributes' -WorkerId $workerId -Bucket 'updates' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $existingUser) -Before $beforeAttributes -After (Convert-ToSfAdSerializable -Value $changes) | Out-Null
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
                    Add-SfAdReportEntry -Report $report -Bucket 'unchanged' -Entry $unchangedEntry
                    $lastAction = "No attribute changes for worker $workerId."
                }

                $currentUser = $existingUser
                if ($currentUser.DistinguishedName -notlike "*$targetOu") {
                    $moveBefore = [pscustomobject]@{
                        distinguishedName = $currentUser.DistinguishedName
                        parentOu = Get-SfAdParentOuFromDistinguishedName -DistinguishedName $currentUser.DistinguishedName
                    }
                    Move-SfAdUser -Config $config -User $currentUser -TargetOu $targetOu -DryRun:$effectiveDryRun
                    $moveEntry = @{
                        workerId = $workerId
                        samAccountName = $currentUser.SamAccountName
                        targetOu = $targetOu
                    }
                    if ($isReviewMode) {
                        $moveEntry['reviewCategory'] = 'ExistingUserPlacement'
                        $moveEntry['currentDistinguishedName'] = $currentUser.DistinguishedName
                    }
                    Add-SfAdReportEntry -Report $report -Bucket 'graveyardMoves' -Entry $moveEntry
                    Add-SfAdReportOperation -Report $report -OperationType 'MoveUser' -WorkerId $workerId -Bucket 'graveyardMoves' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $currentUser) -Before $moveBefore -After ([pscustomobject]@{ targetOu = $targetOu }) | Out-Null
                    if (-not $effectiveDryRun) {
                        $currentUser = Get-SfAdUserByObjectGuid -Config $config -ObjectGuid $currentUser.ObjectGuid.Guid
                    }
                    $lastAction = "Moved worker $workerId to target OU."
                }

                if (-not $currentUser.Enabled -and (Test-SfAdWorkerIsPrehireEligible -Worker $worker -EnableBeforeDays $config.sync.enableBeforeStartDays)) {
                    Enable-SfAdUser -Config $config -User $currentUser -DryRun:$effectiveDryRun
                    $licensedGroups = @(Add-SfAdUserToConfiguredGroups -Config $config -User $currentUser -DryRun:$effectiveDryRun)
                    $enableEntry = @{
                        workerId = $workerId
                        samAccountName = $currentUser.SamAccountName
                        licensingGroups = $licensedGroups
                    }
                    if ($isReviewMode) {
                        $enableEntry['reviewCategory'] = 'ExistingUserEnable'
                        $enableEntry['currentEnabled'] = [bool]$currentUser.Enabled
                    }
                    Add-SfAdReportEntry -Report $report -Bucket 'enables' -Entry $enableEntry
                    Add-SfAdReportOperation -Report $report -OperationType 'EnableUser' -WorkerId $workerId -Bucket 'enables' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $currentUser) -Before ([pscustomobject]@{ enabled = $false }) -After ([pscustomobject]@{ enabled = $true }) | Out-Null
                    if ($licensedGroups.Count -gt 0) {
                        Add-SfAdReportOperation -Report $report -OperationType 'AddGroupMembership' -WorkerId $workerId -Bucket 'enables' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $currentUser) -Before ([pscustomobject]@{ groupsAdded = @() }) -After ([pscustomobject]@{ groupsAdded = $licensedGroups }) | Out-Null
                    }
                    $lastAction = "Enabled worker $workerId."
                }

                if (-not $isReviewMode) {
                    Set-SfAdTrackedWorkerState -State $state -Report $report -WorkerId $workerId -WorkerState ([pscustomobject]@{
                        adObjectGuid = $currentUser.ObjectGuid.Guid
                        distinguishedName = $currentUser.DistinguishedName
                        suppressed = $false
                        firstDisabledAt = $null
                        deleteAfter = $null
                        lastSeenStatus = $worker.status
                    })
                    if ($lastAction -eq "No attribute changes for worker $workerId.") {
                        $lastAction = "Refreshed tracked state for worker $workerId."
                    }
                }
            } finally {
                $processedWorkers += 1
                Update-SfAdRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'ProcessingWorkers' -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -CurrentWorkerId $currentWorkerId -LastAction $lastAction
            }
        }

        if (-not $isReviewMode) {
            Update-SfAdRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'DeletionPass' -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -LastAction 'Running deletion pass.'
            Invoke-SfAdDeletionPass -Config $config -State $state -Report $report -DryRun:$effectiveDryRun -ReviewMode:$isReviewMode
        }

        Update-SfAdRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'SavingState' -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -LastAction 'Persisting checkpoint and state.'
        if (-not $isReviewMode) {
            Set-SfAdTrackedCheckpoint -State $state -Report $report -Checkpoint ((Get-Date).ToString('yyyy-MM-ddTHH:mm:ss'))
        }
        if (-not $effectiveDryRun -and -not $isReviewMode) {
            Save-SfAdSyncState -State $state -Path $config.state.path
        }

        if ($isReviewMode) {
            Set-SfAdReviewSummary -Report $report -MappingConfig $mappingConfig -DeletionPassSkipped
        }
        $report.status = 'Succeeded'
    } catch {
        $report.status = 'Failed'
        $report.errorMessage = $_.Exception.Message
        $report.failedAt = (Get-Date).ToString('o')
        throw
    } finally {
        Update-SfAdRuntimeStatus -Report $report -StatePath $config.state.path -Stage 'WritingReport' -Status $report.status -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -LastAction 'Writing sync report.' -ErrorMessage $report.errorMessage
        $reportPath = Save-SfAdSyncReport -Report $report -Directory $reportOutputDirectory -Mode $Mode
        $finalStage = if ($report.status -eq 'Succeeded') { 'Completed' } else { 'Failed' }
        Update-SfAdRuntimeStatus -Report $report -StatePath $config.state.path -Stage $finalStage -Status $report.status -ProcessedWorkers $processedWorkers -TotalWorkers $totalWorkers -LastAction "Run $($report.status)." -CompletedAt $report.completedAt -ErrorMessage $report.errorMessage
    }

    Write-SfAdSyncLog -Message "Run completed. Report written to $reportPath"
    return $reportPath
}

Export-ModuleMember -Function Write-SfAdSyncLog, Test-SfAdWorkerIsActive, Get-SfAdWorkerIdentityValue, Test-SfAdWorkerIsPrehireEligible, Test-SfAdSyncPreflight, Invoke-SfAdSyncRun
