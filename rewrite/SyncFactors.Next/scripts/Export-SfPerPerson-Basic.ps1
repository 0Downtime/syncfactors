param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$Username,

    [Parameter(Mandatory = $true)]
    [string]$Password,

    [Parameter(Mandatory = $true)]
    [string]$PersonIdExternal,

    [string]$OutputPath = ".\perperson-export.json",
    [string[]]$ExcludeSelectPath = @(),
    [string[]]$ExcludeExpandPath = @()
)

$ErrorActionPreference = "Stop"

function Remove-ExcludedPaths {
    param(
        [string[]]$Values,
        [string[]]$Excluded
    )

    if ($null -eq $Excluded -or $Excluded.Count -eq 0) {
        return $Values
    }

    $excludedSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $Excluded) {
        if (-not [string]::IsNullOrWhiteSpace($item)) {
            [void]$excludedSet.Add($item)
        }
    }

    return @($Values | Where-Object { -not $excludedSet.Contains($_) })
}

$selectValues = @(
    "personIdExternal",
    "personalInfoNav/firstName",
    "personalInfoNav/lastName",
    "personalInfoNav/preferredName",
    "personalInfoNav/displayName",
    "employmentNav/startDate",
    "emailNav/emailAddress",
    "emailNav/isPrimary",
    "emailNav/emailType",
    "employmentNav/userId",
    "employmentNav/jobInfoNav/departmentNav/name_localized",
    "employmentNav/jobInfoNav/departmentNav/name",
    "employmentNav/jobInfoNav/companyNav/name_localized",
    "employmentNav/jobInfoNav/companyNav/externalCode",
    "employmentNav/jobInfoNav/locationNav/name",
    "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/address1",
    "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/city",
    "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/zipCode",
    "employmentNav/jobInfoNav/jobTitle",
    "employmentNav/jobInfoNav/businessUnitNav/name_localized",
    "employmentNav/jobInfoNav/businessUnitNav/externalCode",
    "employmentNav/jobInfoNav/divisionNav/name_localized",
    "employmentNav/jobInfoNav/costCenterNav/name_localized",
    "employmentNav/jobInfoNav/costCenterNav/description_localized",
    "employmentNav/jobInfoNav/costCenterNav/externalCode",
    "employmentNav/jobInfoNav/employeeClass",
    "employmentNav/jobInfoNav/employeeType",
    "employmentNav/userNav/manager/empInfo/personIdExternal"
)

$expandValues = @(
    "employmentNav",
    "employmentNav/jobInfoNav",
    "personalInfoNav",
    "emailNav",
    "employmentNav/jobInfoNav/companyNav",
    "employmentNav/jobInfoNav/departmentNav",
    "employmentNav/jobInfoNav/businessUnitNav",
    "employmentNav/jobInfoNav/costCenterNav",
    "employmentNav/jobInfoNav/divisionNav",
    "employmentNav/jobInfoNav/locationNav",
    "employmentNav/jobInfoNav/locationNav/addressNavDEFLT",
    "employmentNav/userNav",
    "employmentNav/userNav/manager",
    "employmentNav/userNav/manager/empInfo"
)

$selectValues = Remove-ExcludedPaths -Values $selectValues -Excluded $ExcludeSelectPath
$expandValues = Remove-ExcludedPaths -Values $expandValues -Excluded $ExcludeExpandPath

$select = $selectValues -join ","
$expand = $expandValues -join ","

$escapedWorkerId = $PersonIdExternal.Replace("'", "''")
$filter = "personIdExternal eq '$escapedWorkerId'"

$queryParams = @{
    '$format' = 'json'
    '$filter' = $filter
    '$select' = $select
    '$expand' = $expand
}

$queryString = ($queryParams.GetEnumerator() | ForEach-Object {
    "{0}={1}" -f [System.Uri]::EscapeDataString($_.Key), [System.Uri]::EscapeDataString([string]$_.Value)
}) -join "&"

$requestUri = "{0}/PerPerson?{1}" -f $BaseUrl.TrimEnd("/"), $queryString

$pair = "{0}:{1}" -f $Username, $Password
$basicToken = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($pair))

$headers = @{
    Authorization         = "Basic $basicToken"
    Accept                = "application/json"
    "x-correlation-id"    = [guid]::NewGuid().ToString()
    "X-SF-Correlation-Id" = [guid]::NewGuid().ToString()
    "X-SF-Process-Name"   = "SyncFactors.Export"
    "X-SF-Execution-Id"   = $PersonIdExternal
}

$response = Invoke-WebRequest `
    -Method Get `
    -Uri $requestUri `
    -Headers $headers

[System.IO.File]::WriteAllText($OutputPath, $response.Content, [System.Text.Encoding]::UTF8)

Write-Host "Saved export to $OutputPath"
Write-Host "Query URI:"
Write-Host $requestUri
