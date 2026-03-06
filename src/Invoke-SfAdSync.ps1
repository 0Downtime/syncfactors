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

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleRoot = Join-Path -Path $PSScriptRoot -ChildPath 'Modules/SfAdSync'
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force
Import-Module (Join-Path $moduleRoot 'State.psm1') -Force
Import-Module (Join-Path $moduleRoot 'Reporting.psm1') -Force
Import-Module (Join-Path $moduleRoot 'Mapping.psm1') -Force
Import-Module (Join-Path $moduleRoot 'SuccessFactors.psm1') -Force
Import-Module (Join-Path $moduleRoot 'ActiveDirectorySync.psm1') -Force

function Write-Log {
    param(
        [string]$Level = 'INFO',
        [string]$Message
    )

    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Host "[$timestamp][$Level] $Message"
}

function Test-WorkerIsActive {
    param([pscustomobject]$Worker)
    return @('active','enabled','a') -contains "$($Worker.status)".ToLowerInvariant()
}

function Get-WorkerIdentityValue {
    param(
        [pscustomobject]$Worker,
        [pscustomobject]$Config
    )

    $identityField = $Config.successFactors.query.identityField
    return "$($Worker.PSObject.Properties[$identityField].Value)"
}

function Test-WorkerIsPrehireEligible {
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

function Set-ManagerAttributeIfPossible {
    param(
        [pscustomobject]$Config,
        [pscustomobject]$Worker,
        [hashtable]$Changes,
        [hashtable]$Report
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
        workerId = $Worker.employeeId
        reason = 'ManagerNotResolved'
        managerEmployeeId = $Worker.managerEmployeeId
    }
}

function Invoke-Offboarding {
    param(
        [pscustomobject]$Config,
        [Microsoft.ActiveDirectory.Management.ADUser]$User,
        [pscustomobject]$Worker,
        [pscustomobject]$State,
        [hashtable]$Report,
        [switch]$DryRun
    )

    Disable-SfAdUser -User $User -DryRun:$DryRun
    Add-SfAdReportEntry -Report $Report -Bucket 'disables' -Entry @{
        workerId = $Worker.employeeId
        samAccountName = $User.SamAccountName
    }

    if ($User.DistinguishedName -notlike "*$($Config.ad.graveyardOu)") {
        Move-SfAdUser -User $User -TargetOu $Config.ad.graveyardOu -DryRun:$DryRun
        Add-SfAdReportEntry -Report $Report -Bucket 'graveyardMoves' -Entry @{
            workerId = $Worker.employeeId
            samAccountName = $User.SamAccountName
            targetOu = $Config.ad.graveyardOu
        }
    }

    Set-SfAdWorkerState -State $State -WorkerId $Worker.employeeId -WorkerState ([pscustomobject]@{
        adObjectGuid = $User.ObjectGuid.Guid
        distinguishedName = $User.DistinguishedName
        suppressed = $true
        firstDisabledAt = (Get-Date).ToString('o')
        deleteAfter = (Get-Date).AddDays([int]$Config.sync.deletionRetentionDays).ToString('o')
        lastSeenStatus = $Worker.status
    })
}

function Invoke-DeletionPass {
    param(
        [pscustomobject]$Config,
        [pscustomobject]$State,
        [hashtable]$Report,
        [switch]$DryRun
    )

    Ensure-ActiveDirectoryModule
    foreach ($property in @($State.workers.PSObject.Properties)) {
        $workerState = $property.Value
        if (-not $workerState.suppressed -or -not $workerState.deleteAfter) {
            continue
        }

        if ((Get-Date $workerState.deleteAfter) -gt (Get-Date)) {
            continue
        }

        $user = Get-ADUser -Identity $workerState.adObjectGuid -Properties * -ErrorAction SilentlyContinue
        if (-not $user) {
            continue
        }

        $latestWorker = Get-SfWorkerById -Config $Config -WorkerId $property.Name
        if ($latestWorker -and (Test-WorkerIsActive -Worker $latestWorker)) {
            Add-SfAdReportEntry -Report $Report -Bucket 'manualReview' -Entry @{
                workerId = $property.Name
                reason = 'RehireDetectedBeforeDelete'
                distinguishedName = $user.DistinguishedName
            }
            continue
        }

        Remove-SfAdUser -User $user -DryRun:$DryRun
        Add-SfAdReportEntry -Report $Report -Bucket 'deletions' -Entry @{
            workerId = $property.Name
            samAccountName = $user.SamAccountName
        }
    }
}

$config = Get-SfAdSyncConfig -Path $ConfigPath
$mappingConfig = Get-SfAdSyncMappingConfig -Path $MappingConfigPath
$state = Get-SfAdSyncState -Path $config.state.path
$report = New-SfAdSyncReport
$checkpoint = if ($Mode -eq 'Delta') { $state.checkpoint } else { $null }

Write-Log -Message "Fetching SuccessFactors workers in $Mode mode."
$workers = Get-SfWorkers -Config $config -Mode $Mode -Checkpoint $checkpoint

foreach ($worker in $workers) {
    $workerId = Get-WorkerIdentityValue -Worker $worker -Config $config
    if ([string]::IsNullOrWhiteSpace($workerId)) {
        Add-SfAdReportEntry -Report $report -Bucket 'quarantined' -Entry @{
            workerId = $null
            reason = 'MissingEmployeeId'
        }
        continue
    }

    $existingUser = Get-SfAdTargetUser -Config $config -WorkerId $workerId
    $workerState = Get-SfAdWorkerState -State $state -WorkerId $workerId

    if ($workerState -and $workerState.suppressed -and (Test-WorkerIsActive -Worker $worker)) {
        Add-SfAdReportEntry -Report $report -Bucket 'manualReview' -Entry @{
            workerId = $workerId
            reason = 'RehireDetected'
            distinguishedName = $workerState.distinguishedName
        }
        continue
    }

    if (-not (Test-WorkerIsActive -Worker $worker)) {
        if ($existingUser) {
            Invoke-Offboarding -Config $config -User $existingUser -Worker $worker -State $state -Report $report -DryRun:$DryRun
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
    Set-ManagerAttributeIfPossible -Config $config -Worker $worker -Changes $changes -Report $report

    if (-not $existingUser) {
        $createdUser = New-SfAdUser -Config $config -Worker $worker -WorkerId $workerId -Attributes $changes -DryRun:$DryRun
        Add-SfAdReportEntry -Report $report -Bucket 'creates' -Entry @{
            workerId = $workerId
            samAccountName = if ($createdUser.SamAccountName) { $createdUser.SamAccountName } else { $workerId }
        }
        if (-not $DryRun -and $createdUser) {
            if (Test-WorkerIsPrehireEligible -Worker $worker -EnableBeforeDays $config.sync.enableBeforeStartDays) {
                Enable-SfAdUser -User $createdUser -DryRun:$DryRun
                $licensedGroups = Add-SfAdUserToConfiguredGroups -Config $config -User $createdUser -DryRun:$DryRun
                Add-SfAdReportEntry -Report $report -Bucket 'enables' -Entry @{
                    workerId = $workerId
                    samAccountName = $createdUser.SamAccountName
                    licensingGroups = $licensedGroups
                }
            }

            Set-SfAdWorkerState -State $state -WorkerId $workerId -WorkerState ([pscustomobject]@{
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
        Set-SfAdUserAttributes -User $existingUser -Changes $changes -DryRun:$DryRun | Out-Null
        Add-SfAdReportEntry -Report $report -Bucket 'updates' -Entry @{
            workerId = $workerId
            samAccountName = $existingUser.SamAccountName
            changedAttributes = $changes.Keys
        }
    } else {
        Add-SfAdReportEntry -Report $report -Bucket 'unchanged' -Entry @{
            workerId = $workerId
            samAccountName = $existingUser.SamAccountName
        }
    }

    $targetOu = Resolve-SfAdTargetOu -Config $config -Worker $worker
    if ($existingUser.DistinguishedName -notlike "*$targetOu") {
        Move-SfAdUser -User $existingUser -TargetOu $targetOu -DryRun:$DryRun
    }

    if (-not $existingUser.Enabled -and (Test-WorkerIsPrehireEligible -Worker $worker -EnableBeforeDays $config.sync.enableBeforeStartDays)) {
        Enable-SfAdUser -User $existingUser -DryRun:$DryRun
        $licensedGroups = Add-SfAdUserToConfiguredGroups -Config $config -User $existingUser -DryRun:$DryRun
        Add-SfAdReportEntry -Report $report -Bucket 'enables' -Entry @{
            workerId = $workerId
            samAccountName = $existingUser.SamAccountName
            licensingGroups = $licensedGroups
        }
    }

    Set-SfAdWorkerState -State $state -WorkerId $workerId -WorkerState ([pscustomobject]@{
        adObjectGuid = $existingUser.ObjectGuid.Guid
        distinguishedName = $existingUser.DistinguishedName
        suppressed = $false
        firstDisabledAt = $null
        deleteAfter = $null
        lastSeenStatus = $worker.status
    })
}

Invoke-DeletionPass -Config $config -State $state -Report $report -DryRun:$DryRun

$state.checkpoint = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
if (-not $DryRun) {
    Save-SfAdSyncState -State $state -Path $config.state.path
}

$reportPath = Save-SfAdSyncReport -Report $report -Directory $config.reporting.outputDirectory -Mode $Mode
Write-Log -Message "Run completed. Report written to $reportPath"
