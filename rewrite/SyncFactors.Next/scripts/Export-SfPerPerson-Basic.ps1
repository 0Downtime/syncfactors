param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$Username,

    [Parameter(Mandatory = $true)]
    [string]$Password,

    [Parameter(Mandatory = $true)]
    [string]$PersonIdExternal,

    [string]$OutputPath = ".\perperson-export.json"
)

$ErrorActionPreference = "Stop"

$select = @(
    "personIdExternal",
    "personalInfoNav/firstName",
    "personalInfoNav/lastName",
    "employmentNav/startDate",
    "emailNav/emailAddress",
    "employmentNav/jobInfoNav/departmentNav/department",
    "employmentNav/jobInfoNav/companyNav/company",
    "employmentNav/jobInfoNav/locationNav/LocationName",
    "employmentNav/jobInfoNav/locationNav/officeLocationAddress",
    "employmentNav/jobInfoNav/locationNav/officeLocationCity",
    "employmentNav/jobInfoNav/locationNav/officeLocationZipCode",
    "employmentNav/jobInfoNav/jobTitle",
    "employmentNav/jobInfoNav/businessUnitNav/businessUnit",
    "employmentNav/jobInfoNav/divisionNav/division",
    "employmentNav/jobInfoNav/costCenterNav/costCenterDescription",
    "employmentNav/jobInfoNav/employeeClass",
    "employmentNav/jobInfoNav/employeeType",
    "employmentNav/jobInfoNav/managerId"
) -join ","

$expand = @(
    "employmentNav",
    "employmentNav/jobInfoNav",
    "personalInfoNav",
    "emailNav",
    "employmentNav/jobInfoNav/companyNav",
    "employmentNav/jobInfoNav/departmentNav",
    "employmentNav/jobInfoNav/businessUnitNav",
    "employmentNav/jobInfoNav/costCenterNav",
    "employmentNav/jobInfoNav/divisionNav",
    "employmentNav/jobInfoNav/locationNav"
) -join ","

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
