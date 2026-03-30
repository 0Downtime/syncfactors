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

    [string]$OutputPath = ".\perperson-metadata-profile.json",
    [string[]]$ExcludeSelectPath = @(),
    [string[]]$ExcludeExpandPath = @(),
    [string[]]$AdditionalSelectPath = @(),
    [string[]]$AdditionalExpandPath = @(),
    [switch]$ShowDiagnostics
)

$ErrorActionPreference = "Stop"

$scriptPath = Join-Path -Path $PSScriptRoot -ChildPath "Export-SfPerPerson-Sanitized.ps1"
if (-not (Test-Path -Path $scriptPath)) {
    throw "Required script not found: $scriptPath"
}

$selectPaths = @(
    "personIdExternal",
    "userId",
    "personalInfoNav/firstName",
    "personalInfoNav/lastName",
    "personalInfoNav/middleName",
    "employmentNav/originalStartDate",
    "employmentNav/startDate",
    "employmentNav/jobInfoNav/emplStatus",
    "employmentNav/jobInfoNav/jobCode",
    "employmentNav/jobInfoNav/jobTitle",
    "employmentNav/jobInfoNav/position",
    "employmentNav/jobInfoNav/managerId",
    "employmentNav/jobInfoNav/companyNav/name",
    "employmentNav/jobInfoNav/businessUnit",
    "employmentNav/jobInfoNav/businessUnitNav/name",
    "employmentNav/jobInfoNav/location",
    "employmentNav/jobInfoNav/locationNav/name",
    "employmentNav/jobInfoNav/costCenter",
    "employmentNav/jobInfoNav/costCenterNav/name",
    "employmentNav/jobInfoNav/employeeType",
    "employmentNav/jobInfoNav/employeeClass",
    "employmentNav/jobInfoNav/departmentNav/cust_HeadofFunction",
    "emailNav/emailAddress"
)

$expandPaths = @(
    "employmentNav",
    "employmentNav/jobInfoNav",
    "personalInfoNav",
    "emailNav",
    "employmentNav/jobInfoNav/companyNav",
    "employmentNav/jobInfoNav/businessUnitNav",
    "employmentNav/jobInfoNav/locationNav",
    "employmentNav/jobInfoNav/costCenterNav",
    "employmentNav/jobInfoNav/departmentNav"
)

$commonParameters = @{
    BaseUrl = $BaseUrl
    PersonIdExternal = $PersonIdExternal
    OutputPath = $OutputPath
    SkipSanitization = $true
    ExcludeSelectPath = $ExcludeSelectPath
    ExcludeExpandPath = $ExcludeExpandPath
    AdditionalSelectPath = @($selectPaths + $AdditionalSelectPath)
    AdditionalExpandPath = @($expandPaths + $AdditionalExpandPath)
    ShowDiagnostics = $ShowDiagnostics
}

if ($PSCmdlet.ParameterSetName -eq "OAuth") {
    $commonParameters["ClientId"] = $ClientId
    $commonParameters["ClientSecret"] = $ClientSecret
    $commonParameters["CompanyId"] = $CompanyId
    if (-not [string]::IsNullOrWhiteSpace($TokenUrl)) {
        $commonParameters["TokenUrl"] = $TokenUrl
    }
}
else {
    $commonParameters["Username"] = $Username
    $commonParameters["Password"] = $Password
}

& $scriptPath @commonParameters
