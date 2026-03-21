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

Export-ModuleMember -Function New-SyncFactorsSyntheticManagerDirectory, New-SyncFactorsSyntheticWorkers
