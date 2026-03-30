Set-StrictMode -Version Latest

function New-SyncFactorsSyntheticManagerDirectory {
    [CmdletBinding()]
    param(
        [ValidateRange(1, 5000)]
        [int]$ManagerCount = 50
    )

    $managers = @()
    for ($i = 0; $i -lt $ManagerCount; $i++) {
        $managerNumber = 900000 + $i
        $managerId = "MGR$managerNumber"
        $managers += [pscustomobject]@{
            employeeId = $managerId
            samAccountName = "mgr$managerNumber".ToLowerInvariant()
            distinguishedName = "CN=Manager$managerNumber User,OU=Managers,DC=example,DC=com"
            company = if ($i % 3 -eq 0) { 'CORP' } else { 'FIELD' }
            department = if ($i % 5 -eq 0) { 'IT' } else { 'Operations' }
        }
    }

    return $managers
}

function New-SyncFactorsSyntheticWorkers {
    [CmdletBinding()]
    param(
        [ValidateRange(1, 50000)]
        [int]$UserCount = 1000,
        [ValidateRange(0, 5000)]
        [int]$InactiveCount = 0,
        [ValidateRange(0, 5000)]
        [int]$DuplicateWorkerIdCount = 0,
        [ValidateRange(0, 5000)]
        [int]$ManagerCount = 50
    )

    if ($InactiveCount -gt $UserCount) {
        throw 'InactiveCount cannot exceed UserCount.'
    }

    if ($DuplicateWorkerIdCount -ge $UserCount) {
        throw 'DuplicateWorkerIdCount must be less than UserCount.'
    }

    $uniqueWorkerCount = $UserCount - $DuplicateWorkerIdCount
    $managerDirectory = @(New-SyncFactorsSyntheticManagerDirectory -ManagerCount $ManagerCount)
    $workers = @()

    for ($i = 0; $i -lt $uniqueWorkerCount; $i++) {
        $workerNumber = 100000 + $i
        $workerId = "$workerNumber"
        $status = if ($i -ge ($uniqueWorkerCount - $InactiveCount)) { 'inactive' } else { 'active' }
        $startDate = if ($status -eq 'inactive') { (Get-Date).AddDays(-30) } else { (Get-Date).AddDays(-7) }
        $department = if ($i % 5 -eq 0) { 'IT' } else { 'Operations' }
        $company = if ($i % 3 -eq 0) { 'CORP' } else { 'FIELD' }
        $location = if ($i % 2 -eq 0) { 'New York' } else { 'Remote' }
        $candidateManagers = @($managerDirectory | Where-Object { $_.department -eq $department -and $_.company -eq $company })
        if ($candidateManagers.Count -eq 0) {
            $candidateManagers = $managerDirectory
        }
        $assignedManager = $candidateManagers[$i % $candidateManagers.Count]

        $workers += [pscustomobject]@{
            personIdExternal = $workerId
            employeeId = $workerId
            firstName = "Synthetic$workerNumber"
            lastName = 'User'
            status = $status
            startDate = $startDate.ToString('o')
            department = $department
            company = $company
            location = $location
            email = "synthetic$workerNumber@example.com"
            managerEmployeeId = $assignedManager.employeeId
            employmentNav = @(
                [pscustomobject]@{
                    jobInfoNav = @(
                        [pscustomobject]@{
                            jobTitle = if ($department -eq 'IT') { 'Systems Engineer' } else { 'Operations Analyst' }
                            businessUnit = if ($company -eq 'CORP') { 'Corporate' } else { 'Field' }
                            division = if ($department -eq 'IT') { 'Technology' } else { 'Operations' }
                            costCenter = if ($department -eq 'IT') { 'IT-1000' } else { 'OPS-2000' }
                            employeeClass = if ($status -eq 'inactive') { 'TERM' } else { 'EMP' }
                            employmentType = 'Regular'
                        }
                    )
                }
            )
        }
    }

    for ($i = 0; $i -lt $DuplicateWorkerIdCount; $i++) {
        $workers += $workers[$i]
    }

    return [pscustomobject]@{
        workers = $workers
        managers = $managerDirectory
    }
}

function Get-SyncFactorsSyntheticWorkerProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Worker
    )

    $workerId = "$($Worker.personIdExternal)"
    $department = if ($Worker.PSObject.Properties.Name -contains 'department' -and -not [string]::IsNullOrWhiteSpace("$($Worker.department)")) {
        "$($Worker.department)"
    } else {
        'Operations'
    }
    $company = if ($Worker.PSObject.Properties.Name -contains 'company' -and -not [string]::IsNullOrWhiteSpace("$($Worker.company)")) {
        "$($Worker.company)"
    } else {
        'FIELD'
    }

    $targetOu = if ($company -eq 'CORP' -and $department -eq 'IT') {
        'OU=LabIT,OU=LabUsers,DC=example,DC=com'
    } elseif ($company -eq 'CORP') {
        'OU=LabCorp,OU=LabUsers,DC=example,DC=com'
    } else {
        'OU=LabField,OU=LabUsers,DC=example,DC=com'
    }

    $samAccountName = ("sf{0}" -f $workerId).ToLowerInvariant()
    $displayName = "{0} {1}" -f $Worker.firstName, $Worker.lastName

    return [pscustomobject]@{
        workerId = $workerId
        samAccountName = $samAccountName
        userPrincipalName = if ($Worker.PSObject.Properties.Name -contains 'email') { "$($Worker.email)" } else { "$samAccountName@example.com" }
        displayName = $displayName.Trim()
        targetOu = $targetOu
        distinguishedName = "CN=$displayName,$targetOu"
        company = $company
        department = $department
        location = if ($Worker.PSObject.Properties.Name -contains 'location') { "$($Worker.location)" } else { 'Remote' }
        managerEmployeeId = if ($Worker.PSObject.Properties.Name -contains 'managerEmployeeId') { "$($Worker.managerEmployeeId)" } else { $null }
    }
}

function New-SyncFactorsSyntheticChangedAttributeDetails {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Worker,
        [ValidateSet('DepartmentTransfer', 'ManagerRefresh', 'LocationCorrection', 'RehireCleanup', 'TitleRefresh')]
        [string]$Scenario = 'DepartmentTransfer'
    )

    switch ($Scenario) {
        'DepartmentTransfer' {
            return @(
                [pscustomobject]@{
                    sourceField = 'department'
                    targetAttribute = 'Department'
                    currentAdValue = 'Finance'
                    proposedValue = "$($Worker.department)"
                },
                [pscustomobject]@{
                    sourceField = 'employmentNav[0].jobInfoNav[0].division'
                    targetAttribute = 'Division'
                    currentAdValue = 'Shared Services'
                    proposedValue = if ("$($Worker.department)" -eq 'IT') { 'Technology' } else { 'Operations' }
                }
            )
        }
        'ManagerRefresh' {
            return @(
                [pscustomobject]@{
                    sourceField = 'managerEmployeeId'
                    targetAttribute = 'manager'
                    currentAdValue = 'CN=Former Manager,OU=Managers,DC=example,DC=com'
                    proposedValue = "employeeId=$($Worker.managerEmployeeId)"
                }
            )
        }
        'LocationCorrection' {
            return @(
                [pscustomobject]@{
                    sourceField = 'location'
                    targetAttribute = 'Office'
                    currentAdValue = 'Chicago'
                    proposedValue = "$($Worker.location)"
                }
            )
        }
        'RehireCleanup' {
            return @(
                [pscustomobject]@{
                    sourceField = 'status'
                    targetAttribute = 'Enabled'
                    currentAdValue = 'False'
                    proposedValue = 'True'
                },
                [pscustomobject]@{
                    sourceField = 'department'
                    targetAttribute = 'Department'
                    currentAdValue = 'Alumni'
                    proposedValue = "$($Worker.department)"
                }
            )
        }
        'TitleRefresh' {
            return @(
                [pscustomobject]@{
                    sourceField = 'employmentNav[0].jobInfoNav[0].jobTitle'
                    targetAttribute = 'Title'
                    currentAdValue = 'Analyst I'
                    proposedValue = if ("$($Worker.department)" -eq 'IT') { 'Systems Engineer' } else { 'Operations Analyst' }
                }
            )
        }
    }
}

function New-SyncFactorsSyntheticAttributeRows {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$ChangedAttributeDetails
    )

    return @(
        foreach ($row in $ChangedAttributeDetails) {
            [pscustomobject]@{
                sourceField = $row.sourceField
                targetAttribute = $row.targetAttribute
                currentAdValue = $row.currentAdValue
                proposedValue = $row.proposedValue
                changed = "$($row.currentAdValue)" -ne "$($row.proposedValue)"
            }
        }
    )
}

function New-SyncFactorsSyntheticOperatorActions {
    [CmdletBinding()]
    param(
        [ValidateSet('RehireCase', 'QuarantineCase', 'ManagerResolutionCase', 'ConflictCase')]
        [string]$ReviewCaseType = 'QuarantineCase'
    )

    switch ($ReviewCaseType) {
        'RehireCase' {
            return @(
                [pscustomobject]@{
                    code = 'ConfirmAccountReuse'
                    label = 'Confirm account reuse'
                    description = 'Review the prior AD account and confirm whether it should be reused.'
                },
                [pscustomobject]@{
                    code = 'RestoreGroupMembership'
                    label = 'Restore group membership'
                    description = 'Restore the worker''s previous licensing and security groups after review.'
                }
            )
        }
        'ManagerResolutionCase' {
            return @(
                [pscustomobject]@{
                    code = 'AssignFallbackManager'
                    label = 'Assign fallback manager'
                    description = 'Route the worker to an approved fallback manager until source data is corrected.'
                }
            )
        }
        'ConflictCase' {
            return @(
                [pscustomobject]@{
                    code = 'ResolveIdentityConflict'
                    label = 'Resolve identity conflict'
                    description = 'Inspect duplicate identities and decide which source record should win.'
                }
            )
        }
        default {
            return @(
                [pscustomobject]@{
                    code = 'OpenCase'
                    label = 'Open case'
                    description = 'Review the worker record and decide how the sync should proceed.'
                }
            )
        }
    }
}

function New-SyncFactorsSyntheticTrackedWorkerState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Worker,
        [switch]$Suppressed,
        [switch]$PendingDeletion
    )

    $profile = Get-SyncFactorsSyntheticWorkerProfile -Worker $Worker
    $deleteAfter = if ($Suppressed) {
        if ($PendingDeletion) {
            (Get-Date).AddDays(-2).ToString('o')
        } else {
            (Get-Date).AddDays(14).ToString('o')
        }
    } else {
        $null
    }

    return [pscustomobject]@{
        adObjectGuid = [guid]::NewGuid().Guid
        distinguishedName = $profile.distinguishedName
        suppressed = [bool]$Suppressed
        firstDisabledAt = if ($Suppressed) { (Get-Date).AddDays(-30).ToString('o') } else { $null }
        deleteAfter = $deleteAfter
        lastSeenStatus = if ($Suppressed) { 'inactive' } else { "$($Worker.status)" }
    }
}

Export-ModuleMember -Function New-SyncFactorsSyntheticManagerDirectory, New-SyncFactorsSyntheticWorkers, Get-SyncFactorsSyntheticWorkerProfile, New-SyncFactorsSyntheticChangedAttributeDetails, New-SyncFactorsSyntheticAttributeRows, New-SyncFactorsSyntheticOperatorActions, New-SyncFactorsSyntheticTrackedWorkerState
