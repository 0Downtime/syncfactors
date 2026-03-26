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
    [string[]]$ExcludeSelectPath = @(),
    [string[]]$ExcludeExpandPath = @(),
    [string[]]$AdditionalSelectPath = @(),
    [string[]]$AdditionalExpandPath = @(),
    [switch]$IncludeHeaderProfile,
    [switch]$SkipSanitization,
    [switch]$AliasOrgValues,
    [switch]$KeepPersonIdExternal,
    [switch]$ShowDiagnostics
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
        $Object,

        [Parameter(Mandatory = $true)]
        [string]$PropertyName,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($null -eq $Object -or $Object.GetType().Name -ne "JsonObject") {
        return
    }

    if ($Object.ContainsKey($PropertyName)) {
        $Object[$PropertyName] = $Value
    }
}

function Remove-PropertyIfPresent {
    param(
        [Parameter(Mandatory = $true)]
        $Object,

        [Parameter(Mandatory = $true)]
        [string]$PropertyName
    )

    if ($null -eq $Object -or $Object.GetType().Name -ne "JsonObject") {
        return
    }

    if ($Object.ContainsKey($PropertyName)) {
        [void]$Object.Remove($PropertyName)
    }
}

function Get-FirstNavigationObject {
    param(
        $Object,
        [string]$NavigationName
    )

    if ($null -eq $Object -or $Object.GetType().Name -ne "JsonObject" -or -not $Object.ContainsKey($NavigationName)) {
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

function Sanitize-NodeRecursively {
    param(
        [Parameter(Mandatory = $true)]
        $Node,

        [Parameter(Mandatory = $true)]
        [hashtable]$ReplacementMap,

        [switch]$AliasOrgValues
    )

    if ($null -eq $Node) {
        return
    }

    if ($Node.GetType().Name -eq "JsonArray") {
        $array = [System.Text.Json.Nodes.JsonArray]$Node
        for ($i = 0; $i -lt $array.Count; $i++) {
            Sanitize-NodeRecursively -Node $array[$i] -ReplacementMap $ReplacementMap -AliasOrgValues:$AliasOrgValues
        }
        return
    }

    if ($Node.GetType().Name -ne "JsonObject") {
        return
    }

    $object = [System.Text.Json.Nodes.JsonObject]$Node

    Remove-PropertyIfPresent -Object $object -PropertyName "__metadata"
    Remove-PropertyIfPresent -Object $object -PropertyName "__deferred"

    if ($ReplacementMap["personIdExternal"]) {
        Set-StringPropertyIfPresent -Object $object -PropertyName "personIdExternal" -Value $ReplacementMap["personIdExternal"]
    }
    Set-StringPropertyIfPresent -Object $object -PropertyName "firstName" -Value $ReplacementMap["firstName"]
    Set-StringPropertyIfPresent -Object $object -PropertyName "lastName" -Value $ReplacementMap["lastName"]
    Set-StringPropertyIfPresent -Object $object -PropertyName "username" -Value $ReplacementMap["username"]
    Set-StringPropertyIfPresent -Object $object -PropertyName "userName" -Value $ReplacementMap["userName"]
    Set-StringPropertyIfPresent -Object $object -PropertyName "email" -Value $ReplacementMap["email"]
    Set-StringPropertyIfPresent -Object $object -PropertyName "emailAddress" -Value $ReplacementMap["emailAddress"]
    Set-StringPropertyIfPresent -Object $object -PropertyName "managerId" -Value $ReplacementMap["managerId"]
    Set-StringPropertyIfPresent -Object $object -PropertyName "officeLocationAddress" -Value $ReplacementMap["officeLocationAddress"]
    Set-StringPropertyIfPresent -Object $object -PropertyName "officeLocationCity" -Value $ReplacementMap["officeLocationCity"]
    Set-StringPropertyIfPresent -Object $object -PropertyName "officeLocationZipCode" -Value $ReplacementMap["officeLocationZipCode"]

    $currentJobTitle = [string]$object["jobTitle"]
    if (-not [string]::IsNullOrWhiteSpace($currentJobTitle)) {
        Set-StringPropertyIfPresent -Object $object -PropertyName "jobTitle" -Value (Get-AliasValue -Prefix "Job" -Value $currentJobTitle)
    }

    foreach ($propertyName in @(
        "phone",
        "phoneNumber",
        "businessPhone",
        "cellPhone",
        "mobilePhone"
    )) {
        if ($ReplacementMap.ContainsKey($propertyName)) {
            Set-StringPropertyIfPresent -Object $object -PropertyName $propertyName -Value $ReplacementMap[$propertyName]
        }
    }

    if ($AliasOrgValues) {
        foreach ($orgProp in @(
            @{ Property = "department"; Prefix = "Department" },
            @{ Property = "company"; Prefix = "Company" },
            @{ Property = "businessUnit"; Prefix = "BusinessUnit" },
            @{ Property = "division"; Prefix = "Division" },
            @{ Property = "costCenter"; Prefix = "CostCenter" },
            @{ Property = "costCenterDescription"; Prefix = "CostCenter" },
            @{ Property = "location"; Prefix = "Location" },
            @{ Property = "LocationName"; Prefix = "Location" }
        )) {
            $currentValue = [string]$object[$orgProp.Property]
            if (-not [string]::IsNullOrWhiteSpace($currentValue)) {
                Set-StringPropertyIfPresent -Object $object -PropertyName $orgProp.Property -Value (Get-AliasValue -Prefix $orgProp.Prefix -Value $currentValue)
            }
        }
    }

    $childPropertyNames = @()
    foreach ($entry in $object) {
        $childPropertyNames += $entry.Key
    }

    foreach ($childProperty in $childPropertyNames) {
        $child = $object[$childProperty]
        if ($null -ne $child) {
            Sanitize-NodeRecursively -Node $child -ReplacementMap $ReplacementMap -AliasOrgValues:$AliasOrgValues
        }
    }
}

function Sanitize-Worker {
    param(
        [Parameter(Mandatory = $true)]
        $Worker,

        [switch]$AliasOrgValues,
        [switch]$KeepPersonIdExternal
    )

    if ($null -eq $Worker -or $Worker.GetType().Name -ne "JsonObject") {
        return
    }

    $originalPersonId = [string]$Worker["personIdExternal"]
    $personNumber = Get-StableNumber -Input $originalPersonId -Min 10000 -Max 99999
    $sanitizedPersonId = "mock-{0:D5}" -f $personNumber
    $sanitizedUserName = "user.{0:D5}" -f $personNumber
    $sanitizedFirstName = "Worker{0:D3}" -f (Get-StableNumber -Input "$originalPersonId`:fn" -Min 10 -Max 999)
    $sanitizedLastName = "Sample{0:D3}" -f (Get-StableNumber -Input "$originalPersonId`:ln" -Min 10 -Max 999)
    $sanitizedEmail = "{0}@example.test" -f $sanitizedUserName

    $replacementMap = @{}
    $replacementMap["personIdExternal"] = if ($KeepPersonIdExternal) { $null } else { $sanitizedPersonId }
    $replacementMap["firstName"] = $sanitizedFirstName
    $replacementMap["lastName"] = $sanitizedLastName
    $replacementMap["username"] = $sanitizedUserName
    $replacementMap["userName"] = $sanitizedUserName
    $replacementMap["email"] = $sanitizedEmail
    $replacementMap["emailAddress"] = $sanitizedEmail
    $replacementMap["managerId"] = ("mgr-{0:D5}" -f (Get-StableNumber -Input "$originalPersonId`:mgr" -Min 10000 -Max 99999))
    $replacementMap["officeLocationAddress"] = ("Suite {0} Example Way" -f (Get-StableNumber -Input "$originalPersonId`:addr" -Min 100 -Max 999))
    $replacementMap["officeLocationCity"] = ("City{0:D2}" -f (Get-StableNumber -Input "$originalPersonId`:city" -Min 10 -Max 99))
    $replacementMap["officeLocationZipCode"] = ("{0:D5}" -f (Get-StableNumber -Input "$originalPersonId`:zip" -Min 10000 -Max 99999))

    foreach ($phoneProp in @("phone", "phoneNumber", "businessPhone", "cellPhone", "mobilePhone")) {
        $replacementMap[$phoneProp] = ("555-{0:D3}-{1:D4}" -f (Get-StableNumber -Input "$originalPersonId`:$phoneProp:a" -Min 100 -Max 999), (Get-StableNumber -Input "$originalPersonId`:$phoneProp:b" -Min 1000 -Max 9999))
    }

    Sanitize-NodeRecursively -Node $Worker -ReplacementMap $replacementMap -AliasOrgValues:$AliasOrgValues
}

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

function Get-UniquePaths {
    param(
        [string[]]$Values
    )

    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $ordered = @()
    foreach ($value in $Values) {
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        if ($seen.Add($value)) {
            $ordered += $value
        }
    }

    return $ordered
}

function Get-ExpandPathForPropertyPath {
    param(
        [string]$PropertyPath
    )

    if ([string]::IsNullOrWhiteSpace($PropertyPath)) {
        return $null
    }

    $segments = $PropertyPath.Split("/")
    if ($segments.Length -le 1) {
        return $null
    }

    return ($segments[0..($segments.Length - 2)] -join "/")
}

function Resolve-QueryPathFromInvalidProperty {
    param(
        [string]$InvalidPropertyPath,
        [string[]]$CurrentSelectValues
    )

    if ([string]::IsNullOrWhiteSpace($InvalidPropertyPath)) {
        return $null
    }

    $directMatch = $CurrentSelectValues | Where-Object { $_ -eq $InvalidPropertyPath } | Select-Object -First 1
    if (-not [string]::IsNullOrWhiteSpace($directMatch)) {
        return $directMatch
    }

    $parts = $InvalidPropertyPath.Split("/")
    $propertyName = $parts[-1]
    $entityName = if ($parts.Length -gt 1) { $parts[0] } else { "" }

    $entityToNav = @{
        "FOBusinessUnit" = "businessUnitNav"
        "FOCostCenter" = "costCenterNav"
        "FODivision" = "divisionNav"
        "FODepartment" = "departmentNav"
        "FOCompany" = "companyNav"
        "FOLocation" = "locationNav"
    }

    if ($entityToNav.ContainsKey($entityName)) {
        $navName = $entityToNav[$entityName]
        $mapped = $CurrentSelectValues |
            Where-Object { $_ -like "*$navName/$propertyName" } |
            Select-Object -First 1
        if (-not [string]::IsNullOrWhiteSpace($mapped)) {
            return $mapped
        }
    }

    $fallback = $CurrentSelectValues |
        Where-Object { $_ -like "*/$propertyName" } |
        Select-Object -First 1
    return $fallback
}

function Get-InvalidPropertyPathFromErrorJson {
    param(
        [string]$ErrorJson
    )

    if ([string]::IsNullOrWhiteSpace($ErrorJson)) {
        return $null
    }

    try {
        $parsed = $ErrorJson | ConvertFrom-Json -ErrorAction Stop
        $code = $parsed.error.code
        $message = [string]$parsed.error.message.value
        if ($code -ne "COE_PROPERTY_NOT_FOUND") {
            return $null
        }

        $match = [regex]::Match($message, "Invalid property names:\s+([A-Za-z0-9_/]+)")
        if (-not $match.Success) {
            return $null
        }

        return $match.Groups[1].Value
    }
    catch {
        return $null
    }
}

function Get-ResponseBodyFromErrorRecord {
    param(
        [Parameter(Mandatory = $true)]
        $ErrorRecord
    )

    $exception = $ErrorRecord.Exception
    $responseBody = $null

    if ($ErrorRecord.ErrorDetails -and -not [string]::IsNullOrWhiteSpace($ErrorRecord.ErrorDetails.Message)) {
        $responseBody = $ErrorRecord.ErrorDetails.Message
    }

    if ($exception.PSObject.Properties.Name -contains "Response" -and $null -ne $exception.Response) {
        try {
            if ($exception.Response.PSObject.Properties.Name -contains "Content" -and $null -ne $exception.Response.Content) {
                $responseBody = $exception.Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
            elseif ($exception.Response.PSObject.Methods.Name -contains "GetResponseStream") {
                $stream = $exception.Response.GetResponseStream()
                if ($null -ne $stream) {
                    $reader = New-Object System.IO.StreamReader($stream)
                    try {
                        $responseBody = $reader.ReadToEnd()
                    }
                    finally {
                        $reader.Dispose()
                        $stream.Dispose()
                    }
                }
            }
        }
        catch {
            if ([string]::IsNullOrWhiteSpace($responseBody)) {
                $responseBody = $null
            }
        }
    }

    return $responseBody
}

function Get-StatusCodeFromErrorRecord {
    param(
        [Parameter(Mandatory = $true)]
        $ErrorRecord
    )

    $exception = $ErrorRecord.Exception
    if ($exception.PSObject.Properties.Name -contains "Response" -and $null -ne $exception.Response) {
        $response = $exception.Response

        if ($response.PSObject.Properties.Name -contains "StatusCode" -and $null -ne $response.StatusCode) {
            return [int]$response.StatusCode
        }

        if ($response.PSObject.Properties.Name -contains "Status" -and $null -ne $response.Status) {
            return [int]$response.Status
        }
    }

    return $null
}

function Throw-DetailedRequestFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MessagePrefix,

        [Parameter(Mandatory = $true)]
        [string]$RequestUri,

        [Parameter(Mandatory = $true)]
        [string[]]$SelectValues,

        [Parameter(Mandatory = $true)]
        [string[]]$ExpandValues,

        [Parameter(Mandatory = $true)]
        $ErrorRecord
    )

    $statusCode = Get-StatusCodeFromErrorRecord -ErrorRecord $ErrorRecord
    $responseBody = Get-ResponseBodyFromErrorRecord -ErrorRecord $ErrorRecord

    if ($ShowDiagnostics) {
        Write-Host "$MessagePrefix"
        if ($null -ne $statusCode) {
            Write-Host "HTTP status: $statusCode"
        }
        Write-Host "Request URI:"
        Write-Host $RequestUri
        Write-Host "Select paths ($($SelectValues.Count)):"
        Write-Host ($SelectValues -join ", ")
        Write-Host "Expand paths ($($ExpandValues.Count)):"
        Write-Host ($ExpandValues -join ", ")
        if (-not [string]::IsNullOrWhiteSpace($responseBody)) {
            Write-Host "Response body:"
            Write-Host $responseBody
        }
    }

    $details = @($MessagePrefix)
    if ($null -ne $statusCode) {
        $details += "HTTP status: $statusCode"
    }
    $details += "Request URI: $RequestUri"
    $details += "Select paths ($($SelectValues.Count)): $($SelectValues -join ', ')"
    $details += "Expand paths ($($ExpandValues.Count)): $($ExpandValues -join ', ')"
    if (-not [string]::IsNullOrWhiteSpace($responseBody)) {
        $details += "Response body: $responseBody"
    }

    throw ($details -join [Environment]::NewLine)
}

function Invoke-PerPersonRequestWithAutoRetry {
    param(
        [string]$BaseUrl,
        [string]$Filter,
        [string[]]$InitialSelectValues,
        [string[]]$InitialExpandValues,
        [hashtable]$Headers
    )

    $selectValues = @($InitialSelectValues)
    $expandValues = @($InitialExpandValues)
    $attempt = 0

    while ($true) {
        $attempt += 1

        $queryParams = @{
            '$format' = 'json'
            '$filter' = $Filter
            '$select' = ($selectValues -join ",")
            '$expand' = ($expandValues -join ",")
        }

        $queryString = ($queryParams.GetEnumerator() | ForEach-Object {
            "{0}={1}" -f [System.Uri]::EscapeDataString($_.Key), [System.Uri]::EscapeDataString([string]$_.Value)
        }) -join "&"

        $requestUri = "{0}/PerPerson?{1}" -f $BaseUrl.TrimEnd("/"), $queryString

        try {
            $response = Invoke-WebRequest `
                -Method Get `
                -Uri $requestUri `
                -Headers $Headers

            return @{
                Response = $response
                RequestUri = $requestUri
                SelectValues = $selectValues
                ExpandValues = $expandValues
            }
        }
        catch {
            $responseBody = Get-ResponseBodyFromErrorRecord -ErrorRecord $_

            $invalidPropertyPath = Get-InvalidPropertyPathFromErrorJson -ErrorJson $responseBody
            if ([string]::IsNullOrWhiteSpace($invalidPropertyPath)) {
                Throw-DetailedRequestFailure `
                    -MessagePrefix "SuccessFactors PerPerson request failed." `
                    -RequestUri $requestUri `
                    -SelectValues $selectValues `
                    -ExpandValues $expandValues `
                    -ErrorRecord $_
            }

            $queryPropertyPath = Resolve-QueryPathFromInvalidProperty -InvalidPropertyPath $invalidPropertyPath -CurrentSelectValues $selectValues
            if ([string]::IsNullOrWhiteSpace($queryPropertyPath)) {
                Throw-DetailedRequestFailure `
                    -MessagePrefix "SuccessFactors rejected a property path and the script could not map it back to the outgoing query." `
                    -RequestUri $requestUri `
                    -SelectValues $selectValues `
                    -ExpandValues $expandValues `
                    -ErrorRecord $_
            }

            $removed = $false
            $newSelectValues = @($selectValues | Where-Object { $_ -ne $queryPropertyPath })
            if ($newSelectValues.Count -ne $selectValues.Count) {
                $selectValues = $newSelectValues
                $removed = $true
            }

            $expandPath = Get-ExpandPathForPropertyPath -PropertyPath $queryPropertyPath
            if (-not [string]::IsNullOrWhiteSpace($expandPath)) {
                $newExpandValues = @($expandValues | Where-Object { $_ -ne $expandPath })
                if ($newExpandValues.Count -ne $expandValues.Count) {
                    $expandValues = $newExpandValues
                    $removed = $true
                }
            }

            if (-not $removed) {
                Throw-DetailedRequestFailure `
                    -MessagePrefix "SuccessFactors rejected a property path, but the script could not remove it from the outgoing query." `
                    -RequestUri $requestUri `
                    -SelectValues $selectValues `
                    -ExpandValues $expandValues `
                    -ErrorRecord $_
            }

            Write-Host "Attempt $attempt failed because tenant metadata does not expose '$invalidPropertyPath'."
            Write-Host "Retrying after removing:"
            Write-Host "- select: $queryPropertyPath"
            if (-not [string]::IsNullOrWhiteSpace($expandPath)) {
                Write-Host "- expand: $expandPath"
            }
        }
    }
}

if ($PSCmdlet.ParameterSetName -eq "OAuth" -and [string]::IsNullOrWhiteSpace($TokenUrl)) {
    $root = $BaseUrl.TrimEnd("/")
    if ($root -match "/odata/v2$") {
        $root = $root.Substring(0, $root.Length - "/odata/v2".Length)
    }
    $TokenUrl = "$root/oauth/token"
}

$selectValues = @(
    "personIdExternal",
    "userId",
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
    "employmentNav/jobInfoNav/locationNav"
)

if ($IncludeHeaderProfile) {
    $selectValues += @(
        "personalInfoNav/middleName",
        "employmentNav/originalStartDate",
        "employmentNav/jobInfoNav/emplStatus",
        "employmentNav/jobInfoNav/jobCode",
        "employmentNav/jobInfoNav/position",
        "employmentNav/jobInfoNav/positionEntryDate",
        "employmentNav/jobInfoNav/positionTitle",
        "employmentNav/jobInfoNav/bargainingUnit",
        "employmentNav/jobInfoNav/unionCode",
        "employmentNav/jobInfoNav/company",
        "employmentNav/jobInfoNav/businessUnit",
        "employmentNav/jobInfoNav/location",
        "employmentNav/jobInfoNav/costCenter",
        "employmentNav/jobInfoNav/department",
        "employmentNav/jobInfoNav/division",
        "employmentNav/jobInfoNav/customString1",
        "employmentNav/jobInfoNav/customString2",
        "employmentNav/jobInfoNav/customString3",
        "employmentNav/jobInfoNav/customString4",
        "employmentNav/jobInfoNav/customString5",
        "employmentNav/jobInfoNav/customString6",
        "employmentNav/jobInfoNav/customString7",
        "employmentNav/jobInfoNav/customString8",
        "employmentNav/jobInfoNav/customString9",
        "employmentNav/jobInfoNav/customString10",
        "employmentNav/jobInfoNav/customString11",
        "employmentNav/jobInfoNav/customString12",
        "employmentNav/jobInfoNav/customString13",
        "employmentNav/jobInfoNav/customString14",
        "employmentNav/jobInfoNav/customString15"
    )
}

$selectValues += $AdditionalSelectPath
$expandValues += $AdditionalExpandPath

$selectValues = Get-UniquePaths -Values $selectValues
$expandValues = Get-UniquePaths -Values $expandValues

$selectValues = Remove-ExcludedPaths -Values $selectValues -Excluded $ExcludeSelectPath
$expandValues = Remove-ExcludedPaths -Values $expandValues -Excluded $ExcludeExpandPath

$escapedWorkerId = $PersonIdExternal.Replace("'", "''")
$filter = "personIdExternal eq '$escapedWorkerId'"

if ($PSCmdlet.ParameterSetName -eq "OAuth") {
    $tokenBody = @{
        grant_type    = "client_credentials"
        client_id     = $ClientId
        client_secret = $ClientSecret
        company_id    = $CompanyId
    }

    try {
        $tokenResponse = Invoke-RestMethod `
            -Method Post `
            -Uri $TokenUrl `
            -ContentType "application/x-www-form-urlencoded" `
            -Body $tokenBody `
            -Headers @{
                Accept = "application/json"
            }
    }
    catch {
        $statusCode = Get-StatusCodeFromErrorRecord -ErrorRecord $_
        $responseBody = Get-ResponseBodyFromErrorRecord -ErrorRecord $_

        if ($ShowDiagnostics) {
            Write-Host "SuccessFactors OAuth token request failed."
            if ($null -ne $statusCode) {
                Write-Host "HTTP status: $statusCode"
            }
            Write-Host "Token URL:"
            Write-Host $TokenUrl
            if (-not [string]::IsNullOrWhiteSpace($responseBody)) {
                Write-Host "Response body:"
                Write-Host $responseBody
            }
        }

        $message = @("SuccessFactors OAuth token request failed.")
        if ($null -ne $statusCode) {
            $message += "HTTP status: $statusCode"
        }
        $message += "Token URL: $TokenUrl"
        if (-not [string]::IsNullOrWhiteSpace($responseBody)) {
            $message += "Response body: $responseBody"
        }

        throw ($message -join [Environment]::NewLine)
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

$requestResult = Invoke-PerPersonRequestWithAutoRetry `
    -BaseUrl $BaseUrl `
    -Filter $filter `
    -InitialSelectValues $selectValues `
    -InitialExpandValues $expandValues `
    -Headers $headers

$response = $requestResult.Response
$requestUri = $requestResult.RequestUri

$document = [System.Text.Json.Nodes.JsonNode]::Parse($response.Content)
if ($null -eq $document) {
    throw "Received empty JSON response."
}

$results = $document["d"]["results"]
if ($null -eq $results -or $results.GetType().Name -ne "JsonArray") {
    throw "Expected SuccessFactors-style JSON at d.results."
}

$resultsArray = [System.Text.Json.Nodes.JsonArray]$results
if (-not $SkipSanitization) {
    for ($index = 0; $index -lt $resultsArray.Count; $index++) {
        $item = $resultsArray[$index]
        if ($null -ne $item -and $item.GetType().Name -eq "JsonObject") {
            Sanitize-Worker -Worker $item -AliasOrgValues:$AliasOrgValues -KeepPersonIdExternal:$KeepPersonIdExternal
        }
    }
}

$jsonOptions = [System.Text.Json.JsonSerializerOptions]::new()
$jsonOptions.WriteIndented = $true
$sanitizedJson = $document.ToJsonString($jsonOptions)
[System.IO.File]::WriteAllText($OutputPath, $sanitizedJson, [System.Text.Encoding]::UTF8)

Write-Host "Saved sanitized export to $OutputPath"
Write-Host "Query URI:"
Write-Host $requestUri
Write-Host "Final select paths ($($requestResult.SelectValues.Count)):"
Write-Host ($requestResult.SelectValues -join ", ")
Write-Host "Final expand paths ($($requestResult.ExpandValues.Count)):"
Write-Host ($requestResult.ExpandValues -join ", ")

if ($SkipSanitization) {
    Write-Host "Sanitization skipped."
}
else {
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
}
