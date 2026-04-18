[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InputPath,
    [string]$ReportPath,
    [string[]]$AllowedEmploymentStatuses = @('A', 'U', 'T', 'I', '64303', '64304', '64307', '64308'),
    [switch]$AllowUnknownEmploymentStatuses
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

function Get-OptionalPropertyValue {
    param(
        [Parameter(Mandatory)]
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

function Get-ArrayValue {
    param($Value)

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [System.Array]) {
        return @($Value)
    }

    return @($Value)
}

function Get-ResultsArray {
    param($Value)

    $results = Get-OptionalPropertyValue -Object $Value -Name 'results'
    if ($null -ne $results) {
        return Get-ArrayValue $results
    }

    return Get-ArrayValue $Value
}

function Get-FirstNonEmptyValue {
    param([object[]]$Candidates)

    foreach ($candidate in $Candidates) {
        if ($candidate -isnot [string]) {
            if ($null -ne $candidate) {
                return $candidate
            }
            continue
        }

        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            return $candidate
        }
    }

    return $null
}

function Test-HasText {
    param($Value)

    return -not [string]::IsNullOrWhiteSpace([string]$Value)
}

function Normalize-FixtureWorker {
    param(
        [Parameter(Mandatory)]
        $Worker
    )

    return [pscustomobject]@{
        WorkerId = Get-OptionalPropertyValue -Object $Worker -Name 'personIdExternal'
        UserId = Get-FirstNonEmptyValue @(
            (Get-OptionalPropertyValue -Object $Worker -Name 'userId'),
            (Get-OptionalPropertyValue -Object $Worker -Name 'userName'))
        UserName = Get-OptionalPropertyValue -Object $Worker -Name 'userName'
        FirstName = Get-OptionalPropertyValue -Object $Worker -Name 'firstName'
        LastName = Get-OptionalPropertyValue -Object $Worker -Name 'lastName'
        PreferredName = Get-OptionalPropertyValue -Object $Worker -Name 'preferredName'
        DisplayName = Get-OptionalPropertyValue -Object $Worker -Name 'displayName'
        Email = Get-OptionalPropertyValue -Object $Worker -Name 'email'
        StartDate = Get-OptionalPropertyValue -Object $Worker -Name 'startDate'
        EndDate = Get-OptionalPropertyValue -Object $Worker -Name 'endDate'
        FirstDateWorked = Get-OptionalPropertyValue -Object $Worker -Name 'firstDateWorked'
        LastDateWorked = Get-OptionalPropertyValue -Object $Worker -Name 'lastDateWorked'
        LastModifiedDateTime = Get-OptionalPropertyValue -Object $Worker -Name 'lastModifiedDateTime'
        EmploymentStatus = Get-FirstNonEmptyValue @(
            (Get-OptionalPropertyValue -Object $Worker -Name 'employmentStatus'),
            (Get-OptionalPropertyValue -Object $Worker -Name 'emplStatus'))
        LatestTerminationDate = Get-OptionalPropertyValue -Object $Worker -Name 'latestTerminationDate'
        ActiveEmploymentsCount = Get-OptionalPropertyValue -Object $Worker -Name 'activeEmploymentsCount'
        ManagerId = Get-OptionalPropertyValue -Object $Worker -Name 'managerId'
        SourceShape = 'FixtureWorker'
    }
}

function Normalize-PerPersonWorker {
    param(
        [Parameter(Mandatory)]
        $Worker
    )

    $personalInfo = @(Get-ResultsArray (Get-OptionalPropertyValue -Object $Worker -Name 'personalInfoNav')) | Select-Object -First 1
    $emails = @(Get-ResultsArray (Get-OptionalPropertyValue -Object $Worker -Name 'emailNav'))
    $primaryEmail = $emails | Where-Object { (Get-OptionalPropertyValue -Object $_ -Name 'isPrimary') -eq $true } | Select-Object -First 1
    if ($null -eq $primaryEmail) {
        $primaryEmail = $emails | Select-Object -First 1
    }

    $employment = @(Get-ResultsArray (Get-OptionalPropertyValue -Object $Worker -Name 'employmentNav')) | Select-Object -First 1
    $jobInfo = @(Get-ResultsArray (Get-OptionalPropertyValue -Object (Get-OptionalPropertyValue -Object $employment -Name 'jobInfoNav')) ) | Select-Object -First 1
    $userNav = Get-OptionalPropertyValue -Object $employment -Name 'userNav'
    $manager = Get-OptionalPropertyValue -Object $userNav -Name 'manager'
    $managerEmpInfo = Get-OptionalPropertyValue -Object $manager -Name 'empInfo'
    $terminationInfo = Get-OptionalPropertyValue -Object $Worker -Name 'personEmpTerminationInfoNav'

    return [pscustomobject]@{
        WorkerId = Get-OptionalPropertyValue -Object $Worker -Name 'personIdExternal'
        UserId = Get-FirstNonEmptyValue @(
            (Get-OptionalPropertyValue -Object $employment -Name 'userId'),
            (Get-OptionalPropertyValue -Object $userNav -Name 'username'))
        UserName = Get-OptionalPropertyValue -Object $userNav -Name 'username'
        FirstName = Get-OptionalPropertyValue -Object $personalInfo -Name 'firstName'
        LastName = Get-OptionalPropertyValue -Object $personalInfo -Name 'lastName'
        PreferredName = Get-OptionalPropertyValue -Object $personalInfo -Name 'preferredName'
        DisplayName = Get-OptionalPropertyValue -Object $personalInfo -Name 'displayName'
        Email = Get-FirstNonEmptyValue @(
            (Get-OptionalPropertyValue -Object $primaryEmail -Name 'emailAddress'),
            (Get-OptionalPropertyValue -Object $primaryEmail -Name 'email'))
        StartDate = Get-OptionalPropertyValue -Object $employment -Name 'startDate'
        EndDate = Get-FirstNonEmptyValue @(
            (Get-OptionalPropertyValue -Object $employment -Name 'endDate'),
            (Get-OptionalPropertyValue -Object $jobInfo -Name 'endDate'))
        FirstDateWorked = Get-OptionalPropertyValue -Object $jobInfo -Name 'startDate'
        LastDateWorked = Get-OptionalPropertyValue -Object $jobInfo -Name 'lastDateWorked'
        LastModifiedDateTime = Get-OptionalPropertyValue -Object $Worker -Name 'lastModifiedDateTime'
        EmploymentStatus = Get-FirstNonEmptyValue @(
            (Get-OptionalPropertyValue -Object $jobInfo -Name 'emplStatus'),
            (Get-OptionalPropertyValue -Object $Worker -Name 'employmentStatus'),
            (Get-OptionalPropertyValue -Object $Worker -Name 'emplStatus'))
        LatestTerminationDate = Get-OptionalPropertyValue -Object $terminationInfo -Name 'latestTerminationDate'
        ActiveEmploymentsCount = Get-OptionalPropertyValue -Object $terminationInfo -Name 'activeEmploymentsCount'
        ManagerId = Get-OptionalPropertyValue -Object $managerEmpInfo -Name 'personIdExternal'
        SourceShape = 'PerPersonWorker'
    }
}

function Resolve-NormalizedWorkers {
    param(
        [Parameter(Mandatory)]
        $Document
    )

    $fixtureWorkers = Get-OptionalPropertyValue -Object $Document -Name 'workers'
    if ($null -ne $fixtureWorkers) {
        return [pscustomobject]@{
            SourceType = 'MockFixtureDocument'
            Workers = @(Get-ArrayValue $fixtureWorkers | ForEach-Object { Normalize-FixtureWorker -Worker $_ })
        }
    }

    $odataRoot = Get-OptionalPropertyValue -Object $Document -Name 'd'
    $odataResults = if ($null -ne $odataRoot) { Get-ResultsArray $odataRoot } else { @() }
    if ($odataResults.Count -gt 0) {
        return [pscustomobject]@{
            SourceType = 'PerPersonOData'
            Workers = @($odataResults | ForEach-Object { Normalize-PerPersonWorker -Worker $_ })
        }
    }

    $topLevelResults = Get-OptionalPropertyValue -Object $Document -Name 'results'
    if ($null -ne $topLevelResults) {
        return [pscustomobject]@{
            SourceType = 'TopLevelResults'
            Workers = @(Get-ArrayValue $topLevelResults | ForEach-Object { Normalize-PerPersonWorker -Worker $_ })
        }
    }

    $documentArray = Get-ArrayValue $Document
    if ($documentArray.Count -gt 0) {
        $firstItem = $documentArray[0]
        $detectedType = if ($null -ne (Get-OptionalPropertyValue -Object $firstItem -Name 'personIdExternal') -and
            ($null -ne (Get-OptionalPropertyValue -Object $firstItem -Name 'email') -or
             $null -ne (Get-OptionalPropertyValue -Object $firstItem -Name 'employmentNav'))) {
            'ArrayInput'
        }
        else {
            $null
        }

        if ($null -ne $detectedType) {
            $normalizer = if ($null -ne (Get-OptionalPropertyValue -Object $firstItem -Name 'employmentNav')) {
                { param($worker) Normalize-PerPersonWorker -Worker $worker }
            }
            else {
                { param($worker) Normalize-FixtureWorker -Worker $worker }
            }

            return [pscustomobject]@{
                SourceType = $detectedType
                Workers = @($documentArray | ForEach-Object { & $normalizer $_ })
            }
        }
    }

    throw "Unsupported SuccessFactors contract input shape."
}

function Add-Issue {
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.List[string]]$Target,
        [Parameter(Mandatory)]
        [string]$Message
    )

    $Target.Add($Message) | Out-Null
}

function Add-MissingCount {
    param(
        [Parameter(Mandatory)]
        [hashtable]$MissingFieldCounts,
        [Parameter(Mandatory)]
        [string]$FieldName
    )

    if ($MissingFieldCounts.ContainsKey($FieldName)) {
        $MissingFieldCounts[$FieldName]++
        return
    }

    $MissingFieldCounts[$FieldName] = 1
}

function Test-AndParseDateValue {
    param(
        [string]$Value,
        [string]$FieldName,
        [string]$WorkerLabel,
        [System.Collections.Generic.List[string]]$Errors
    )

    if (-not (Test-HasText $Value)) {
        return $null
    }

    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($Value, [ref]$parsed)) {
        Add-Issue -Target $Errors -Message "worker '$WorkerLabel' has invalid $FieldName '$Value'."
        return $null
    }

    return $parsed
}

$projectRoot = Resolve-ProjectRoot
$resolvedInputPath = Resolve-RequiredPath -Path $InputPath -Label 'SuccessFactors contract input'
$inputJson = Get-Content -Path $resolvedInputPath -Raw
$document = $inputJson | ConvertFrom-Json -Depth 100
$normalized = Resolve-NormalizedWorkers -Document $document
$workers = @($normalized.Workers)

$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
$missingFieldCounts = @{}
$statusCounts = @{}
$workerIds = [System.Collections.Generic.List[string]]::new()
$userIds = [System.Collections.Generic.List[string]]::new()
$emails = [System.Collections.Generic.List[string]]::new()

foreach ($worker in $workers) {
    $workerLabel = if (Test-HasText $worker.WorkerId) { $worker.WorkerId } else { '(missing-worker-id)' }

    if (-not (Test-HasText $worker.WorkerId)) {
        Add-MissingCount -MissingFieldCounts $missingFieldCounts -FieldName 'personIdExternal'
        Add-Issue -Target $errors -Message "worker record is missing personIdExternal."
    }
    else {
        $workerIds.Add([string]$worker.WorkerId) | Out-Null
    }

    if (-not (Test-HasText $worker.UserId) -and -not (Test-HasText $worker.UserName)) {
        Add-MissingCount -MissingFieldCounts $missingFieldCounts -FieldName 'identity'
        Add-Issue -Target $errors -Message "worker '$workerLabel' is missing both userId and userName."
    }
    elseif (Test-HasText $worker.UserId) {
        $userIds.Add([string]$worker.UserId) | Out-Null
    }
    else {
        $userIds.Add([string]$worker.UserName) | Out-Null
    }

    if (-not (Test-HasText $worker.FirstName)) {
        Add-MissingCount -MissingFieldCounts $missingFieldCounts -FieldName 'firstName'
        Add-Issue -Target $errors -Message "worker '$workerLabel' is missing firstName."
    }

    if (-not (Test-HasText $worker.LastName)) {
        Add-MissingCount -MissingFieldCounts $missingFieldCounts -FieldName 'lastName'
        Add-Issue -Target $errors -Message "worker '$workerLabel' is missing lastName."
    }

    if (-not (Test-HasText $worker.StartDate)) {
        Add-MissingCount -MissingFieldCounts $missingFieldCounts -FieldName 'startDate'
        Add-Issue -Target $errors -Message "worker '$workerLabel' is missing startDate."
    }

    if (-not (Test-HasText $worker.EmploymentStatus)) {
        Add-MissingCount -MissingFieldCounts $missingFieldCounts -FieldName 'emplStatus'
        Add-Issue -Target $errors -Message "worker '$workerLabel' is missing emplStatus."
    }
    else {
        if ($statusCounts.ContainsKey($worker.EmploymentStatus)) {
            $statusCounts[$worker.EmploymentStatus]++
        }
        else {
            $statusCounts[$worker.EmploymentStatus] = 1
        }

        if (-not $AllowUnknownEmploymentStatuses -and
            $AllowedEmploymentStatuses -notcontains [string]$worker.EmploymentStatus) {
            Add-Issue -Target $errors -Message "worker '$workerLabel' uses unexpected emplStatus '$($worker.EmploymentStatus)'."
        }
    }

    if (Test-HasText $worker.Email) {
        $emails.Add(([string]$worker.Email).ToLowerInvariant()) | Out-Null
    }

    $startDate = Test-AndParseDateValue -Value $worker.StartDate -FieldName 'startDate' -WorkerLabel $workerLabel -Errors $errors
    $endDate = Test-AndParseDateValue -Value $worker.EndDate -FieldName 'endDate' -WorkerLabel $workerLabel -Errors $errors
    $firstDateWorked = Test-AndParseDateValue -Value $worker.FirstDateWorked -FieldName 'firstDateWorked' -WorkerLabel $workerLabel -Errors $errors
    $lastDateWorked = Test-AndParseDateValue -Value $worker.LastDateWorked -FieldName 'lastDateWorked' -WorkerLabel $workerLabel -Errors $errors
    [void](Test-AndParseDateValue -Value $worker.LastModifiedDateTime -FieldName 'lastModifiedDateTime' -WorkerLabel $workerLabel -Errors $errors)
    [void](Test-AndParseDateValue -Value $worker.LatestTerminationDate -FieldName 'latestTerminationDate' -WorkerLabel $workerLabel -Errors $errors)

    if ($null -ne $startDate -and $null -ne $endDate -and $endDate -lt $startDate) {
        Add-Issue -Target $errors -Message "worker '$workerLabel' has endDate earlier than startDate."
    }

    if ($null -ne $firstDateWorked -and $null -ne $lastDateWorked -and $lastDateWorked -lt $firstDateWorked) {
        Add-Issue -Target $errors -Message "worker '$workerLabel' has lastDateWorked earlier than firstDateWorked."
    }
}

$duplicateWorkerIds = @($workerIds | Group-Object | Where-Object Count -gt 1 | Sort-Object Name)
$duplicateUserIds = @($userIds | Group-Object | Where-Object Count -gt 1 | Sort-Object Name)
$duplicateEmails = @($emails | Group-Object | Where-Object Count -gt 1 | Sort-Object Name)

foreach ($duplicate in $duplicateWorkerIds) {
    Add-Issue -Target $errors -Message "duplicate personIdExternal '$($duplicate.Name)' appears $($duplicate.Count) times."
}

foreach ($duplicate in $duplicateUserIds) {
    Add-Issue -Target $errors -Message "duplicate identity '$($duplicate.Name)' appears $($duplicate.Count) times."
}

foreach ($duplicate in $duplicateEmails) {
    Add-Issue -Target $warnings -Message "duplicate email '$($duplicate.Name)' appears $($duplicate.Count) times."
}

$knownWorkerIds = $workerIds | Sort-Object -Unique
foreach ($worker in $workers) {
    if (Test-HasText $worker.ManagerId -and $knownWorkerIds -notcontains [string]$worker.ManagerId) {
        $workerLabel = if (Test-HasText $worker.WorkerId) { $worker.WorkerId } else { '(missing-worker-id)' }
        Add-Issue -Target $warnings -Message "worker '$workerLabel' references managerId '$($worker.ManagerId)' that is not present in this input set."
    }
}

$sortedStatusCounts = [ordered]@{}
foreach ($pair in ($statusCounts.GetEnumerator() | Sort-Object Name)) {
    $sortedStatusCounts[$pair.Name] = $pair.Value
}

$sortedMissingFieldCounts = [ordered]@{}
foreach ($pair in ($missingFieldCounts.GetEnumerator() | Sort-Object Name)) {
    $sortedMissingFieldCounts[$pair.Name] = $pair.Value
}

$resultLabel = if ($errors.Count -eq 0) { 'PASSED' } else { 'FAILED' }
$statusSummary = if ($sortedStatusCounts.Count -eq 0) {
    'none'
}
else {
    (($sortedStatusCounts.GetEnumerator() | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value }) -join ', ')
}
$missingSummary = if ($sortedMissingFieldCounts.Count -eq 0) {
    'none'
}
else {
    (($sortedMissingFieldCounts.GetEnumerator() | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value }) -join ', ')
}

$report = [ordered]@{
    InputPath = $resolvedInputPath
    SourceType = $normalized.SourceType
    WorkerCount = $workers.Count
    Result = $resultLabel
    AllowedEmploymentStatuses = @($AllowedEmploymentStatuses)
    StatusCounts = $sortedStatusCounts
    MissingFieldCounts = $sortedMissingFieldCounts
    DuplicateWorkerIds = @($duplicateWorkerIds | ForEach-Object { [ordered]@{ Value = $_.Name; Count = $_.Count } })
    DuplicateIdentities = @($duplicateUserIds | ForEach-Object { [ordered]@{ Value = $_.Name; Count = $_.Count } })
    DuplicateEmails = @($duplicateEmails | ForEach-Object { [ordered]@{ Value = $_.Name; Count = $_.Count } })
    WarningCount = $warnings.Count
    ErrorCount = $errors.Count
    Warnings = @($warnings)
    Errors = @($errors)
    GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $resolvedReportPath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $ReportPath))
    Directory.CreateDirectory((Split-Path -Parent $resolvedReportPath)) | Out-Null
    $report | ConvertTo-Json -Depth 10 | Set-Content -Path $resolvedReportPath -Encoding UTF8
}

Write-Host "SuccessFactors Contract Validation" -ForegroundColor Cyan
Write-Host "Input: $resolvedInputPath"
Write-Host "Source Type: $($normalized.SourceType)"
Write-Host "Workers: $($workers.Count)"
Write-Host "Result: $resultLabel"
Write-Host "Employment Status Counts: $statusSummary"
Write-Host "Missing Required Fields: $missingSummary"
Write-Host "Warnings: $($warnings.Count)"
Write-Host "Errors: $($errors.Count)"
if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    Write-Host "Report: $resolvedReportPath"
}

if ($warnings.Count -gt 0) {
    Write-Host ""
    Write-Host "Warnings" -ForegroundColor Yellow
    foreach ($warning in $warnings) {
        Write-Host "- $warning"
    }
}

if ($errors.Count -gt 0) {
    Write-Host ""
    Write-Host "Errors" -ForegroundColor Red
    foreach ($error in $errors) {
        Write-Host "- $error"
    }

    throw "SuccessFactors contract validation failed with $($errors.Count) error(s)."
}
