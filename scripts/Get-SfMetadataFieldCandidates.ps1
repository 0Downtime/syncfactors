param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

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

    [string]$OutputPath = ".\sf-metadata-field-candidates.json",
    [switch]$IncludeRawMetadata
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
    param(
        [string]$BaseUrl,
        [string]$TokenUrl,
        [string]$ClientId,
        [string]$ClientSecret,
        [string]$CompanyId,
        [string]$Username,
        [string]$Password,
        [string]$ParameterSetName
    )

    if ($ParameterSetName -eq "OAuth") {
        $resolvedTokenUrl = $TokenUrl
        if ([string]::IsNullOrWhiteSpace($resolvedTokenUrl)) {
            $resolvedTokenUrl = "{0}/oauth/token" -f (Get-SuccessFactorsBaseRoot -Url $BaseUrl)
        }

        $tokenBody = @{
            grant_type = "client_credentials"
            client_id = $ClientId
            client_secret = $ClientSecret
            company_id = $CompanyId
        }

        $tokenResponse = Invoke-RestMethod `
            -Method Post `
            -Uri $resolvedTokenUrl `
            -ContentType "application/x-www-form-urlencoded" `
            -Body $tokenBody `
            -Headers @{
                Accept = "application/json"
            }

        if (-not $tokenResponse.access_token) {
            throw "OAuth response did not include access_token."
        }

        return @{
            Authorization = "Bearer $($tokenResponse.access_token)"
            Accept = "application/xml"
        }
    }

    $pair = "{0}:{1}" -f $Username, $Password
    $basicToken = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($pair))
    return @{
        Authorization = "Basic $basicToken"
        Accept = "application/xml"
    }
}

function Get-NormalizedBaseUrl {
    param(
        [string]$BaseUrl
    )

    $trimmed = $BaseUrl.TrimEnd("/")
    if ($trimmed -match "/odata/v2$") {
        return $trimmed
    }

    return "{0}/odata/v2" -f $trimmed
}

function Get-KeywordTokens {
    param(
        [string]$Header
    )

    $normalized = ($Header -replace "[^A-Za-z0-9]+", " ").Trim()
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return @()
    }

    $tokens = @($normalized.Split(" ", [System.StringSplitOptions]::RemoveEmptyEntries))
    return $tokens | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique
}

function Get-HeaderSearchPlan {
    $headers = @(
        "Employee ID",
        "Legal First Name",
        "Legal Last Name",
        "Preferred First Name",
        "Employee Status",
        "Job Title",
        "Supervisor",
        "Direct Reports",
        "Company Name",
        "Business Unit Name",
        "People Group Name",
        "Function Name",
        "Sub Function Name",
        "Geozone Name",
        "Location Name",
        "Cost Center Code",
        "Cost Center Name",
        "Employee Type",
        "Employee Class",
        "Leadership Level",
        "Business Email Information Email Address",
        "User/Employee ID",
        "Middle Name",
        "Original Start Date",
        "Most Recent Hire Date",
        "Region",
        "CC Code",
        "Job Code",
        "Position Start Date",
        "Position Entry Date",
        "Position Code",
        "Position Title",
        "People Group",
        "Bargaining Unit",
        "Union Job Code",
        "Manager User Sys ID",
        "Function",
        "Sub Function",
        "HRBP Name",
        "HRBP User Id",
        "Head of Function",
        "Head of Sub Function",
        "Cintas Uniform Allotment",
        "Cintas Uniform Category"
    )

    $overrides = @{
        "Employee ID" = @("personIdExternal", "userId", "employeeid")
        "User/Employee ID" = @("userId", "personIdExternal", "userid")
        "Legal First Name" = @("firstName", "legalfirstname")
        "Preferred First Name" = @("preferredName", "preferredfirstname", "firstname")
        "Middle Name" = @("middleName", "middlename")
        "Legal Last Name" = @("lastName", "legallastname")
        "Employee Status" = @("emplStatus", "status", "employeestatus")
        "Original Start Date" = @("originalStartDate", "startDate", "hireDate")
        "Most Recent Hire Date" = @("startDate", "hireDate", "mostrecenthiredate")
        "Business Unit Name" = @("businessUnit", "businessunit")
        "Region" = @("region", "cust_region")
        "Location Name" = @("location", "locationname")
        "Geozone Name" = @("geozone", "cust_geozone")
        "CC Code" = @("costCenter", "costcentercode", "cccode")
        "Cost Center Code" = @("costCenter", "costcentercode")
        "Cost Center Name" = @("costCenterDescription", "costcentername")
        "Job Code" = @("jobCode", "jobcode")
        "Job Title" = @("jobTitle", "jobtitle")
        "Position Start Date" = @("startDate", "positionstartdate")
        "Position Entry Date" = @("positionEntryDate", "positionentrydate")
        "Position Code" = @("position", "positioncode")
        "Position Title" = @("positionTitle", "positiontitle")
        "People Group" = @("peopleGroup", "peoplegroup")
        "People Group Name" = @("peopleGroup", "peoplegroup")
        "Bargaining Unit" = @("bargainingUnit", "bargainingunit")
        "Union Job Code" = @("unionJobCode", "unioncode", "unionjobcode")
        "Supervisor" = @("managerId", "supervisor", "manager")
        "Manager User Sys ID" = @("managerId", "managersysid")
        "Function" = @("function", "cust_function")
        "Function Name" = @("function", "cust_function")
        "Sub Function" = @("subFunction", "subfunction", "cust_subfunction")
        "Sub Function Name" = @("subFunction", "subfunction", "cust_subfunction")
        "Company Name" = @("company")
        "Employee Type" = @("employeeType", "employmentType")
        "Employee Class" = @("employeeClass")
        "Leadership Level" = @("leadershipLevel", "cust_leadershiplevel")
        "Business Email Information Email Address" = @("emailAddress", "businessemail", "email")
        "HRBP Name" = @("hrbp", "cust_hrbp")
        "HRBP User Id" = @("hrbp", "cust_hrbpuserid", "userid")
        "Head of Function" = @("headoffunction", "cust_headoffunction")
        "Head of Sub Function" = @("headofsubfunction", "cust_headofsubfunction")
        "Cintas Uniform Allotment" = @("cintas", "uniform", "cust_cintasuniformallotment")
        "Cintas Uniform Category" = @("cintas", "uniform", "cust_cintasuniformcategory")
        "Direct Reports" = @("managerId", "directreports")
    }

    return $headers | ForEach-Object {
        $header = $_
        $tokens = Get-KeywordTokens -Header $header
        if ($overrides.ContainsKey($header)) {
            $tokens += $overrides[$header]
        }

        [PSCustomObject]@{
            Header = $header
            Tokens = @($tokens | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
        }
    }
}

function Get-EntityBasePath {
    param(
        [string]$EntityName
    )

    switch -Regex ($EntityName) {
        "^PerPerson$" { return "" }
        "^PerPersonal$" { return "personalInfoNav" }
        "^PerEmail$" { return "emailNav" }
        "^PerPhone$" { return "phoneNav" }
        "^EmpEmployment$" { return "employmentNav" }
        "^EmpJob$" { return "employmentNav/jobInfoNav" }
        "^User$" { return "userNav" }
        "^FOCompany$" { return "employmentNav/jobInfoNav/companyNav" }
        "^FODepartment$" { return "employmentNav/jobInfoNav/departmentNav" }
        "^FOBusinessUnit$" { return "employmentNav/jobInfoNav/businessUnitNav" }
        "^FOCostCenter$" { return "employmentNav/jobInfoNav/costCenterNav" }
        "^FODivision$" { return "employmentNav/jobInfoNav/divisionNav" }
        "^FOLocation$" { return "employmentNav/jobInfoNav/locationNav" }
        default { return $null }
    }
}

function Get-ConfidenceScore {
    param(
        [string]$Header,
        [string]$EntityName,
        [string]$PropertyName,
        [string[]]$Tokens
    )

    $score = 0
    $propertyLower = $PropertyName.ToLowerInvariant()
    $entityLower = $EntityName.ToLowerInvariant()

    foreach ($token in $Tokens) {
        if ($propertyLower -eq $token) {
            $score += 10
        }
        elseif ($propertyLower -like "*$token*") {
            $score += 4
        }

        if ($entityLower -like "*$token*") {
            $score += 2
        }
    }

    if ($Header -like "*Name" -and $propertyLower -like "*name*") {
        $score += 2
    }

    if ($Header -eq "Business Email Information Email Address" -and $propertyLower -eq "emailaddress") {
        $score += 8
    }

    if ($Header -eq "Employee ID" -and ($propertyLower -eq "personidexternal" -or $propertyLower -eq "userid")) {
        $score += 8
    }

    return $score
}

function Get-ConfidenceLabel {
    param(
        [int]$Score
    )

    if ($Score -ge 12) { return "high" }
    if ($Score -ge 7) { return "medium" }
    return "low"
}

function Build-CandidatePath {
    param(
        [string]$EntityName,
        [string]$PropertyName
    )

    $basePath = Get-EntityBasePath -EntityName $EntityName
    if ($null -eq $basePath) {
        return $null
    }

    if ([string]::IsNullOrWhiteSpace($basePath)) {
        return $PropertyName
    }

    return "{0}/{1}" -f $basePath, $PropertyName
}

function Get-EntityMapFromMetadata {
    param(
        [xml]$MetadataXml
    )

    $entities = @{}
    $entityTypes = $MetadataXml.SelectNodes("//*[local-name()='EntityType']")
    foreach ($entityType in $entityTypes) {
        $entityName = $entityType.Name
        if ([string]::IsNullOrWhiteSpace($entityName)) {
            continue
        }

        $properties = @()
        foreach ($property in $entityType.SelectNodes("./*[local-name()='Property']")) {
            $properties += [PSCustomObject]@{
                Name = $property.Name
                Type = $property.Type
            }
        }

        $navProperties = @()
        foreach ($nav in $entityType.SelectNodes("./*[local-name()='NavigationProperty']")) {
            $navProperties += [PSCustomObject]@{
                Name = $nav.Name
                Relationship = $nav.Relationship
                ToRole = $nav.ToRole
                FromRole = $nav.FromRole
            }
        }

        $entities[$entityName] = [PSCustomObject]@{
            Name = $entityName
            Properties = $properties
            NavigationProperties = $navProperties
        }
    }

    return $entities
}

function Get-CandidatesForHeaders {
    param(
        [hashtable]$EntityMap
    )

    $searchPlan = Get-HeaderSearchPlan
    $priorityEntities = @(
        "PerPerson",
        "PerPersonal",
        "PerEmail",
        "PerPhone",
        "EmpEmployment",
        "EmpJob",
        "User",
        "FOCompany",
        "FODepartment",
        "FOBusinessUnit",
        "FOCostCenter",
        "FODivision",
        "FOLocation"
    )

    $orderedEntities = @($priorityEntities + ($EntityMap.Keys | Where-Object { $priorityEntities -notcontains $_ } | Sort-Object))

    $results = @()
    foreach ($item in $searchPlan) {
        $matches = @()
        foreach ($entityName in $orderedEntities) {
            if (-not $EntityMap.ContainsKey($entityName)) {
                continue
            }

            $entity = $EntityMap[$entityName]
            foreach ($property in $entity.Properties) {
                $score = Get-ConfidenceScore -Header $item.Header -EntityName $entityName -PropertyName $property.Name -Tokens $item.Tokens
                if ($score -le 0) {
                    continue
                }

                $candidatePath = Build-CandidatePath -EntityName $entityName -PropertyName $property.Name
                $matches += [PSCustomObject]@{
                    Header = $item.Header
                    Entity = $entityName
                    Property = $property.Name
                    Type = $property.Type
                    CandidatePath = $candidatePath
                    Confidence = Get-ConfidenceLabel -Score $score
                    Score = $score
                    Notes = if ($candidatePath) { "Candidate path inferred from metadata." } else { "Entity found in metadata, but no standard PerPerson path mapping is predefined." }
                }
            }
        }

        $results += [PSCustomObject]@{
            Header = $item.Header
            Matches = @($matches | Sort-Object -Property @{ Expression = "Score"; Descending = $true }, @{ Expression = "Entity"; Descending = $false } | Select-Object -First 12)
        }
    }

    return $results
}

$normalizedBaseUrl = Get-NormalizedBaseUrl -BaseUrl $BaseUrl
$headers = Get-RequestHeaders `
    -BaseUrl $BaseUrl `
    -TokenUrl $TokenUrl `
    -ClientId $ClientId `
    -ClientSecret $ClientSecret `
    -CompanyId $CompanyId `
    -Username $Username `
    -Password $Password `
    -ParameterSetName $PSCmdlet.ParameterSetName

$metadataUri = "{0}/`$metadata" -f $normalizedBaseUrl
$metadataResponse = Invoke-WebRequest `
    -Method Get `
    -Uri $metadataUri `
    -Headers $headers

[xml]$metadataXml = $metadataResponse.Content
$entityMap = Get-EntityMapFromMetadata -MetadataXml $metadataXml
$candidates = Get-CandidatesForHeaders -EntityMap $entityMap

$output = [ordered]@{
    MetadataUri = $metadataUri
    RetrievedAtUtc = [DateTime]::UtcNow.ToString("o")
    EntityCount = $entityMap.Count
    CandidateGroups = $candidates
}

if ($IncludeRawMetadata) {
    $output["RawMetadata"] = $metadataResponse.Content
}

$json = $output | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($OutputPath, $json, [System.Text.Encoding]::UTF8)

Write-Host "Saved metadata field candidates to $OutputPath"
Write-Host "Metadata URI:"
Write-Host $metadataUri
Write-Host "Candidate summary:"
foreach ($group in $candidates) {
    $top = $group.Matches | Select-Object -First 3
    if ($null -eq $top -or $top.Count -eq 0) {
        Write-Host "- $($group.Header): no likely matches found"
        continue
    }

    $summary = $top | ForEach-Object {
        if ([string]::IsNullOrWhiteSpace($_.CandidatePath)) {
            "{0}.{1} ({2})" -f $_.Entity, $_.Property, $_.Confidence
        }
        else {
            "{0} ({1})" -f $_.CandidatePath, $_.Confidence
        }
    }

    Write-Host "- $($group.Header): $($summary -join '; ')"
}
