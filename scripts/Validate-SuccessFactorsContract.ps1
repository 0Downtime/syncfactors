[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InputPath,
    [string]$ReportPath,
    [string[]]$AllowedEmploymentStatuses = @('A', 'U', 'T', 'I', 'R', '64303', '64304', '64307', '64308')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

function Get-ChildCollection {
    param(
        $Value
    )

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        return @($Value)
    }

    if ($Value.PSObject.Properties.Name -contains 'results') {
        return @(Get-ChildCollection -Value $Value.results)
    }

    return @($Value)
}

function Get-FirstItem {
    param(
        $Value
    )

    $items = @(Get-ChildCollection -Value $Value)
    if ($items.Count -eq 0) {
        return $null
    }

    return $items[0]
}

function Get-PrimaryEmailAddress {
    param(
        $EmailNav
    )

    $emails = @(Get-ChildCollection -Value $EmailNav)
    if ($emails.Count -eq 0) {
        return $null
    }

    $primary = $emails | Where-Object { $_.isPrimary -eq $true } | Select-Object -First 1
    if ($null -ne $primary) {
        return $primary.emailAddress
    }

    return ($emails | Select-Object -First 1).emailAddress
}

function Get-ObjectPropertyValue {
    param(
        $Object,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function ConvertTo-NormalizedWorker {
    param(
        [Parameter(Mandatory)]
        $Record,
        [Parameter(Mandatory)]
        [string]$Format
    )

    if ($Format -eq 'fixture-document') {
        return [pscustomobject]@{
            WorkerId               = Get-ObjectPropertyValue -Object $Record -Name 'personIdExternal'
            UserId                 = Get-ObjectPropertyValue -Object $Record -Name 'userId'
            UserName               = Get-ObjectPropertyValue -Object $Record -Name 'userName'
            FirstName              = Get-ObjectPropertyValue -Object $Record -Name 'firstName'
            LastName               = Get-ObjectPropertyValue -Object $Record -Name 'lastName'
            PreferredName          = Get-ObjectPropertyValue -Object $Record -Name 'preferredName'
            DisplayName            = Get-ObjectPropertyValue -Object $Record -Name 'displayName'
            Email                  = Get-ObjectPropertyValue -Object $Record -Name 'email'
            StartDate              = Get-ObjectPropertyValue -Object $Record -Name 'startDate'
            EndDate                = Get-ObjectPropertyValue -Object $Record -Name 'endDate'
            FirstDateWorked        = Get-ObjectPropertyValue -Object $Record -Name 'firstDateWorked'
            LastDateWorked         = Get-ObjectPropertyValue -Object $Record -Name 'lastDateWorked'
            LastModifiedDateTime   = Get-ObjectPropertyValue -Object $Record -Name 'lastModifiedDateTime'
            EmploymentStatus       = Get-ObjectPropertyValue -Object $Record -Name 'employmentStatus'
            LatestTerminationDate  = Get-ObjectPropertyValue -Object $Record -Name 'latestTerminationDate'
            ActiveEmploymentsCount = Get-ObjectPropertyValue -Object $Record -Name 'activeEmploymentsCount'
            ManagerId              = Get-ObjectPropertyValue -Object $Record -Name 'managerId'
        }
    }

    $personalInfo = Get-FirstItem -Value $Record.personalInfoNav
    $employment = Get-FirstItem -Value $Record.employmentNav
    $jobInfo = Get-FirstItem -Value $employment.jobInfoNav
    $userNav = Get-ObjectPropertyValue -Object $employment -Name 'userNav'
    $manager = Get-ObjectPropertyValue -Object $userNav -Name 'manager'
    $managerInfo = Get-ObjectPropertyValue -Object $manager -Name 'empInfo'

    return [pscustomobject]@{
        WorkerId               = $Record.personIdExternal
        UserId                 = Get-ObjectPropertyValue -Object $employment -Name 'userId'
        UserName               = Get-ObjectPropertyValue -Object $userNav -Name 'username'
        FirstName              = Get-ObjectPropertyValue -Object $personalInfo -Name 'firstName'
        LastName               = Get-ObjectPropertyValue -Object $personalInfo -Name 'lastName'
        PreferredName          = Get-ObjectPropertyValue -Object $personalInfo -Name 'preferredName'
        DisplayName            = Get-ObjectPropertyValue -Object $personalInfo -Name 'displayName'
        Email                  = Get-PrimaryEmailAddress -EmailNav $Record.emailNav
        StartDate              = Get-ObjectPropertyValue -Object $employment -Name 'startDate'
        EndDate                = Get-ObjectPropertyValue -Object $employment -Name 'endDate'
        FirstDateWorked        = Get-ObjectPropertyValue -Object $employment -Name 'firstDateWorked'
        LastDateWorked         = Get-ObjectPropertyValue -Object $employment -Name 'lastDateWorked'
        LastModifiedDateTime   = Get-ObjectPropertyValue -Object $Record -Name 'lastModifiedDateTime'
        EmploymentStatus       = Get-ObjectPropertyValue -Object $jobInfo -Name 'emplStatus'
        LatestTerminationDate  = Get-ObjectPropertyValue -Object $Record.personEmpTerminationInfoNav -Name 'latestTerminationDate'
        ActiveEmploymentsCount = Get-ObjectPropertyValue -Object $Record.personEmpTerminationInfoNav -Name 'activeEmploymentsCount'
        ManagerId              = Get-ObjectPropertyValue -Object $managerInfo -Name 'personIdExternal'
    }
}

function Test-DateValue {
    param(
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $true
    }

    return [DateTimeOffset]::TryParse($Value, [ref]([DateTimeOffset]::MinValue))
}

function Get-DateValue {
    param(
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($Value, [ref]$parsed)) {
        return $null
    }

    return $parsed
}

function Add-ValidationIssue {
    param(
        [Parameter(Mandatory)]
        $Collection,
        [Parameter(Mandatory)]
        [string]$Kind,
        [Parameter(Mandatory)]
        [string]$Code,
        [Parameter(Mandatory)]
        [string]$Message,
        [string]$WorkerId
    )

    $Collection.Add([pscustomobject]@{
        kind = $Kind
        code = $Code
        workerId = $WorkerId
        message = $Message
    }) | Out-Null
}

try {
    $resolvedInputPath = Resolve-RequiredPath -Path $InputPath -Label 'Input'
    $projectRoot = Resolve-ProjectRoot

    $rawDocument = Get-Content -Path $resolvedInputPath -Raw
    $json = $rawDocument | ConvertFrom-Json -Depth 100

    $records = @()
    $detectedFormat = $null

    if ($json.PSObject.Properties.Name -contains 'workers') {
        $detectedFormat = 'fixture-document'
        $records = @(Get-ChildCollection -Value $json.workers)
    }
    elseif ($json.PSObject.Properties.Name -contains 'd' -and
        $null -ne $json.d -and
        $json.d.PSObject.Properties.Name -contains 'results') {
        $detectedFormat = 'odata-export'
        $records = @(Get-ChildCollection -Value $json.d.results)
    }
    elseif ($json.PSObject.Properties.Name -contains 'results') {
        $detectedFormat = 'results-document'
        $records = @(Get-ChildCollection -Value $json.results)
    }
    elseif ($json -is [System.Collections.IEnumerable] -and $json -isnot [string]) {
        $detectedFormat = 'raw-array'
        $records = @($json)
    }
    else {
        throw "Unsupported SuccessFactors contract input shape in '$resolvedInputPath'. Expected fixture workers, OData d.results, results, or a top-level array."
    }

    $workers = @($records | ForEach-Object { ConvertTo-NormalizedWorker -Record $_ -Format $detectedFormat })
    $issues = [System.Collections.Generic.List[object]]::new()
    $statusCounts = [ordered]@{}
    $requiredFieldFailures = [ordered]@{
        workerId = 0
        identity = 0
        firstName = 0
        lastName = 0
        startDate = 0
        employmentStatus = 0
    }

    foreach ($worker in $workers) {
        $status = [string]$worker.EmploymentStatus
        if (-not [string]::IsNullOrWhiteSpace($status)) {
            if (-not $statusCounts.Contains($status)) {
                $statusCounts[$status] = 0
            }

            $statusCounts[$status]++
        }

        if ([string]::IsNullOrWhiteSpace($worker.WorkerId)) {
            $requiredFieldFailures.workerId++
            Add-ValidationIssue -Collection $issues -Kind 'error' -Code 'missing-worker-id' -Message 'Worker is missing personIdExternal.'
        }

        if ([string]::IsNullOrWhiteSpace($worker.UserId) -and [string]::IsNullOrWhiteSpace($worker.UserName)) {
            $requiredFieldFailures.identity++
            Add-ValidationIssue -Collection $issues -Kind 'error' -Code 'missing-identity' -WorkerId $worker.WorkerId -Message 'Worker is missing both userId and userName.'
        }

        if ([string]::IsNullOrWhiteSpace($worker.FirstName)) {
            $requiredFieldFailures.firstName++
            Add-ValidationIssue -Collection $issues -Kind 'error' -Code 'missing-first-name' -WorkerId $worker.WorkerId -Message 'Worker is missing firstName.'
        }

        if ([string]::IsNullOrWhiteSpace($worker.LastName)) {
            $requiredFieldFailures.lastName++
            Add-ValidationIssue -Collection $issues -Kind 'error' -Code 'missing-last-name' -WorkerId $worker.WorkerId -Message 'Worker is missing lastName.'
        }

        if ([string]::IsNullOrWhiteSpace($worker.StartDate)) {
            $requiredFieldFailures.startDate++
            Add-ValidationIssue -Collection $issues -Kind 'error' -Code 'missing-start-date' -WorkerId $worker.WorkerId -Message 'Worker is missing startDate.'
        }

        if ([string]::IsNullOrWhiteSpace($worker.EmploymentStatus)) {
            $requiredFieldFailures.employmentStatus++
            Add-ValidationIssue -Collection $issues -Kind 'error' -Code 'missing-employment-status' -WorkerId $worker.WorkerId -Message 'Worker is missing emplStatus/employmentStatus.'
        }
        elseif ($AllowedEmploymentStatuses -notcontains $worker.EmploymentStatus) {
            Add-ValidationIssue -Collection $issues -Kind 'error' -Code 'unknown-employment-status' -WorkerId $worker.WorkerId -Message "Worker uses unsupported employment status '$($worker.EmploymentStatus)'."
        }

        foreach ($dateField in @('StartDate', 'EndDate', 'FirstDateWorked', 'LastDateWorked', 'LastModifiedDateTime', 'LatestTerminationDate')) {
            $value = [string]$worker.$dateField
            if (-not (Test-DateValue -Value $value)) {
                Add-ValidationIssue -Collection $issues -Kind 'error' -Code 'invalid-date' -WorkerId $worker.WorkerId -Message "Worker field '$dateField' contains an invalid date value '$value'."
            }
        }

        $startDate = Get-DateValue -Value $worker.StartDate
        $endDate = Get-DateValue -Value $worker.EndDate
        if ($null -ne $startDate -and $null -ne $endDate -and $endDate -lt $startDate) {
            Add-ValidationIssue -Collection $issues -Kind 'error' -Code 'end-date-before-start-date' -WorkerId $worker.WorkerId -Message 'Worker endDate is earlier than startDate.'
        }

        $firstDateWorked = Get-DateValue -Value $worker.FirstDateWorked
        $lastDateWorked = Get-DateValue -Value $worker.LastDateWorked
        if ($null -ne $firstDateWorked -and $null -ne $lastDateWorked -and $lastDateWorked -lt $firstDateWorked) {
            Add-ValidationIssue -Collection $issues -Kind 'error' -Code 'last-date-worked-before-first-date-worked' -WorkerId $worker.WorkerId -Message 'Worker lastDateWorked is earlier than firstDateWorked.'
        }
    }

    $duplicateWorkerIds = $workers |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.WorkerId) } |
        Group-Object WorkerId |
        Where-Object Count -gt 1
    foreach ($group in $duplicateWorkerIds) {
        Add-ValidationIssue -Collection $issues -Kind 'error' -Code 'duplicate-worker-id' -WorkerId $group.Name -Message "personIdExternal '$($group.Name)' is duplicated $($group.Count) times."
    }

    $duplicateUserIds = $workers |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.UserId) } |
        Group-Object UserId |
        Where-Object Count -gt 1
    foreach ($group in $duplicateUserIds) {
        Add-ValidationIssue -Collection $issues -Kind 'error' -Code 'duplicate-user-id' -WorkerId $group.Group[0].WorkerId -Message "userId '$($group.Name)' is duplicated $($group.Count) times."
    }

    $workerIdSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($worker in $workers) {
        if (-not [string]::IsNullOrWhiteSpace($worker.WorkerId)) {
            [void]$workerIdSet.Add($worker.WorkerId)
        }
    }

    foreach ($worker in $workers) {
        if (-not [string]::IsNullOrWhiteSpace($worker.ManagerId) -and -not $workerIdSet.Contains($worker.ManagerId)) {
            Add-ValidationIssue -Collection $issues -Kind 'warning' -Code 'manager-not-found' -WorkerId $worker.WorkerId -Message "Manager '$($worker.ManagerId)' does not exist in the current payload."
        }
    }

    $errorCount = @($issues | Where-Object kind -eq 'error').Count
    $warningCount = @($issues | Where-Object kind -eq 'warning').Count

    $report = [pscustomobject]@{
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        projectRoot = $projectRoot
        inputPath = $resolvedInputPath
        detectedFormat = $detectedFormat
        workerCount = $workers.Count
        result = if ($errorCount -eq 0) { 'passed' } else { 'failed' }
        allowedEmploymentStatuses = $AllowedEmploymentStatuses
        statusCounts = $statusCounts
        requiredFieldFailures = $requiredFieldFailures
        duplicateWorkerIds = @($duplicateWorkerIds | ForEach-Object { [pscustomobject]@{ value = $_.Name; count = $_.Count } })
        duplicateUserIds = @($duplicateUserIds | ForEach-Object { [pscustomobject]@{ value = $_.Name; count = $_.Count } })
        warnings = @($issues | Where-Object kind -eq 'warning')
        errors = @($issues | Where-Object kind -eq 'error')
    }

    if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
        $fullReportPath = if ([System.IO.Path]::IsPathRooted($ReportPath)) {
            [System.IO.Path]::GetFullPath($ReportPath)
        }
        else {
            [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $ReportPath))
        }

        $reportDirectory = Split-Path -Path $fullReportPath -Parent
        if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
            New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
        }

        $report | ConvertTo-Json -Depth 20 | Set-Content -Path $fullReportPath
    }

    Write-Host "SuccessFactors Contract Validation"
    Write-Host ("Input: {0}" -f $resolvedInputPath)
    Write-Host ("Detected format: {0}" -f $detectedFormat)
    Write-Host ("Worker count: {0}" -f $workers.Count)
    Write-Host ("Result: {0}" -f $report.result.ToUpperInvariant())
    Write-Host ("Errors: {0}" -f $errorCount)
    Write-Host ("Warnings: {0}" -f $warningCount)

    if ($statusCounts.Count -gt 0) {
        Write-Host 'Employment statuses:'
        foreach ($entry in $statusCounts.GetEnumerator() | Sort-Object Name) {
            Write-Host ("  {0}: {1}" -f $entry.Key, $entry.Value)
        }
    }

    if ($warningCount -gt 0) {
        Write-Host 'Warnings:'
        foreach ($warning in $report.warnings) {
            Write-Host ("  [{0}] {1}" -f $warning.code, $warning.message)
        }
    }

    if ($errorCount -gt 0) {
        Write-Host 'Errors:'
        foreach ($issue in $report.errors) {
            Write-Host ("  [{0}] {1}" -f $issue.code, $issue.message)
        }

        exit 1
    }
}
catch {
    Write-Error $_
    exit 1
}
