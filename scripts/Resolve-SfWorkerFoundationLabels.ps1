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
    [string]$Password
)

$ErrorActionPreference = "Stop"

function Get-SuccessFactorsBaseRoot {
    param(
        [string]$Url
    )

    $root = $Url.TrimEnd("/")
    if ($root -match "/odata/v2$") {
        return $root.Substring(0, $root.Length - "/odata/v2".Length)
    }

    return $root
}

function Get-RequestHeaders {
    if ($PSCmdlet.ParameterSetName -eq "OAuth") {
        $resolvedTokenUrl = $TokenUrl
        if ([string]::IsNullOrWhiteSpace($resolvedTokenUrl)) {
            $resolvedTokenUrl = "{0}/oauth/token" -f (Get-SuccessFactorsBaseRoot -Url $BaseUrl)
        }

        $tokenResponse = Invoke-RestMethod `
            -Method Post `
            -Uri $resolvedTokenUrl `
            -ContentType "application/x-www-form-urlencoded" `
            -Body @{
                grant_type = "client_credentials"
                client_id = $ClientId
                client_secret = $ClientSecret
                company_id = $CompanyId
            } `
            -Headers @{
                Accept = "application/json"
            }

        if (-not $tokenResponse.access_token) {
            throw "OAuth response did not include access_token."
        }

        return @{
            Authorization = "Bearer $($tokenResponse.access_token)"
            Accept = "application/json"
        }
    }

    $pair = "{0}:{1}" -f $Username, $Password
    $basicToken = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($pair))
    return @{
        Authorization = "Basic $basicToken"
        Accept = "application/json"
    }
}

function Invoke-SfJsonGet {
    param(
        [string]$Uri,
        [hashtable]$Headers
    )

    return Invoke-RestMethod `
        -Method Get `
        -Uri $Uri `
        -Headers $Headers
}

function Get-FirstResult {
    param(
        $Response
    )

    if ($null -eq $Response) {
        return $null
    }

    if ($Response.PSObject.Properties.Name -contains "d" -and
        $Response.d.PSObject.Properties.Name -contains "results" -and
        $Response.d.results.Count -gt 0) {
        return $Response.d.results[0]
    }

    if ($Response.PSObject.Properties.Name -contains "value" -and $Response.value.Count -gt 0) {
        return $Response.value[0]
    }

    return $null
}

function Get-FirstNavigationResult {
    param(
        $Object,
        [string]$PropertyName
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        return $null
    }

    $value = $property.Value
    if ($null -eq $value) {
        return $null
    }

    if ($value.PSObject.Properties.Name -contains "results" -and $value.results.Count -gt 0) {
        return $value.results[0]
    }

    return $null
}

function Get-StringProperty {
    param(
        $Object,
        [string[]]$PropertyNames
    )

    if ($null -eq $Object) {
        return $null
    }

    foreach ($propertyName in $PropertyNames) {
        $property = $Object.PSObject.Properties[$propertyName]
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return [string]$property.Value
        }
    }

    return $null
}

function Resolve-FoundationObjectLabel {
    param(
        [string]$EntitySet,
        [string]$Code,
        [hashtable]$Headers
    )

    if ([string]::IsNullOrWhiteSpace($Code)) {
        return [PSCustomObject]@{
            Entity = $EntitySet
            Code = $null
            Label = $null
            LabelField = $null
            StartDate = $null
        }
    }

    $candidateFields = @(
        "name",
        "name_defaultValue",
        "name_localized",
        "description",
        "description_localized",
        "company",
        "department",
        "division",
        "businessUnit",
        "LocationName"
    )

    $filter = [Uri]::EscapeDataString("externalCode eq '$($Code.Replace("'", "''"))'")
    $uri = "{0}/{1}?`$format=json&`$filter={2}&`$orderby=startDate desc&`$top=1" -f $BaseUrl.TrimEnd("/"), $EntitySet, $filter
    $response = Invoke-SfJsonGet -Uri $uri -Headers $Headers
    $record = Get-FirstResult -Response $response

    if ($null -eq $record) {
        return [PSCustomObject]@{
            Entity = $EntitySet
            Code = $Code
            Label = $null
            LabelField = $null
            StartDate = $null
        }
    }

    $resolvedField = $null
    $resolvedLabel = $null
    foreach ($field in $candidateFields) {
        $value = Get-StringProperty -Object $record -PropertyNames @($field)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $resolvedField = $field
            $resolvedLabel = $value
            break
        }
    }

    return [PSCustomObject]@{
        Entity = $EntitySet
        Code = $Code
        Label = $resolvedLabel
        LabelField = $resolvedField
        StartDate = Get-StringProperty -Object $record -PropertyNames @("startDate")
    }
}

$headers = Get-RequestHeaders

$workerSelect = @(
    "personIdExternal",
    "employmentNav/startDate",
    "employmentNav/jobInfoNav/department",
    "employmentNav/jobInfoNav/division",
    "employmentNav/jobInfoNav/company",
    "employmentNav/jobInfoNav/location",
    "employmentNav/jobInfoNav/jobTitle",
    "employmentNav/jobInfoNav/employeeType"
)

$workerExpand = @(
    "employmentNav",
    "employmentNav/jobInfoNav"
)

$workerFilter = [Uri]::EscapeDataString("personIdExternal eq '$($PersonIdExternal.Replace("'", "''"))'")
$workerUri = "{0}/PerPerson?`$format=json&`$filter={1}&`$select={2}&`$expand={3}" -f `
    $BaseUrl.TrimEnd("/"), `
    $workerFilter, `
    [Uri]::EscapeDataString(($workerSelect -join ",")), `
    [Uri]::EscapeDataString(($workerExpand -join ","))

$workerResponse = Invoke-SfJsonGet -Uri $workerUri -Headers $headers
$worker = Get-FirstResult -Response $workerResponse
if ($null -eq $worker) {
    throw "No worker returned for personIdExternal '$PersonIdExternal'."
}

$employment = Get-FirstNavigationResult -Object $worker -PropertyName "employmentNav"
$jobInfo = Get-FirstNavigationResult -Object $employment -PropertyName "jobInfoNav"

$departmentCode = Get-StringProperty -Object $jobInfo -PropertyNames @("department")
$divisionCode = Get-StringProperty -Object $jobInfo -PropertyNames @("division")
$companyCode = Get-StringProperty -Object $jobInfo -PropertyNames @("company")
$locationCode = Get-StringProperty -Object $jobInfo -PropertyNames @("location")
$jobTitle = Get-StringProperty -Object $jobInfo -PropertyNames @("jobTitle")
$employeeType = Get-StringProperty -Object $jobInfo -PropertyNames @("employeeType")

$resolved = @(
    Resolve-FoundationObjectLabel -EntitySet "FODepartment" -Code $departmentCode -Headers $headers
    Resolve-FoundationObjectLabel -EntitySet "FODivision" -Code $divisionCode -Headers $headers
    Resolve-FoundationObjectLabel -EntitySet "FOCompany" -Code $companyCode -Headers $headers
    Resolve-FoundationObjectLabel -EntitySet "FOLocation" -Code $locationCode -Headers $headers
)

Write-Host ""
Write-Host "Worker: $PersonIdExternal"
Write-Host ""

$summaryRows = @(
    [PSCustomObject]@{ Attribute = "Department"; Code = $departmentCode; Text = ($resolved | Where-Object Entity -eq "FODepartment").Label; Source = "FODepartment" }
    [PSCustomObject]@{ Attribute = "Division"; Code = $divisionCode; Text = ($resolved | Where-Object Entity -eq "FODivision").Label; Source = "FODivision" }
    [PSCustomObject]@{ Attribute = "Company"; Code = $companyCode; Text = ($resolved | Where-Object Entity -eq "FOCompany").Label; Source = "FOCompany" }
    [PSCustomObject]@{ Attribute = "Location"; Code = $locationCode; Text = ($resolved | Where-Object Entity -eq "FOLocation").Label; Source = "FOLocation" }
    [PSCustomObject]@{ Attribute = "Job Title"; Code = $null; Text = $jobTitle; Source = "EmpJob.jobTitle" }
    [PSCustomObject]@{ Attribute = "Employee Type"; Code = $employeeType; Text = $null; Source = "EmpJob.employeeType" }
)

$summaryRows | Format-Table -AutoSize

Write-Host ""
Write-Host "Foundation object lookup details:"
Write-Host ""

$resolved | Select-Object Entity, Code, Label, LabelField, StartDate | Format-Table -AutoSize
