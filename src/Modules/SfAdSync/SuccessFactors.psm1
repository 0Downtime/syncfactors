Set-StrictMode -Version Latest

function Get-SfOAuthToken {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $tokenUri = $Config.successFactors.oauth.tokenUrl
    if (-not $tokenUri) {
        throw "successFactors.oauth.tokenUrl is required."
    }

    $body = @{
        grant_type    = 'client_credentials'
        client_id     = $Config.successFactors.oauth.clientId
        client_secret = $Config.successFactors.oauth.clientSecret
    }

    if ($Config.successFactors.oauth.companyId) {
        $body['company_id'] = $Config.successFactors.oauth.companyId
    }

    $response = Invoke-RestMethod -Uri $tokenUri -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded'
    return $response.access_token
}

function Get-SfAuthHeaders {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

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

    $headers = Get-SfAuthHeaders -Config $Config
    return Invoke-RestMethod -Uri $uriBuilder.Uri.AbsoluteUri -Headers $headers -Method Get
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
    if ($response.d.results) {
        return $response.d.results
    }

    if ($response.value) {
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
    if ($response.d.results -and $response.d.results.Count -gt 0) {
        return $response.d.results[0]
    }

    if ($response.value -and $response.value.Count -gt 0) {
        return $response.value[0]
    }

    return $null
}

Export-ModuleMember -Function Get-SfOAuthToken, Get-SfAuthHeaders, Invoke-SfODataGet, Get-SfWorkers, Get-SfWorkerById
