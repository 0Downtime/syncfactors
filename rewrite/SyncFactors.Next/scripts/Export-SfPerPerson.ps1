param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$ClientId,

    [Parameter(Mandatory = $true)]
    [string]$ClientSecret,

    [Parameter(Mandatory = $true)]
    [string]$CompanyId,

    [Parameter(Mandatory = $true)]
    [string]$PersonIdExternal,

    [string]$TokenUrl = "",
    [string]$OutputPath = ".\perperson-export.json"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($TokenUrl)) {
    $root = $BaseUrl.TrimEnd("/")
    if ($root -match "/odata/v2$") {
        $root = $root.Substring(0, $root.Length - "/odata/v2".Length)
    }
    $TokenUrl = "$root/oauth/token"
}

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

$tokenBody = @{
    grant_type    = "client_credentials"
    client_id     = $ClientId
    client_secret = $ClientSecret
    company_id    = $CompanyId
}

$tokenResponse = Invoke-RestMethod `
    -Method Post `
    -Uri $TokenUrl `
    -ContentType "application/x-www-form-urlencoded" `
    -Body $tokenBody `
    -Headers @{
        Accept = "application/json"
    }

if (-not $tokenResponse.access_token) {
    throw "OAuth response did not include access_token."
}

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

$headers = @{
    Authorization         = "Bearer $($tokenResponse.access_token)"
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
