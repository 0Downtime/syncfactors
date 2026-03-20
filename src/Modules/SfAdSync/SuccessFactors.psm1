Set-StrictMode -Version Latest

function Get-SfAuthMode {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $successFactors = $Config.successFactors
    if ($successFactors.PSObject.Properties.Name -contains 'auth') {
        $auth = $successFactors.auth
        if ($auth -and $auth.PSObject.Properties.Name -contains 'mode' -and -not [string]::IsNullOrWhiteSpace("$($auth.mode)")) {
            return "$($auth.mode)".ToLowerInvariant()
        }
    }

    if ($successFactors.PSObject.Properties.Name -contains 'oauth') {
        return 'oauth'
    }

    if ($successFactors.PSObject.Properties.Name -contains 'auth' -and $successFactors.auth -and $successFactors.auth.PSObject.Properties.Name -contains 'basic') {
        return 'basic'
    }

    return 'basic'
}

function Get-SfBasicAuthHeaderValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $basic = $Config.successFactors.auth.basic
    $credentialBytes = [System.Text.Encoding]::UTF8.GetBytes("$($basic.username):$($basic.password)")
    return 'Basic ' + [Convert]::ToBase64String($credentialBytes)
}

function Get-SfSanitizedText {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string]$Text,
        [AllowNull()]
        [string[]]$Secrets
    )

    if ($null -eq $Text) {
        return $null
    }

    $sanitized = $Text
    foreach ($secret in @($Secrets)) {
        if ([string]::IsNullOrWhiteSpace($secret)) {
            continue
        }

        $escapedSecret = [regex]::Escape($secret)
        $sanitized = [regex]::Replace($sanitized, $escapedSecret, '[REDACTED]')
    }

    return $sanitized
}

function Get-SfExceptionDetails {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Exception]$Exception,
        [AllowNull()]
        [string[]]$Secrets
    )

    $details = New-Object System.Collections.Generic.List[string]
    $details.Add("Exception type: $($Exception.GetType().FullName)")

    $exceptionMessage = Get-SfSanitizedText -Text $Exception.Message -Secrets $Secrets
    if (-not [string]::IsNullOrWhiteSpace($exceptionMessage)) {
        $details.Add("Exception message: $exceptionMessage")
    }

    if ($Exception.InnerException) {
        $innerType = $Exception.InnerException.GetType().FullName
        $innerMessage = Get-SfSanitizedText -Text $Exception.InnerException.Message -Secrets $Secrets
        $details.Add("Inner exception type: $innerType")
        if (-not [string]::IsNullOrWhiteSpace($innerMessage)) {
            $details.Add("Inner exception message: $innerMessage")
        }
    }

    $response = $null
    if ($Exception.PSObject.Properties.Name -contains 'Response') {
        $response = $Exception.Response
    }
    if ($response) {
        $statusCode = $null
        $statusDescription = $null

        if ($response.PSObject.Properties.Name -contains 'StatusCode') {
            $statusCode = [int]$response.StatusCode
            $statusDescription = "$($response.StatusCode)"
        }

        if (-not [string]::IsNullOrWhiteSpace($statusDescription)) {
            $details.Add("HTTP status: $statusDescription")
        } elseif ($null -ne $statusCode) {
            $details.Add("HTTP status code: $statusCode")
        }

        try {
            $responseStream = $response.GetResponseStream()
            if ($responseStream) {
                $reader = [System.IO.StreamReader]::new($responseStream)
                try {
                    $responseBody = $reader.ReadToEnd()
                } finally {
                    $reader.Dispose()
                    $responseStream.Dispose()
                }

                if (-not [string]::IsNullOrWhiteSpace($responseBody)) {
                    $responseBody = Get-SfSanitizedText -Text $responseBody -Secrets $Secrets
                    if ($responseBody.Length -gt 500) {
                        $responseBody = $responseBody.Substring(0, 500)
                    }

                    $details.Add("Response body: $responseBody")
                }
            }
        } catch {
            $details.Add("Response body read failed: $($_.Exception.Message)")
        }
    }

    return $details
}

function New-SfRequestFailure {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Operation,
        [Parameter(Mandatory)]
        [string]$Uri,
        [Parameter(Mandatory)]
        [System.Exception]$Exception,
        [AllowNull()]
        [string[]]$Secrets
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("$Operation failed.")
    $lines.Add("URI: $Uri")

    foreach ($line in Get-SfExceptionDetails -Exception $Exception -Secrets $Secrets) {
        $lines.Add($line)
    }

    return [System.Exception]::new(($lines -join [Environment]::NewLine), $Exception)
}

function Get-SfOAuthToken {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $oauth = if ($Config.successFactors.PSObject.Properties.Name -contains 'auth') { $Config.successFactors.auth.oauth } else { $Config.successFactors.oauth }
    $tokenUri = $oauth.tokenUrl
    if (-not $tokenUri) {
        throw "successFactors.auth.oauth.tokenUrl is required."
    }

    $body = @{
        grant_type    = 'client_credentials'
        client_id     = $oauth.clientId
        client_secret = $oauth.clientSecret
    }

    if ($oauth.companyId) {
        $body['company_id'] = $oauth.companyId
    }

    try {
        $response = Invoke-RestMethod -Uri $tokenUri -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded'
    } catch {
        $secrets = @(
            "$($oauth.clientSecret)"
        )
        throw (New-SfRequestFailure -Operation 'SuccessFactors OAuth token request' -Uri $tokenUri -Exception $_.Exception -Secrets $secrets)
    }

    if (-not $response.access_token) {
        throw "SuccessFactors OAuth token request succeeded but the response did not include access_token. URI: $tokenUri"
    }

    return $response.access_token
}

function Get-SfAuthHeaders {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $authMode = Get-SfAuthMode -Config $Config

    if ($authMode -eq 'basic') {
        return @{
            Authorization = Get-SfBasicAuthHeaderValue -Config $Config
            Accept        = 'application/json'
        }
    }

    $token = Get-SfOAuthToken -Config $Config
    return @{
        Authorization = "Bearer $token"
        Accept        = 'application/json'
    }
}

function Invoke-SfODataGet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [string]$RelativePath,
        [hashtable]$Query
    )

    $baseUrl = $Config.successFactors.baseUrl.TrimEnd('/')
    $uriBuilder = [System.UriBuilder]::new("$baseUrl/$RelativePath")

    if ($Query) {
        $pairs = foreach ($key in $Query.Keys) {
            $encodedKey = [System.Uri]::EscapeDataString($key)
            $encodedValue = [System.Uri]::EscapeDataString("$($Query[$key])")
            "$encodedKey=$encodedValue"
        }

        $uriBuilder.Query = ($pairs -join '&')
    }

    $requestUri = $uriBuilder.Uri.AbsoluteUri
    $headers = Get-SfAuthHeaders -Config $Config

    try {
        return Invoke-RestMethod -Uri $requestUri -Headers $headers -Method Get
    } catch {
        $authMode = Get-SfAuthMode -Config $Config
        $oauth = if ($Config.successFactors.PSObject.Properties.Name -contains 'auth') { $Config.successFactors.auth.oauth } else { $Config.successFactors.oauth }
        $secrets = @(
            $(if ($authMode -eq 'basic') { "$($Config.successFactors.auth.basic.password)" } else { "$($oauth.clientSecret)" }),
            "$($headers.Authorization)"
        )
        throw (New-SfRequestFailure -Operation 'SuccessFactors OData request' -Uri $requestUri -Exception $_.Exception -Secrets $secrets)
    }
}

function Get-SfWorkers {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [ValidateSet('Delta','Full')]
        [string]$Mode,
        [string]$Checkpoint
    )

    $workerQuery = @{
        '$select' = ($Config.successFactors.query.select -join ',')
        '$expand' = ($Config.successFactors.query.expand -join ',')
    }

    if ($Mode -eq 'Delta' -and $Checkpoint) {
        $workerQuery['$filter'] = "$($Config.successFactors.query.deltaField) ge datetime'$Checkpoint'"
    } elseif ($Config.successFactors.query.baseFilter) {
        $workerQuery['$filter'] = $Config.successFactors.query.baseFilter
    }

    $response = Invoke-SfODataGet -Config $Config -RelativePath $Config.successFactors.query.entitySet -Query $workerQuery
    if ($response.PSObject.Properties.Name -contains 'd' -and $response.d -and $response.d.results) {
        return $response.d.results
    }

    if ($response.PSObject.Properties.Name -contains 'value' -and $response.value) {
        return $response.value
    }

    return @()
}

function Get-SfWorkerById {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [string]$WorkerId
    )

    $query = @{
        '$select' = ($Config.successFactors.query.select -join ',')
        '$expand' = ($Config.successFactors.query.expand -join ',')
        '$filter' = "$($Config.successFactors.query.identityField) eq '$WorkerId'"
    }

    $response = Invoke-SfODataGet -Config $Config -RelativePath $Config.successFactors.query.entitySet -Query $query
    if ($response.PSObject.Properties.Name -contains 'd' -and $response.d -and $response.d.results -and $response.d.results.Count -gt 0) {
        return $response.d.results[0]
    }

    if ($response.PSObject.Properties.Name -contains 'value' -and $response.value -and $response.value.Count -gt 0) {
        return $response.value[0]
    }

    return $null
}

Export-ModuleMember -Function Get-SfOAuthToken, Get-SfAuthHeaders, Invoke-SfODataGet, Get-SfWorkers, Get-SfWorkerById
