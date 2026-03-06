Set-StrictMode -Version Latest

$moduleRoot = $PSScriptRoot
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force
Import-Module (Join-Path $moduleRoot 'State.psm1') -Force
Import-Module (Join-Path $moduleRoot 'Reporting.psm1') -Force
Import-Module (Join-Path $moduleRoot 'Mapping.psm1') -Force
Import-Module (Join-Path $moduleRoot 'SuccessFactors.psm1') -Force
Import-Module (Join-Path $moduleRoot 'ActiveDirectorySync.psm1') -Force

function Write-SfAdSyncLog {
    [CmdletBinding()]
    param(
        [string]$Level = 'INFO',
        [string]$Message
    )

    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Host "[$timestamp][$Level] $Message"
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

    if ($Value.PSObject -and $Value.PSObject.Properties.Count -gt 0 -and -not ($Value -is [string])) {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
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

    return @{
        workerId = $WorkerId
        objectGuid = if ($User -and $User.ObjectGuid) { $User.ObjectGuid.Guid } else { $null }
        samAccountName = if ($User) { $User.SamAccountName } else { $null }
        distinguishedName = if ($User) { $User.DistinguishedName } else { $null }
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
        [string]$WorkerId
    )

    if (-not $Worker.managerEmployeeId) {
        return
    }

    $manager = Get-SfAdTargetUser -Config $Config -WorkerId $Worker.managerEmployeeId
    if ($manager) {
        $Changes['manager'] = $manager.DistinguishedName
        return
    }

    Add-SfAdReportEntry -Report $Report -Bucket 'quarantined' -Entry @{
        workerId = $WorkerId
        reason = 'ManagerNotResolved'
        managerEmployeeId = $Worker.managerEmployeeId
    }
}

function Invoke-SfAdOffboarding {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [pscustomobject]$User,
        [pscustomobject]$Worker,
        [pscustomobject]$State,
        [System.Collections.IDictionary]$Report,
        [switch]$DryRun
    )

    $workerId = "$($Worker.employeeId)"
    $userTarget = Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $User

    $disableBefore = [pscustomobject]@{ enabled = [bool]$User.Enabled }
    Disable-SfAdUser -User $User -DryRun:$DryRun
    Add-SfAdReportEntry -Report $Report -Bucket 'disables' -Entry @{
        workerId = $workerId
        samAccountName = $User.SamAccountName
    }
    Add-SfAdReportOperation -Report $Report -OperationType 'DisableUser' -WorkerId $workerId -Bucket 'disables' -Target $userTarget -Before $disableBefore -After ([pscustomobject]@{ enabled = $false }) | Out-Null

    $currentUser = $User
    if (-not $DryRun -and $User.ObjectGuid) {
        $currentUser = Get-SfAdUserByObjectGuid -ObjectGuid $User.ObjectGuid.Guid
    }

    if ($currentUser.DistinguishedName -notlike "*$($Config.ad.graveyardOu)") {
        $moveBefore = [pscustomobject]@{
            distinguishedName = $currentUser.DistinguishedName
            parentOu = Get-SfAdParentOuFromDistinguishedName -DistinguishedName $currentUser.DistinguishedName
        }
        Move-SfAdUser -User $currentUser -TargetOu $Config.ad.graveyardOu -DryRun:$DryRun
        Add-SfAdReportEntry -Report $Report -Bucket 'graveyardMoves' -Entry @{
            workerId = $workerId
            samAccountName = $currentUser.SamAccountName
            targetOu = $Config.ad.graveyardOu
        }
        Add-SfAdReportOperation -Report $Report -OperationType 'MoveUser' -WorkerId $workerId -Bucket 'graveyardMoves' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $currentUser) -Before $moveBefore -After ([pscustomobject]@{ targetOu = $Config.ad.graveyardOu }) | Out-Null
    }

    Set-SfAdTrackedWorkerState -State $State -Report $Report -WorkerId $workerId -WorkerState ([pscustomobject]@{
        adObjectGuid = $User.ObjectGuid.Guid
        distinguishedName = $User.DistinguishedName
        suppressed = $true
        firstDisabledAt = (Get-Date).ToString('o')
        deleteAfter = (Get-Date).AddDays([int]$Config.sync.deletionRetentionDays).ToString('o')
        lastSeenStatus = $Worker.status
    })
}

function Invoke-SfAdDeletionPass {
    [CmdletBinding()]
    param(
        [pscustomobject]$Config,
        [pscustomobject]$State,
        [System.Collections.IDictionary]$Report,
        [switch]$DryRun
    )

    Ensure-ActiveDirectoryModule
    foreach ($property in @(Get-SfAdWorkerEntries -Workers $State.workers)) {
        $workerState = $property.Value
        if (-not $workerState.suppressed -or -not $workerState.deleteAfter) {
            continue
        }

        if ((Get-Date $workerState.deleteAfter) -gt (Get-Date)) {
            continue
        }

        $user = Get-SfAdUserByObjectGuid -ObjectGuid $workerState.adObjectGuid
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

        $snapshot = if (-not $DryRun) { Get-SfAdUserSnapshot -User $user } else { [pscustomobject]@{ samAccountName = $user.SamAccountName; objectGuid = $workerState.adObjectGuid } }
        Remove-SfAdUser -User $user -DryRun:$DryRun
        Add-SfAdReportEntry -Report $Report -Bucket 'deletions' -Entry @{
            workerId = $property.Name
            samAccountName = $user.SamAccountName
        }
        Add-SfAdReportOperation -Report $Report -OperationType 'DeleteUser' -WorkerId $property.Name -Bucket 'deletions' -Target (Get-SfAdUserTargetDescriptor -WorkerId $property.Name -User $user) -Before $snapshot -After $null | Out-Null
    }
}

function Invoke-SfAdSyncRun {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath,
        [Parameter(Mandatory)]
        [string]$MappingConfigPath,
        [ValidateSet('Delta','Full')]
        [string]$Mode = 'Delta',
        [switch]$DryRun
    )

    $resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
    $resolvedMappingConfigPath = (Resolve-Path -Path $MappingConfigPath).Path
    $config = Get-SfAdSyncConfig -Path $resolvedConfigPath
    $mappingConfig = Get-SfAdSyncMappingConfig -Path $resolvedMappingConfigPath
    $state = Get-SfAdSyncState -Path $config.state.path
    $report = New-SfAdSyncReport -Mode $Mode -DryRun:$DryRun -ConfigPath $resolvedConfigPath -MappingConfigPath $resolvedMappingConfigPath -StatePath $config.state.path
    $checkpoint = if ($Mode -eq 'Delta') { $state.checkpoint } else { $null }

    Write-SfAdSyncLog -Message "Fetching SuccessFactors workers in $Mode mode."
    $workers = Get-SfWorkers -Config $config -Mode $Mode -Checkpoint $checkpoint

    foreach ($worker in $workers) {
        $workerId = Get-SfAdWorkerIdentityValue -Worker $worker -Config $config
        if ([string]::IsNullOrWhiteSpace($workerId)) {
            Add-SfAdReportEntry -Report $report -Bucket 'quarantined' -Entry @{
                workerId = $null
                reason = 'MissingEmployeeId'
            }
            continue
        }

        $existingUser = Get-SfAdTargetUser -Config $config -WorkerId $workerId
        $workerState = Get-SfAdWorkerState -State $state -WorkerId $workerId

        if ($workerState -and $workerState.suppressed -and (Test-SfAdWorkerIsActive -Worker $worker)) {
            Add-SfAdReportEntry -Report $report -Bucket 'manualReview' -Entry @{
                workerId = $workerId
                reason = 'RehireDetected'
                distinguishedName = $workerState.distinguishedName
            }
            continue
        }

        if (-not (Test-SfAdWorkerIsActive -Worker $worker)) {
            if ($existingUser) {
                Invoke-SfAdOffboarding -Config $config -User $existingUser -Worker $worker -State $state -Report $report -DryRun:$DryRun
            }
            continue
        }

        $attributeResult = Get-SfAdAttributeChanges -Worker $worker -ExistingUser $existingUser -MappingConfig $mappingConfig
        if ($attributeResult.MissingRequired.Count -gt 0) {
            Add-SfAdReportEntry -Report $report -Bucket 'quarantined' -Entry @{
                workerId = $workerId
                reason = 'MissingRequiredData'
                fields = $attributeResult.MissingRequired
            }
            continue
        }

        $changes = @{}
        foreach ($key in $attributeResult.Changes.Keys) {
            $changes[$key] = $attributeResult.Changes[$key]
        }

        $changes[$config.ad.identityAttribute] = $workerId
        Set-SfAdManagerAttributeIfPossible -Config $config -Worker $worker -Changes $changes -Report $report -WorkerId $workerId

        if (-not $existingUser) {
            $createdUser = New-SfAdUser -Config $config -Worker $worker -WorkerId $workerId -Attributes $changes -DryRun:$DryRun
            Add-SfAdReportEntry -Report $report -Bucket 'creates' -Entry @{
                workerId = $workerId
                samAccountName = if ($createdUser.SamAccountName) { $createdUser.SamAccountName } else { $workerId }
            }

            $createAfter = if (-not $DryRun -and $createdUser) { Get-SfAdUserSnapshot -User $createdUser } else { Convert-ToSfAdSerializable -Value $createdUser }
            Add-SfAdReportOperation -Report $report -OperationType 'CreateUser' -WorkerId $workerId -Bucket 'creates' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $createdUser) -Before $null -After $createAfter | Out-Null

            if (-not $DryRun -and $createdUser) {
                if (Test-SfAdWorkerIsPrehireEligible -Worker $worker -EnableBeforeDays $config.sync.enableBeforeStartDays) {
                    Enable-SfAdUser -User $createdUser -DryRun:$DryRun
                    Add-SfAdReportEntry -Report $report -Bucket 'enables' -Entry @{
                        workerId = $workerId
                        samAccountName = $createdUser.SamAccountName
                        licensingGroups = @()
                    }
                    Add-SfAdReportOperation -Report $report -OperationType 'EnableUser' -WorkerId $workerId -Bucket 'enables' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $createdUser) -Before ([pscustomobject]@{ enabled = $false }) -After ([pscustomobject]@{ enabled = $true }) | Out-Null

                    $licensedGroups = Add-SfAdUserToConfiguredGroups -Config $config -User $createdUser -DryRun:$DryRun
                    if ($licensedGroups.Count -gt 0) {
                        $report.enables[-1].licensingGroups = $licensedGroups
                        Add-SfAdReportOperation -Report $report -OperationType 'AddGroupMembership' -WorkerId $workerId -Bucket 'enables' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $createdUser) -Before ([pscustomobject]@{ groupsAdded = @() }) -After ([pscustomobject]@{ groupsAdded = $licensedGroups }) | Out-Null
                    }
                }

                Set-SfAdTrackedWorkerState -State $state -Report $report -WorkerId $workerId -WorkerState ([pscustomobject]@{
                    adObjectGuid = $createdUser.ObjectGuid.Guid
                    distinguishedName = $createdUser.DistinguishedName
                    suppressed = $false
                    firstDisabledAt = $null
                    deleteAfter = $null
                    lastSeenStatus = $worker.status
                })
            }
            continue
        }

        if ($changes.Count -gt 0) {
            $beforeAttributes = Get-SfAdAttributeBeforeValues -User $existingUser -Changes $changes
            Set-SfAdUserAttributes -User $existingUser -Changes $changes -DryRun:$DryRun | Out-Null
            Add-SfAdReportEntry -Report $report -Bucket 'updates' -Entry @{
                workerId = $workerId
                samAccountName = $existingUser.SamAccountName
                changedAttributes = $changes.Keys
            }
            Add-SfAdReportOperation -Report $report -OperationType 'UpdateAttributes' -WorkerId $workerId -Bucket 'updates' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $existingUser) -Before $beforeAttributes -After (Convert-ToSfAdSerializable -Value $changes) | Out-Null
        } else {
            Add-SfAdReportEntry -Report $report -Bucket 'unchanged' -Entry @{
                workerId = $workerId
                samAccountName = $existingUser.SamAccountName
            }
        }

        $currentUser = $existingUser
        $targetOu = Resolve-SfAdTargetOu -Config $config -Worker $worker
        if ($currentUser.DistinguishedName -notlike "*$targetOu") {
            $moveBefore = [pscustomobject]@{
                distinguishedName = $currentUser.DistinguishedName
                parentOu = Get-SfAdParentOuFromDistinguishedName -DistinguishedName $currentUser.DistinguishedName
            }
            Move-SfAdUser -User $currentUser -TargetOu $targetOu -DryRun:$DryRun
            Add-SfAdReportEntry -Report $report -Bucket 'graveyardMoves' -Entry @{
                workerId = $workerId
                samAccountName = $currentUser.SamAccountName
                targetOu = $targetOu
            }
            Add-SfAdReportOperation -Report $report -OperationType 'MoveUser' -WorkerId $workerId -Bucket 'graveyardMoves' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $currentUser) -Before $moveBefore -After ([pscustomobject]@{ targetOu = $targetOu }) | Out-Null
            if (-not $DryRun) {
                $currentUser = Get-SfAdUserByObjectGuid -ObjectGuid $currentUser.ObjectGuid.Guid
            }
        }

        if (-not $currentUser.Enabled -and (Test-SfAdWorkerIsPrehireEligible -Worker $worker -EnableBeforeDays $config.sync.enableBeforeStartDays)) {
            Enable-SfAdUser -User $currentUser -DryRun:$DryRun
            $licensedGroups = Add-SfAdUserToConfiguredGroups -Config $config -User $currentUser -DryRun:$DryRun
            Add-SfAdReportEntry -Report $report -Bucket 'enables' -Entry @{
                workerId = $workerId
                samAccountName = $currentUser.SamAccountName
                licensingGroups = $licensedGroups
            }
            Add-SfAdReportOperation -Report $report -OperationType 'EnableUser' -WorkerId $workerId -Bucket 'enables' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $currentUser) -Before ([pscustomobject]@{ enabled = $false }) -After ([pscustomobject]@{ enabled = $true }) | Out-Null
            if ($licensedGroups.Count -gt 0) {
                Add-SfAdReportOperation -Report $report -OperationType 'AddGroupMembership' -WorkerId $workerId -Bucket 'enables' -Target (Get-SfAdUserTargetDescriptor -WorkerId $workerId -User $currentUser) -Before ([pscustomobject]@{ groupsAdded = @() }) -After ([pscustomobject]@{ groupsAdded = $licensedGroups }) | Out-Null
            }
        }

        Set-SfAdTrackedWorkerState -State $state -Report $report -WorkerId $workerId -WorkerState ([pscustomobject]@{
            adObjectGuid = $currentUser.ObjectGuid.Guid
            distinguishedName = $currentUser.DistinguishedName
            suppressed = $false
            firstDisabledAt = $null
            deleteAfter = $null
            lastSeenStatus = $worker.status
        })
    }

    Invoke-SfAdDeletionPass -Config $config -State $state -Report $report -DryRun:$DryRun

    Set-SfAdTrackedCheckpoint -State $state -Report $report -Checkpoint ((Get-Date).ToString('yyyy-MM-ddTHH:mm:ss'))
    if (-not $DryRun) {
        Save-SfAdSyncState -State $state -Path $config.state.path
    }

    $reportPath = Save-SfAdSyncReport -Report $report -Directory $config.reporting.outputDirectory -Mode $Mode
    Write-SfAdSyncLog -Message "Run completed. Report written to $reportPath"
    return $reportPath
}

Export-ModuleMember -Function Write-SfAdSyncLog, Test-SfAdWorkerIsActive, Get-SfAdWorkerIdentityValue, Test-SfAdWorkerIsPrehireEligible, Invoke-SfAdSyncRun
