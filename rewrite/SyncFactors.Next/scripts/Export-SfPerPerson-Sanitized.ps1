param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$PersonIdExternal,

    [Parameter(Mandatory = $true, ParameterSetName = "OAuth")]
    [string]$ClientId,

    [Parameter(Mandatory = $true, ParameterSetName = "OAuth")]
    [string]$ClientSecret,

    [Parameter(Mandatory = $true, ParameterSetName = "OAuth")]
    [string]$CompanyId,

    [Parameter(ParameterSetName = "OAuth")]
    [string]$TokenUrl = "",

    [Parameter(Mandatory = $true, ParameterSetName = "Basic")]
    [string]$Username,

    [Parameter(Mandatory = $true, ParameterSetName = "Basic")]
    [string]$Password,

    [string]$OutputPath = ".\perperson-export-sanitized.json",
    [switch]$AliasOrgValues,
    [switch]$KeepPersonIdExternal
)

$ErrorActionPreference = "Stop"

function Get-StableNumber {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Input,

        [int]$Min = 1,
        [int]$Max = 99999
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Input)
        $hash = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    $value = [System.BitConverter]::ToUInt32($hash, 0)
    return $Min + ($value % ($Max - $Min + 1))
}

function Get-AliasValue {
    param(
        [string]$Prefix,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    return "{0}-{1:D2}" -f $Prefix, (Get-StableNumber -Input "$Prefix`:$Value" -Min 1 -Max 99)
}

function Set-StringPropertyIfPresent {
    param(
        [Parameter(Mandatory = $true)]
        [System.Text.Json.Nodes.JsonObject]$Object,

        [Parameter(Mandatory = $true)]
        [string]$PropertyName,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Object.ContainsKey($PropertyName)) {
        $Object[$PropertyName] = $Value
    }
}

function Get-FirstNavigationObject {
    param(
        [System.Text.Json.Nodes.JsonObject]$Object,
        [string]$NavigationName
    )

    if ($null -eq $Object -or -not $Object.ContainsKey($NavigationName)) {
        return $null
    }

    $navigation = $Object[$NavigationName]
    if ($null -eq $navigation -or $navigation.GetType().Name -ne "JsonObject") {
        return $null
    }

    $results = $navigation["results"]
    if ($null -eq $results -or $results.GetType().Name -ne "JsonArray" -or $results.Count -eq 0) {
        return $null
    }

    return $results[0]
}

function Sanitize-Worker {
    param(
        [Parameter(Mandatory = $true)]
        [System.Text.Json.Nodes.JsonObject]$Worker,

        [switch]$AliasOrgValues,
        [switch]$KeepPersonIdExternal
    )

    $originalPersonId = [string]$Worker["personIdExternal"]
    $personNumber = Get-StableNumber -Input $originalPersonId -Min 10000 -Max 99999
    $sanitizedPersonId = "mock-{0:D5}" -f $personNumber
    $sanitizedUserName = "user.{0:D5}" -f $personNumber
    $sanitizedFirstName = "Worker{0:D3}" -f (Get-StableNumber -Input "$originalPersonId`:fn" -Min 10 -Max 999)
    $sanitizedLastName = "Sample{0:D3}" -f (Get-StableNumber -Input "$originalPersonId`:ln" -Min 10 -Max 999)
    $sanitizedEmail = "{0}@example.test" -f $sanitizedUserName

    if (-not $KeepPersonIdExternal) {
        Set-StringPropertyIfPresent -Object $Worker -PropertyName "personIdExternal" -Value $sanitizedPersonId
    }

    Set-StringPropertyIfPresent -Object $Worker -PropertyName "username" -Value $sanitizedUserName
    Set-StringPropertyIfPresent -Object $Worker -PropertyName "userName" -Value $sanitizedUserName
    Set-StringPropertyIfPresent -Object $Worker -PropertyName "email" -Value $sanitizedEmail
    Set-StringPropertyIfPresent -Object $Worker -PropertyName "managerId" -Value ("mgr-{0:D5}" -f (Get-StableNumber -Input "$originalPersonId`:mgr" -Min 10000 -Max 99999))

    $personalInfo = Get-FirstNavigationObject -Object $Worker -NavigationName "personalInfoNav"
    if ($null -ne $personalInfo) {
        Set-StringPropertyIfPresent -Object $personalInfo -PropertyName "firstName" -Value $sanitizedFirstName
        Set-StringPropertyIfPresent -Object $personalInfo -PropertyName "lastName" -Value $sanitizedLastName
    }

    Set-StringPropertyIfPresent -Object $Worker -PropertyName "firstName" -Value $sanitizedFirstName
    Set-StringPropertyIfPresent -Object $Worker -PropertyName "lastName" -Value $sanitizedLastName

    $emailNav = Get-FirstNavigationObject -Object $Worker -NavigationName "emailNav"
    if ($null -ne $emailNav) {
        Set-StringPropertyIfPresent -Object $emailNav -PropertyName "emailAddress" -Value $sanitizedEmail
    }

    $employment = Get-FirstNavigationObject -Object $Worker -NavigationName "employmentNav"
    if ($null -eq $employment) {
        return
    }

    $jobInfo = Get-FirstNavigationObject -Object $employment -NavigationName "jobInfoNav"
    if ($null -eq $jobInfo) {
        return
    }

    Set-StringPropertyIfPresent -Object $jobInfo -PropertyName "managerId" -Value ("mgr-{0:D5}" -f (Get-StableNumber -Input "$originalPersonId`:mgr" -Min 10000 -Max 99999))
    Set-StringPropertyIfPresent -Object $jobInfo -PropertyName "jobTitle" -Value (Get-AliasValue -Prefix "Job" -Value ([string]$jobInfo["jobTitle"]))

    $locationNav = $jobInfo["locationNav"]
    if ($null -ne $locationNav -and $locationNav.GetType().Name -eq "JsonObject") {
        Set-StringPropertyIfPresent -Object $locationNav -PropertyName "LocationName" -Value (
            if ($AliasOrgValues) { Get-AliasValue -Prefix "Location" -Value ([string]$locationNav["LocationName"]) } else { [string]$locationNav["LocationName"] }
        )
        Set-StringPropertyIfPresent -Object $locationNav -PropertyName "officeLocationAddress" -Value ("Suite {0} Example Way" -f (Get-StableNumber -Input "$originalPersonId`:addr" -Min 100 -Max 999))
        Set-StringPropertyIfPresent -Object $locationNav -PropertyName "officeLocationCity" -Value ("City{0:D2}" -f (Get-StableNumber -Input "$originalPersonId`:city" -Min 10 -Max 99))
        Set-StringPropertyIfPresent -Object $locationNav -PropertyName "officeLocationZipCode" -Value ("{0:D5}" -f (Get-StableNumber -Input "$originalPersonId`:zip" -Min 10000 -Max 99999))
    }

    foreach ($navSpec in @(
        @{ Name = "departmentNav"; Property = "department"; Prefix = "Department" },
        @{ Name = "companyNav"; Property = "company"; Prefix = "Company" },
        @{ Name = "businessUnitNav"; Property = "businessUnit"; Prefix = "BusinessUnit" },
        @{ Name = "divisionNav"; Property = "division"; Prefix = "Division" },
        @{ Name = "costCenterNav"; Property = "costCenterDescription"; Prefix = "CostCenter" }
    )) {
        $nav = $jobInfo[$navSpec.Name]
        if ($null -ne $nav -and $nav.GetType().Name -eq "JsonObject" -and $AliasOrgValues) {
            Set-StringPropertyIfPresent -Object $nav -PropertyName $navSpec.Property -Value (
                Get-AliasValue -Prefix $navSpec.Prefix -Value ([string]$nav[$navSpec.Property])
            )
        }
    }

    if ($AliasOrgValues) {
        foreach ($propSpec in @(
            @{ Property = "department"; Prefix = "Department" },
            @{ Property = "company"; Prefix = "Company" },
            @{ Property = "businessUnit"; Prefix = "BusinessUnit" },
            @{ Property = "division"; Prefix = "Division" },
            @{ Property = "costCenter"; Prefix = "CostCenter" },
            @{ Property = "location"; Prefix = "Location" }
        )) {
            Set-StringPropertyIfPresent -Object $jobInfo -PropertyName $propSpec.Property -Value (
                Get-AliasValue -Prefix $propSpec.Prefix -Value ([string]$jobInfo[$propSpec.Property])
            )
        }
    }

    foreach ($phoneProp in @("phone", "phoneNumber", "businessPhone", "cellPhone", "mobilePhone")) {
        Set-StringPropertyIfPresent -Object $Worker -PropertyName $phoneProp -Value ("555-{0:D3}-{1:D4}" -f (Get-StableNumber -Input "$originalPersonId`:$phoneProp:a" -Min 100 -Max 999), (Get-StableNumber -Input "$originalPersonId`:$phoneProp:b" -Min 1000 -Max 9999))
        Set-StringPropertyIfPresent -Object $jobInfo -PropertyName $phoneProp -Value ("555-{0:D3}-{1:D4}" -f (Get-StableNumber -Input "$originalPersonId`:$phoneProp:c" -Min 100 -Max 999), (Get-StableNumber -Input "$originalPersonId`:$phoneProp:d" -Min 1000 -Max 9999))
    }
}

if ($PSCmdlet.ParameterSetName -eq "OAuth" -and [string]::IsNullOrWhiteSpace($TokenUrl)) {
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

if ($PSCmdlet.ParameterSetName -eq "OAuth") {
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

    $headers = @{
        Authorization         = "Bearer $($tokenResponse.access_token)"
        Accept                = "application/json"
        "x-correlation-id"    = [guid]::NewGuid().ToString()
        "X-SF-Correlation-Id" = [guid]::NewGuid().ToString()
        "X-SF-Process-Name"   = "SyncFactors.Export.Sanitized"
        "X-SF-Execution-Id"   = $PersonIdExternal
    }
}
else {
    $pair = "{0}:{1}" -f $Username, $Password
    $basicToken = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($pair))
    $headers = @{
        Authorization         = "Basic $basicToken"
        Accept                = "application/json"
        "x-correlation-id"    = [guid]::NewGuid().ToString()
        "X-SF-Correlation-Id" = [guid]::NewGuid().ToString()
        "X-SF-Process-Name"   = "SyncFactors.Export.Sanitized"
        "X-SF-Execution-Id"   = $PersonIdExternal
    }
}

$response = Invoke-WebRequest `
    -Method Get `
    -Uri $requestUri `
    -Headers $headers

$document = [System.Text.Json.Nodes.JsonNode]::Parse($response.Content)
if ($null -eq $document) {
    throw "Received empty JSON response."
}

$results = $document["d"]["results"]
if ($null -eq $results -or $results.GetType().Name -ne "JsonArray") {
    throw "Expected SuccessFactors-style JSON at d.results."
}

foreach ($item in $results) {
    if ($null -ne $item -and $item.GetType().Name -eq "JsonObject") {
        Sanitize-Worker -Worker $item -AliasOrgValues:$AliasOrgValues -KeepPersonIdExternal:$KeepPersonIdExternal
    }
}

$jsonOptions = [System.Text.Json.JsonSerializerOptions]::new()
$jsonOptions.WriteIndented = $true
$sanitizedJson = $document.ToJsonString($jsonOptions)
[System.IO.File]::WriteAllText($OutputPath, $sanitizedJson, [System.Text.Encoding]::UTF8)

Write-Host "Saved sanitized export to $OutputPath"
Write-Host "Query URI:"
Write-Host $requestUri
Write-Host "Sanitization applied:"
Write-Host "- names"
Write-Host "- usernames"
Write-Host "- emails"
Write-Host "- addresses/city/zip"
Write-Host "- phone-like fields when present"
Write-Host "- manager identifiers"
if ($AliasOrgValues) {
    Write-Host "- org labels (company/department/location/businessUnit/division/costCenter)"
}
if (-not $KeepPersonIdExternal) {
    Write-Host "- personIdExternal"
}
