[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$OutputPath,
    [bool]$IncludeDisabledAdUsers = $true,
    [ValidateRange(1, 5000)]
    [int]$PageSize
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.DirectoryServices.Protocols

function Get-RepoRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

function Get-FullPathFromRepoRoot {
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$RepoRoot
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

function Get-NormalizedString {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    $text = [string]$Value
    $trimmed = $text.Trim()
    return [string]::IsNullOrWhiteSpace($trimmed) ? $null : $trimmed
}

function Get-ScalarString {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Collections.IDictionary]) {
        if ($Value.Contains('value')) {
            return Get-ScalarString -Value $Value['value']
        }

        return $null
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = @($Value)
        if ($items.Count -eq 0) {
            return $null
        }

        return Get-ScalarString -Value $items[0]
    }

    return Get-NormalizedString -Value $Value
}

function Get-ObjectMemberValue {
    param(
        $Object,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    if ($Object -is [System.Collections.IDictionary]) {
        return $Object.Contains($Name) ? $Object[$Name] : $null
    }

    $property = $Object.PSObject.Properties[$Name]
    return $null -ne $property ? $property.Value : $null
}

function Get-RecordPropertyValue {
    param(
        $Record,
        [Parameter(Mandatory)]
        [string]$Name
    )

    return Get-ObjectMemberValue -Object $Record -Name $Name
}

function Get-ValueByPath {
    param(
        $Object,
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $current = $Object
    foreach ($segment in ($Path -split '/' | Where-Object { $_.Length -gt 0 })) {
        if ($null -eq $current) {
            return $null
        }

        if ($current -is [System.Collections.IEnumerable] -and $current -isnot [string]) {
            $items = @($current)
            if ($items.Count -eq 0) {
                return $null
            }

            $current = $items[0]
        }

        $current = Get-ObjectMemberValue -Object $current -Name $segment
        $results = Get-ObjectMemberValue -Object $current -Name 'results'
        if ($null -ne $results -and $results -is [System.Collections.IEnumerable] -and $results -isnot [string]) {
            $items = @($results)
            $current = $items.Count -gt 0 ? $items[0] : $null
        }
    }

    return Get-ScalarString -Value $current
}

function Get-RequiredHashtableValue {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Table,
        [Parameter(Mandatory)]
        [string]$Key,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if (-not $Table.Contains($Key)) {
        throw "Missing required config value '$Label'."
    }

    return $Table[$Key]
}

function Get-OptionalHashtableValue {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Table,
        [Parameter(Mandatory)]
        [string]$Key
    )

    return $Table.Contains($Key) ? $Table[$Key] : $null
}

function Get-RequiredSecretValue {
    param(
        [AllowNull()]
        [string]$EnvironmentVariableName,
        [AllowNull()]
        [string]$FallbackValue,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if (-not [string]::IsNullOrWhiteSpace($EnvironmentVariableName)) {
        $environmentValue = [Environment]::GetEnvironmentVariable($EnvironmentVariableName)
        if (-not [string]::IsNullOrWhiteSpace($environmentValue)) {
            return $environmentValue
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($FallbackValue)) {
        return $FallbackValue
    }

    if (-not [string]::IsNullOrWhiteSpace($EnvironmentVariableName)) {
        throw "Environment variable '$EnvironmentVariableName' was not set for $Label."
    }

    throw "Missing required secret value for $Label."
}

function Get-OptionalSecretValue {
    param(
        [AllowNull()]
        [string]$EnvironmentVariableName,
        [AllowNull()]
        [string]$FallbackValue
    )

    if (-not [string]::IsNullOrWhiteSpace($EnvironmentVariableName)) {
        $environmentValue = [Environment]::GetEnvironmentVariable($EnvironmentVariableName)
        if (-not [string]::IsNullOrWhiteSpace($environmentValue)) {
            return $environmentValue
        }
    }

    return $FallbackValue
}

function Resolve-ConfigPath {
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$ConfigPath,
        [Parameter(Mandatory)]
        [string]$RepoRoot
    )

    $loadWorktreeEnvPath = Join-Path $RepoRoot 'scripts/codex/Load-WorktreeEnv.ps1'
    if (-not (Test-Path $loadWorktreeEnvPath)) {
        throw "Required script not found: $loadWorktreeEnvPath"
    }

    . $loadWorktreeEnvPath

    if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
        $resolvedPath = Get-FullPathFromRepoRoot -Path $ConfigPath -RepoRoot $RepoRoot
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS)) {
        $resolvedPath = $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS
    }
    else {
        throw 'SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS was not set after loading the worktree environment.'
    }

    if (-not (Test-Path $resolvedPath)) {
        throw "Sync config path was not found: $resolvedPath"
    }

    return $resolvedPath
}

function Read-SyncRuntimeConfig {
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath
    )

    $config = Get-Content -Path $ConfigPath -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $secrets = Get-RequiredHashtableValue -Table $config -Key 'secrets' -Label 'secrets'
    $successFactors = Get-RequiredHashtableValue -Table $config -Key 'successFactors' -Label 'successFactors'
    $sfAuth = Get-RequiredHashtableValue -Table $successFactors -Key 'auth' -Label 'successFactors.auth'
    $sfQuery = Get-RequiredHashtableValue -Table $successFactors -Key 'query' -Label 'successFactors.query'
    $ad = Get-RequiredHashtableValue -Table $config -Key 'ad' -Label 'ad'
    $reporting = Get-RequiredHashtableValue -Table $config -Key 'reporting' -Label 'reporting'

    $sfMode = Get-NormalizedString -Value (Get-RequiredHashtableValue -Table $sfAuth -Key 'mode' -Label 'successFactors.auth.mode')
    $sfMode = $sfMode.ToLowerInvariant()

    $basicAuth = Get-OptionalHashtableValue -Table $sfAuth -Key 'basic'
    $oauthAuth = Get-OptionalHashtableValue -Table $sfAuth -Key 'oauth'
    $transport = Get-OptionalHashtableValue -Table $ad -Key 'transport'
    if ($null -eq $transport) {
        $transport = @{
            mode = 'ldaps'
            allowLdapFallback = $false
            requireCertificateValidation = $true
            requireSigning = $true
            trustedCertificateThumbprints = @()
        }
    }

    $trustedThumbprints = @()
    $configuredThumbprints = Get-OptionalHashtableValue -Table $transport -Key 'trustedCertificateThumbprints'
    if ($configuredThumbprints -is [System.Collections.IEnumerable] -and $configuredThumbprints -isnot [string]) {
        $trustedThumbprints = @($configuredThumbprints | ForEach-Object { Get-NormalizedString -Value $_ } | Where-Object { $null -ne $_ })
    }

    if ($sfMode -eq 'basic') {
        if ($null -eq $basicAuth) {
            throw "SuccessFactors auth mode is 'basic', but successFactors.auth.basic was not configured."
        }

        $sfAuthConfig = [pscustomobject]@{
            Mode = 'basic'
            Username = Get-RequiredSecretValue -EnvironmentVariableName (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $secrets -Key 'successFactorsUsernameEnv')) -FallbackValue (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $basicAuth -Key 'username')) -Label 'SuccessFactors basic username'
            Password = Get-RequiredSecretValue -EnvironmentVariableName (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $secrets -Key 'successFactorsPasswordEnv')) -FallbackValue (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $basicAuth -Key 'password')) -Label 'SuccessFactors basic password'
        }
    }
    elseif ($sfMode -eq 'oauth') {
        if ($null -eq $oauthAuth) {
            throw "SuccessFactors auth mode is 'oauth', but successFactors.auth.oauth was not configured."
        }

        $sfAuthConfig = [pscustomobject]@{
            Mode = 'oauth'
            TokenUrl = Get-NormalizedString -Value (Get-RequiredHashtableValue -Table $oauthAuth -Key 'tokenUrl' -Label 'successFactors.auth.oauth.tokenUrl')
            ClientId = Get-RequiredSecretValue -EnvironmentVariableName (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $secrets -Key 'successFactorsClientIdEnv')) -FallbackValue (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $oauthAuth -Key 'clientId')) -Label 'SuccessFactors OAuth client ID'
            ClientSecret = Get-RequiredSecretValue -EnvironmentVariableName (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $secrets -Key 'successFactorsClientSecretEnv')) -FallbackValue (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $oauthAuth -Key 'clientSecret')) -Label 'SuccessFactors OAuth client secret'
            CompanyId = Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $oauthAuth -Key 'companyId')
        }
    }
    else {
        throw "Unsupported SuccessFactors auth mode '$sfMode'."
    }

    $sfQueryPageSize = if ($sfQuery.Contains('pageSize')) { [int]$sfQuery['pageSize'] } else { 200 }
    $adPort = if ($ad.Contains('port') -and $null -ne $ad['port']) { [int]$ad['port'] } else { $null }
    $transportMode = Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $transport -Key 'mode')
    if ([string]::IsNullOrWhiteSpace($transportMode)) {
        $transportMode = 'ldaps'
    }

    $allowLdapFallback = if ($transport.Contains('allowLdapFallback')) { [bool]$transport['allowLdapFallback'] } else { $false }
    $requireCertificateValidation = if ($transport.Contains('requireCertificateValidation')) { [bool]$transport['requireCertificateValidation'] } else { $true }
    $requireSigning = if ($transport.Contains('requireSigning')) { [bool]$transport['requireSigning'] } else { $true }

    $runtimeConfig = [pscustomobject]@{
        ConfigPath = $ConfigPath
        SuccessFactors = [pscustomobject]@{
            BaseUrl = Get-NormalizedString -Value (Get-RequiredHashtableValue -Table $successFactors -Key 'baseUrl' -Label 'successFactors.baseUrl')
            Auth = $sfAuthConfig
            Query = [pscustomobject]@{
                EntitySet = Get-NormalizedString -Value (Get-RequiredHashtableValue -Table $sfQuery -Key 'entitySet' -Label 'successFactors.query.entitySet')
                IdentityField = Get-NormalizedString -Value (Get-RequiredHashtableValue -Table $sfQuery -Key 'identityField' -Label 'successFactors.query.identityField')
                BaseFilter = Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $sfQuery -Key 'baseFilter')
                OrderBy = Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $sfQuery -Key 'orderBy')
                AsOfDate = Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $sfQuery -Key 'asOfDate')
                PageSize = $sfQueryPageSize
                Select = @($sfQuery['select'])
                Expand = @($sfQuery['expand'])
            }
        }
        ActiveDirectory = [pscustomobject]@{
            Server = Get-RequiredSecretValue -EnvironmentVariableName (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $secrets -Key 'adServerEnv')) -FallbackValue (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $ad -Key 'server')) -Label 'AD server'
            Port = $adPort
            Username = Get-OptionalSecretValue -EnvironmentVariableName (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $secrets -Key 'adUsernameEnv')) -FallbackValue (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $ad -Key 'username'))
            BindPassword = Get-OptionalSecretValue -EnvironmentVariableName (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $secrets -Key 'adBindPasswordEnv')) -FallbackValue (Get-NormalizedString -Value (Get-OptionalHashtableValue -Table $ad -Key 'bindPassword'))
            Transport = [pscustomobject]@{
                Mode = $transportMode
                AllowLdapFallback = $allowLdapFallback
                RequireCertificateValidation = $requireCertificateValidation
                RequireSigning = $requireSigning
                TrustedCertificateThumbprints = $trustedThumbprints
            }
        }
        Reporting = [pscustomobject]@{
            OutputDirectory = Get-NormalizedString -Value (Get-RequiredHashtableValue -Table $reporting -Key 'outputDirectory' -Label 'reporting.outputDirectory')
        }
    }

    return $runtimeConfig
}

function Get-UniqueValues {
    param(
        [Parameter(Mandatory)]
        [string[]]$Values
    )

    $set = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($value in $Values) {
        $normalizedValue = Get-NormalizedString -Value $value
        if ($null -ne $normalizedValue) {
            [void]$set.Add($normalizedValue)
        }
    }

    return @($set)
}

function Get-SfSelectValues {
    param(
        [Parameter(Mandatory)]
        $Query
    )

    $values = @($Query.Select)
    $values += $Query.IdentityField
    $values += 'emplStatus'
    if (-not [string]::Equals($Query.IdentityField, 'userId', [System.StringComparison]::OrdinalIgnoreCase)) {
        $values += 'userId'
    }

    return Get-UniqueValues -Values @($values)
}

function Get-SfExpandValues {
    param(
        [Parameter(Mandatory)]
        $Query
    )

    return Get-UniqueValues -Values @($Query.Expand)
}

function New-QueryString {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Parameters
    )

    $pairs = foreach ($entry in $Parameters.GetEnumerator() | Sort-Object Key) {
        if ($null -eq $entry.Value -or [string]::IsNullOrWhiteSpace([string]$entry.Value)) {
            continue
        }

        '{0}={1}' -f [System.Uri]::EscapeDataString([string]$entry.Key), [System.Uri]::EscapeDataString([string]$entry.Value)
    }

    return ($pairs -join '&')
}

function Resolve-AbsoluteUri {
    param(
        [Parameter(Mandatory)]
        [string]$BaseUri,
        [Parameter(Mandatory)]
        [string]$Reference
    )

    $absoluteUri = $null
    if ([System.Uri]::TryCreate($Reference, [System.UriKind]::Absolute, [ref]$absoluteUri)) {
        return $absoluteUri.AbsoluteUri
    }

    return ([System.Uri]::new([System.Uri]$BaseUri, $Reference)).AbsoluteUri
}

function Get-SfOAuthHeaders {
    param(
        [Parameter(Mandatory)]
        $Auth
    )

    $body = @{
        grant_type = 'client_credentials'
        client_id = $Auth.ClientId
        client_secret = $Auth.ClientSecret
    }

    if (-not [string]::IsNullOrWhiteSpace($Auth.CompanyId)) {
        $body['company_id'] = $Auth.CompanyId
    }

    $response = Invoke-WebRequest `
        -Method Post `
        -Uri $Auth.TokenUrl `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body $body `
        -Headers @{ Accept = 'application/json' } `
        -SkipHttpErrorCheck

    $statusCode = [int]$response.StatusCode
    if ($statusCode -lt 200 -or $statusCode -ge 300) {
        throw "SuccessFactors OAuth token request failed. Status=$statusCode TokenUrl=$($Auth.TokenUrl) Body=$($response.Content)"
    }

    $payload = $response.Content | ConvertFrom-Json -AsHashtable -Depth 100
    $accessToken = Get-NormalizedString -Value $payload['access_token']
    if ([string]::IsNullOrWhiteSpace($accessToken)) {
        throw "SuccessFactors OAuth token request succeeded, but access_token was missing. TokenUrl=$($Auth.TokenUrl)"
    }

    return @{
        Authorization = "Bearer $accessToken"
        Accept = 'application/json'
        'x-correlation-id' = [guid]::NewGuid().ToString()
        'X-SF-Correlation-Id' = [guid]::NewGuid().ToString()
        'X-SF-Process-Name' = 'SyncFactors.Export.SfAdDelta'
        'X-SF-Execution-Id' = [guid]::NewGuid().ToString()
    }
}

function Get-SfBasicHeaders {
    param(
        [Parameter(Mandatory)]
        $Auth
    )

    $pair = '{0}:{1}' -f $Auth.Username, $Auth.Password
    $token = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($pair))

    return @{
        Authorization = "Basic $token"
        Accept = 'application/json'
        'x-correlation-id' = [guid]::NewGuid().ToString()
        'X-SF-Correlation-Id' = [guid]::NewGuid().ToString()
        'X-SF-Process-Name' = 'SyncFactors.Export.SfAdDelta'
        'X-SF-Execution-Id' = [guid]::NewGuid().ToString()
    }
}

function Get-SfHeaders {
    param(
        [Parameter(Mandatory)]
        $Auth
    )

    return $Auth.Mode -eq 'oauth'
        ? (Get-SfOAuthHeaders -Auth $Auth)
        : (Get-SfBasicHeaders -Auth $Auth)
}

function Invoke-SfRequest {
    param(
        [Parameter(Mandatory)]
        [string]$Uri,
        [Parameter(Mandatory)]
        [hashtable]$Headers
    )

    $response = Invoke-WebRequest -Method Get -Uri $Uri -Headers $Headers -SkipHttpErrorCheck
    $statusCode = [int]$response.StatusCode

    return [pscustomobject]@{
        RequestUri = $Uri
        StatusCode = $statusCode
        Body = $response.Content
    }
}

function Get-SfWorkersFromPayload {
    param($Payload)

    $legacy = Get-ObjectMemberValue -Object $Payload -Name 'd'
    if ($null -ne $legacy) {
        $results = Get-ObjectMemberValue -Object $legacy -Name 'results'
        if ($null -ne $results) {
            return @($results)
        }
    }

    $modern = Get-ObjectMemberValue -Object $Payload -Name 'value'
    if ($null -ne $modern) {
        return @($modern)
    }

    return @()
}

function Get-SfNextLink {
    param(
        $Payload,
        [Parameter(Mandatory)]
        [string]$RequestUri
    )

    $legacy = Get-ObjectMemberValue -Object $Payload -Name 'd'
    if ($null -ne $legacy) {
        $nextLink = Get-NormalizedString -Value (Get-ObjectMemberValue -Object $legacy -Name '__next')
        if ($null -ne $nextLink) {
            return Resolve-AbsoluteUri -BaseUri $RequestUri -Reference $nextLink
        }
    }

    foreach ($propertyName in @('@odata.nextLink', 'odata.nextLink')) {
        $nextLink = Get-NormalizedString -Value (Get-ObjectMemberValue -Object $Payload -Name $propertyName)
        if ($null -ne $nextLink) {
            return Resolve-AbsoluteUri -BaseUri $RequestUri -Reference $nextLink
        }
    }

    return $null
}

function New-SfWorkerRecord {
    param(
        [Parameter(Mandatory)]
        $Worker,
        [Parameter(Mandatory)]
        $Query
    )

    $identityValue = Get-NormalizedString -Value (Get-ValueByPath -Object $Worker -Path $Query.IdentityField)
    $userId = Get-NormalizedString -Value (Get-ValueByPath -Object $Worker -Path 'userId')
    $emplStatus = Get-NormalizedString -Value (Get-ValueByPath -Object $Worker -Path 'emplStatus')

    return [pscustomobject]@{
        Anchor = $identityValue
        SfEmployeeId = $identityValue
        SfUserId = $userId
        SfEmplStatus = $emplStatus
        Raw = $Worker
    }
}

function Get-SfRequestUri {
    param(
        [Parameter(Mandatory)]
        [string]$BaseUrl,
        [Parameter(Mandatory)]
        $Query,
        [Parameter(Mandatory)]
        [int]$PageSize,
        [int]$Skip = 0,
        [switch]$UseLegacyPaging
    )

    $parameters = @{
        '$format' = 'json'
        '$select' = ((Get-SfSelectValues -Query $Query) -join ',')
    }

    if ($UseLegacyPaging) {
        $parameters['$top'] = $PageSize
        $parameters['$skip'] = $Skip
    }
    else {
        $parameters['customPageSize'] = $PageSize
        $parameters['paging'] = 'snapshot'
    }

    $expandValues = Get-SfExpandValues -Query $Query
    if ($expandValues.Count -gt 0) {
        $parameters['$expand'] = ($expandValues -join ',')
    }

    if (-not [string]::IsNullOrWhiteSpace($Query.BaseFilter)) {
        $parameters['$filter'] = $Query.BaseFilter
    }

    if (-not [string]::IsNullOrWhiteSpace($Query.OrderBy)) {
        $parameters['$orderby'] = $Query.OrderBy
    }

    if (-not [string]::IsNullOrWhiteSpace($Query.AsOfDate)) {
        $parameters['asOfDate'] = $Query.AsOfDate
    }

    $queryString = New-QueryString -Parameters $parameters
    return '{0}/{1}?{2}' -f $BaseUrl.TrimEnd('/'), $Query.EntitySet, $queryString
}

function Get-SuccessFactorsWorkers {
    param(
        [Parameter(Mandatory)]
        $RuntimeConfig,
        [Parameter(Mandatory)]
        [int]$EffectivePageSize
    )

    $headers = Get-SfHeaders -Auth $RuntimeConfig.SuccessFactors.Auth
    $query = $RuntimeConfig.SuccessFactors.Query
    $allWorkers = [System.Collections.Generic.List[object]]::new()

    $serverPagedUri = Get-SfRequestUri -BaseUrl $RuntimeConfig.SuccessFactors.BaseUrl -Query $query -PageSize $EffectivePageSize
    $serverPagedResponse = Invoke-SfRequest -Uri $serverPagedUri -Headers $headers

    if ($serverPagedResponse.StatusCode -eq 400) {
        Write-Warning "SuccessFactors rejected server-side pagination parameters. Falling back to legacy offset paging."
        $skip = 0
        while ($true) {
            $legacyUri = Get-SfRequestUri -BaseUrl $RuntimeConfig.SuccessFactors.BaseUrl -Query $query -PageSize $EffectivePageSize -Skip $skip -UseLegacyPaging
            $response = Invoke-SfRequest -Uri $legacyUri -Headers $headers
            if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
                throw "SuccessFactors request failed. Status=$($response.StatusCode) Uri=$legacyUri Body=$($response.Body)"
            }

            $payload = $response.Body | ConvertFrom-Json -AsHashtable -Depth 100
            $workers = @(Get-SfWorkersFromPayload -Payload $payload)
            foreach ($worker in $workers) {
                $allWorkers.Add((New-SfWorkerRecord -Worker $worker -Query $query))
            }

            if ($workers.Count -lt $EffectivePageSize) {
                break
            }

            $skip += $EffectivePageSize
        }

        return @($allWorkers)
    }

    if ($serverPagedResponse.StatusCode -lt 200 -or $serverPagedResponse.StatusCode -ge 300) {
        throw "SuccessFactors request failed. Status=$($serverPagedResponse.StatusCode) Uri=$serverPagedUri Body=$($serverPagedResponse.Body)"
    }

    $nextUri = $serverPagedUri
    $currentResponse = $serverPagedResponse
    while ($true) {
        $payload = $currentResponse.Body | ConvertFrom-Json -AsHashtable -Depth 100
        $workers = @(Get-SfWorkersFromPayload -Payload $payload)
        foreach ($worker in $workers) {
            $allWorkers.Add((New-SfWorkerRecord -Worker $worker -Query $query))
        }

        $nextUri = Get-SfNextLink -Payload $payload -RequestUri $currentResponse.RequestUri
        if ([string]::IsNullOrWhiteSpace($nextUri)) {
            break
        }

        $currentResponse = Invoke-SfRequest -Uri $nextUri -Headers $headers
        if ($currentResponse.StatusCode -lt 200 -or $currentResponse.StatusCode -ge 300) {
            throw "SuccessFactors request failed. Status=$($currentResponse.StatusCode) Uri=$nextUri Body=$($currentResponse.Body)"
        }
    }

    return @($allWorkers)
}

function Normalize-Thumbprint {
    param([AllowNull()][string]$Thumbprint)

    if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        return ''
    }

    return $Thumbprint.Replace(':', '').Replace(' ', '').Trim().ToUpperInvariant()
}

function Test-LdapServerCertificate {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate]$Certificate,
        [Parameter(Mandatory)]
        $Transport
    )

    if ($null -eq $Certificate) {
        return $false
    }

    if (-not $Transport.RequireCertificateValidation) {
        return $true
    }

    $certificate2 = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($Certificate)
    try {
        $thumbprints = @($Transport.TrustedCertificateThumbprints | ForEach-Object { Normalize-Thumbprint -Thumbprint $_ } | Where-Object { $_.Length -gt 0 })
        if ($thumbprints.Count -gt 0) {
            return $thumbprints -contains (Normalize-Thumbprint -Thumbprint $certificate2.Thumbprint)
        }

        $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
        try {
            $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
            $chain.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
            return $chain.Build($certificate2)
        }
        finally {
            $chain.Dispose()
        }
    }
    finally {
        $certificate2.Dispose()
    }
}

function Get-LdapPortForMode {
    param(
        [AllowNull()]
        [int]$ConfiguredPort,
        [Parameter(Mandatory)]
        [string]$RequestedMode,
        [Parameter(Mandatory)]
        [string]$ConfiguredMode
    )

    if ($null -eq $ConfiguredPort) {
        return (Get-DefaultLdapPort -Mode $RequestedMode)
    }

    if ($RequestedMode -eq 'ldap' -and $ConfiguredPort -eq (Get-DefaultLdapPort -Mode $ConfiguredMode)) {
        return (Get-DefaultLdapPort -Mode 'ldap')
    }

    return $ConfiguredPort
}

function Get-DefaultLdapPort {
    param(
        [Parameter(Mandatory)]
        [string]$Mode
    )

    return $Mode -eq 'ldaps' ? 636 : 389
}

function New-LdapConnectionForMode {
    param(
        [Parameter(Mandatory)]
        $ActiveDirectoryConfig,
        [Parameter(Mandatory)]
        [string]$Mode
    )

    $requestedMode = $Mode.ToLowerInvariant()
    $configuredMode = $ActiveDirectoryConfig.Transport.Mode.ToLowerInvariant()
    $port = Get-LdapPortForMode -ConfiguredPort $ActiveDirectoryConfig.Port -RequestedMode $requestedMode -ConfiguredMode $configuredMode
    $identifier = [System.DirectoryServices.Protocols.LdapDirectoryIdentifier]::new($ActiveDirectoryConfig.Server, $port)
    $connection = [System.DirectoryServices.Protocols.LdapConnection]::new($identifier)
    $connection.Timeout = [TimeSpan]::FromSeconds(15)
    $connection.AuthType = if ([string]::IsNullOrWhiteSpace($ActiveDirectoryConfig.Username)) {
        [System.DirectoryServices.Protocols.AuthType]::Anonymous
    }
    else {
        [System.DirectoryServices.Protocols.AuthType]::Basic
    }

    if (-not [string]::IsNullOrWhiteSpace($ActiveDirectoryConfig.Username)) {
        $connection.Credential = [System.Net.NetworkCredential]::new($ActiveDirectoryConfig.Username, $ActiveDirectoryConfig.BindPassword)
    }

    $connection.SessionOptions.ProtocolVersion = 3
    $connection.SessionOptions.ReferralChasing = [System.DirectoryServices.Protocols.ReferralChasingOptions]::None
    if ($ActiveDirectoryConfig.Transport.RequireSigning) {
        $connection.SessionOptions.Signing = $true
        $connection.SessionOptions.Sealing = $true
    }

    if ($requestedMode -ne 'ldap') {
        $transport = $ActiveDirectoryConfig.Transport
        $callbackScriptBlock = {
            param($ldapConnection, $certificate)
            return (Test-LdapServerCertificate -Certificate $certificate -Transport $transport)
        }.GetNewClosure()
        $callback = [System.DirectoryServices.Protocols.VerifyServerCertificateCallback]$callbackScriptBlock
        $connection.SessionOptions.add_VerifyServerCertificate($callback)
    }

    if ($requestedMode -eq 'ldaps') {
        $connection.SessionOptions.SecureSocketLayer = $true
    }
    elseif ($requestedMode -eq 'starttls') {
        $connection.SessionOptions.StartTransportLayerSecurity($null)
    }

    $connection.Bind()
    return $connection
}

function New-LdapConnection {
    param(
        [Parameter(Mandatory)]
        $ActiveDirectoryConfig
    )

    $primaryMode = $ActiveDirectoryConfig.Transport.Mode.ToLowerInvariant()
    try {
        return New-LdapConnectionForMode -ActiveDirectoryConfig $ActiveDirectoryConfig -Mode $primaryMode
    }
    catch [System.DirectoryServices.Protocols.LdapException] {
        if (-not $ActiveDirectoryConfig.Transport.AllowLdapFallback -or $primaryMode -eq 'ldap') {
            throw
        }

        Write-Warning "AD bind failed over $primaryMode. Retrying with plain LDAP."
        return New-LdapConnectionForMode -ActiveDirectoryConfig $ActiveDirectoryConfig -Mode 'ldap'
    }
}

function Get-LdapAttributeValue {
    param(
        [Parameter(Mandatory)]
        [System.DirectoryServices.Protocols.SearchResultEntry]$Entry,
        [Parameter(Mandatory)]
        [string]$AttributeName
    )

    if (-not $Entry.Attributes.Contains($AttributeName)) {
        return $null
    }

    $values = $Entry.Attributes[$AttributeName]
    if ($null -eq $values -or $values.Count -eq 0) {
        return $null
    }

    return Get-NormalizedString -Value $values[0]
}

function Get-DefaultNamingContext {
    param(
        [Parameter(Mandatory)]
        [System.DirectoryServices.Protocols.LdapConnection]$Connection,
        [Parameter(Mandatory)]
        [string]$Server
    )

    $request = [System.DirectoryServices.Protocols.SearchRequest]::new(
        '',
        '(objectClass=*)',
        [System.DirectoryServices.Protocols.SearchScope]::Base,
        @('defaultNamingContext'))

    $response = [System.DirectoryServices.Protocols.SearchResponse]$Connection.SendRequest($request)
    if ($response.Entries.Count -eq 0) {
        throw "AD rootDSE query returned no entries while resolving defaultNamingContext from server '$Server'."
    }

    $searchEntry = [System.DirectoryServices.Protocols.SearchResultEntry]$response.Entries[0]
    $namingContext = Get-LdapAttributeValue -Entry $searchEntry -AttributeName 'defaultNamingContext'
    if ([string]::IsNullOrWhiteSpace($namingContext)) {
        throw "AD rootDSE query did not return defaultNamingContext from server '$Server'."
    }

    return $namingContext
}

function Get-AdUserEnabledState {
    param([AllowNull()][string]$UserAccountControl)

    if ([string]::IsNullOrWhiteSpace($UserAccountControl)) {
        return $null
    }

    $controlValue = 0
    if (-not [int]::TryParse($UserAccountControl, [ref]$controlValue)) {
        return $null
    }

    return (($controlValue -band 0x0002) -eq 0)
}

function New-AdUserRecord {
    param(
        [Parameter(Mandatory)]
        [System.DirectoryServices.Protocols.SearchResultEntry]$Entry
    )

    $samAccountName = Get-LdapAttributeValue -Entry $Entry -AttributeName 'sAMAccountName'
    $userAccountControl = Get-LdapAttributeValue -Entry $Entry -AttributeName 'userAccountControl'

    return [pscustomobject]@{
        Anchor = $samAccountName
        AdSamAccountName = $samAccountName
        AdEmployeeId = Get-LdapAttributeValue -Entry $Entry -AttributeName 'employeeID'
        AdDisplayName = Get-LdapAttributeValue -Entry $Entry -AttributeName 'displayName'
        AdEnabled = Get-AdUserEnabledState -UserAccountControl $userAccountControl
        AdDistinguishedName = Get-LdapAttributeValue -Entry $Entry -AttributeName 'distinguishedName'
        AdMail = Get-LdapAttributeValue -Entry $Entry -AttributeName 'mail'
        Raw = $Entry
    }
}

function Get-ActiveDirectoryUsers {
    param(
        [Parameter(Mandatory)]
        $RuntimeConfig,
        [bool]$IncludeDisabledAdUsers = $true
    )

    $attributes = @(
        'sAMAccountName',
        'employeeID',
        'displayName',
        'distinguishedName',
        'mail',
        'userPrincipalName',
        'department',
        'company',
        'userAccountControl'
    )

    $connection = $null
    $searchBase = $null
    try {
        $connection = New-LdapConnection -ActiveDirectoryConfig $RuntimeConfig.ActiveDirectory
        $searchBase = Get-DefaultNamingContext -Connection $connection -Server $RuntimeConfig.ActiveDirectory.Server
        $allUsers = [System.Collections.Generic.List[object]]::new()
        $cookie = [byte[]]::new(0)

        do {
            $request = [System.DirectoryServices.Protocols.SearchRequest]::new(
                $searchBase,
                '(&(objectCategory=person)(objectClass=user))',
                [System.DirectoryServices.Protocols.SearchScope]::Subtree,
                $attributes)

            $pageControl = [System.DirectoryServices.Protocols.PageResultRequestControl]::new(1000)
            if ($cookie.Length -gt 0) {
                $pageControl.Cookie = $cookie
            }

            [void]$request.Controls.Add($pageControl)
            $response = [System.DirectoryServices.Protocols.SearchResponse]$connection.SendRequest($request)

            foreach ($entry in $response.Entries) {
                $record = New-AdUserRecord -Entry ([System.DirectoryServices.Protocols.SearchResultEntry]$entry)
                if (-not $IncludeDisabledAdUsers -and $record.AdEnabled -eq $false) {
                    continue
                }

                $allUsers.Add($record)
            }

            $pageResponse = $null
            foreach ($control in $response.Controls) {
                if ($control -is [System.DirectoryServices.Protocols.PageResultResponseControl]) {
                    $pageResponse = $control
                    break
                }
            }

            $cookie = if ($null -ne $pageResponse -and $null -ne $pageResponse.Cookie) { $pageResponse.Cookie } else { [byte[]]::new(0) }
        }
        while ($cookie.Length -gt 0)

        return @($allUsers)
    }
    catch [System.DirectoryServices.Protocols.DirectoryOperationException] {
        throw "AD query failed against search base '$searchBase' on server '$($RuntimeConfig.ActiveDirectory.Server)'. $($_.Exception.Message)"
    }
    catch [System.DirectoryServices.Protocols.LdapException] {
        throw "AD query failed on server '$($RuntimeConfig.ActiveDirectory.Server)'. $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $connection) {
            $connection.Dispose()
        }
    }
}

function New-AnchorIndex {
    param(
        [Parameter(Mandatory)]
        [object[]]$Records
    )

    $index = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[object]]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $blankAnchorRecords = [System.Collections.Generic.List[object]]::new()

    foreach ($record in $Records) {
        $anchor = Get-NormalizedString -Value $record.Anchor
        if ([string]::IsNullOrWhiteSpace($anchor)) {
            $blankAnchorRecords.Add($record)
            continue
        }

        if (-not $index.ContainsKey($anchor)) {
            $index[$anchor] = [System.Collections.Generic.List[object]]::new()
        }

        $index[$anchor].Add($record)
    }

    return [pscustomobject]@{
        Anchors = $index
        BlankAnchors = @($blankAnchorRecords)
    }
}

function New-DiffRow {
    param(
        [AllowNull()]
        [string]$Anchor,
        [Parameter(Mandatory)]
        [string]$Status,
        [bool]$SfPresent,
        [bool]$AdPresent,
        $SfRecord,
        $AdRecord,
        [AllowNull()]
        [string]$Notes
    )

    return [pscustomobject]@{
        anchor = $Anchor
        status = $Status
        sfPresent = $SfPresent
        adPresent = $AdPresent
        sfEmployeeId = if ($null -ne $SfRecord) { Get-RecordPropertyValue -Record $SfRecord -Name 'SfEmployeeId' } else { $null }
        sfUserId = if ($null -ne $SfRecord) { Get-RecordPropertyValue -Record $SfRecord -Name 'SfUserId' } else { $null }
        sfEmplStatus = if ($null -ne $SfRecord) { Get-RecordPropertyValue -Record $SfRecord -Name 'SfEmplStatus' } else { $null }
        adSamAccountName = if ($null -ne $AdRecord) { Get-RecordPropertyValue -Record $AdRecord -Name 'AdSamAccountName' } else { $null }
        adEmployeeId = if ($null -ne $AdRecord) { Get-RecordPropertyValue -Record $AdRecord -Name 'AdEmployeeId' } else { $null }
        adDisplayName = if ($null -ne $AdRecord) { Get-RecordPropertyValue -Record $AdRecord -Name 'AdDisplayName' } else { $null }
        adEnabled = if ($null -ne $AdRecord) { Get-RecordPropertyValue -Record $AdRecord -Name 'AdEnabled' } else { $null }
        adDistinguishedName = if ($null -ne $AdRecord) { Get-RecordPropertyValue -Record $AdRecord -Name 'AdDistinguishedName' } else { $null }
        adMail = if ($null -ne $AdRecord) { Get-RecordPropertyValue -Record $AdRecord -Name 'AdMail' } else { $null }
        notes = $Notes
    }
}

function Get-DiffRowsForAnchor {
    param(
        [Parameter(Mandatory)]
        [string]$Anchor,
        [AllowNull()]
        [object[]]$SfRecords,
        [AllowNull()]
        [object[]]$AdRecords
    )

    $sfItems = @($SfRecords | Where-Object { $null -ne $_ })
    $adItems = @($AdRecords | Where-Object { $null -ne $_ })
    $rows = [System.Collections.Generic.List[object]]::new()

    if ($sfItems.Count -gt 1) {
        $notes = "Multiple SuccessFactors records share the anchor '$Anchor'. Count=$($sfItems.Count)"
        $matchingAdRecord = if ($adItems.Count -eq 1) { $adItems[0] } else { $null }
        foreach ($sfRecord in $sfItems) {
            $rows.Add((New-DiffRow -Anchor $Anchor -Status 'DuplicateSFAnchor' -SfPresent $true -AdPresent ($adItems.Count -gt 0) -SfRecord $sfRecord -AdRecord $matchingAdRecord -Notes $notes))
        }
    }

    if ($adItems.Count -gt 1) {
        $notes = "Multiple AD records share the anchor '$Anchor'. Count=$($adItems.Count)"
        $matchingSfRecord = if ($sfItems.Count -eq 1) { $sfItems[0] } else { $null }
        foreach ($adRecord in $adItems) {
            $rows.Add((New-DiffRow -Anchor $Anchor -Status 'DuplicateADAnchor' -SfPresent ($sfItems.Count -gt 0) -AdPresent $true -SfRecord $matchingSfRecord -AdRecord $adRecord -Notes $notes))
        }
    }

    if ($rows.Count -gt 0) {
        return @($rows)
    }

    if ($sfItems.Count -eq 1 -and $adItems.Count -eq 1) {
        $sfRecord = $sfItems[0]
        $adRecord = $adItems[0]
        $adEmployeeId = Get-NormalizedString -Value (Get-RecordPropertyValue -Record $adRecord -Name 'AdEmployeeId')
        if (-not [string]::IsNullOrWhiteSpace($adEmployeeId) -and -not [string]::Equals($adEmployeeId, $Anchor, [System.StringComparison]::OrdinalIgnoreCase)) {
            return @(
                (New-DiffRow -Anchor $Anchor -Status 'AnchorMismatch' -SfPresent $true -AdPresent $true -SfRecord $sfRecord -AdRecord $adRecord -Notes "AD employeeID '$adEmployeeId' does not match the SF anchor '$Anchor'.")
            )
        }

        return @(
            (New-DiffRow -Anchor $Anchor -Status 'Match' -SfPresent $true -AdPresent $true -SfRecord $sfRecord -AdRecord $adRecord -Notes $null)
        )
    }

    if ($sfItems.Count -eq 1) {
        return @(
            (New-DiffRow -Anchor $Anchor -Status 'OnlyInSF' -SfPresent $true -AdPresent $false -SfRecord $sfItems[0] -AdRecord $null -Notes 'No AD user matched the SF anchor by sAMAccountName.')
        )
    }

    if ($adItems.Count -eq 1) {
        return @(
            (New-DiffRow -Anchor $Anchor -Status 'OnlyInAD' -SfPresent $false -AdPresent $true -SfRecord $null -AdRecord $adItems[0] -Notes 'No SuccessFactors user matched the AD sAMAccountName.')
        )
    }

    return @()
}

function Get-SfAdDeltaRows {
    param(
        [Parameter(Mandatory)]
        [object[]]$SfRecords,
        [Parameter(Mandatory)]
        [object[]]$AdRecords
    )

    $sfIndex = New-AnchorIndex -Records $SfRecords
    $adIndex = New-AnchorIndex -Records $AdRecords
    $rows = [System.Collections.Generic.List[object]]::new()

    foreach ($record in $sfIndex.BlankAnchors) {
        $rows.Add((New-DiffRow -Anchor $null -Status 'OnlyInSF' -SfPresent $true -AdPresent $false -SfRecord $record -AdRecord $null -Notes 'SuccessFactors record did not contain a usable anchor value.'))
    }

    foreach ($record in $adIndex.BlankAnchors) {
        $rows.Add((New-DiffRow -Anchor $null -Status 'OnlyInAD' -SfPresent $false -AdPresent $true -SfRecord $null -AdRecord $record -Notes 'AD record did not contain a usable sAMAccountName value.'))
    }

    $allAnchors = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($anchor in $sfIndex.Anchors.Keys) {
        [void]$allAnchors.Add($anchor)
    }

    foreach ($anchor in $adIndex.Anchors.Keys) {
        [void]$allAnchors.Add($anchor)
    }

    foreach ($anchor in ($allAnchors | Sort-Object)) {
        $sfItems = if ($sfIndex.Anchors.ContainsKey($anchor)) { @($sfIndex.Anchors[$anchor]) } else { @() }
        $adItems = if ($adIndex.Anchors.ContainsKey($anchor)) { @($adIndex.Anchors[$anchor]) } else { @() }
        foreach ($row in Get-DiffRowsForAnchor -Anchor $anchor -SfRecords $sfItems -AdRecords $adItems) {
            $rows.Add($row)
        }
    }

    return @($rows | Sort-Object status, anchor, sfEmployeeId, adSamAccountName)
}

function Resolve-OutputPath {
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$OutputPath,
        [Parameter(Mandatory)]
        $RuntimeConfig,
        [Parameter(Mandatory)]
        [string]$RepoRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        return Get-FullPathFromRepoRoot -Path $OutputPath -RepoRoot $RepoRoot
    }

    $outputDirectory = Get-FullPathFromRepoRoot -Path $RuntimeConfig.Reporting.OutputDirectory -RepoRoot $RepoRoot
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    return Join-Path $outputDirectory "sf-ad-delta-$timestamp.csv"
}

function Write-DiffCsv {
    param(
        [Parameter(Mandatory)]
        [object[]]$Rows,
        [Parameter(Mandatory)]
        [string]$OutputPath
    )

    $directory = Split-Path -Parent $OutputPath
    if ([string]::IsNullOrWhiteSpace($directory)) {
        throw "Could not resolve an output directory from path '$OutputPath'."
    }

    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporaryPath = Join-Path $directory ([System.Guid]::NewGuid().ToString() + '.tmp.csv')

    try {
        $Rows | Export-Csv -Path $temporaryPath -NoTypeInformation -Encoding utf8
        Move-Item -Path $temporaryPath -Destination $OutputPath -Force
    }
    finally {
        if (Test-Path $temporaryPath) {
            Remove-Item -Path $temporaryPath -Force
        }
    }
}

function Write-DiffSummary {
    param(
        [Parameter(Mandatory)]
        [object[]]$Rows,
        [Parameter(Mandatory)]
        [string]$OutputPath
    )

    $statuses = @('Match', 'OnlyInSF', 'OnlyInAD', 'DuplicateSFAnchor', 'DuplicateADAnchor', 'AnchorMismatch')
    foreach ($status in $statuses) {
        $count = @($Rows | Where-Object { $_.status -eq $status }).Count
        Write-Host ("{0}: {1}" -f $status, $count)
    }

    $otherCount = @($Rows | Where-Object { $statuses -notcontains $_.status }).Count
    if ($otherCount -gt 0) {
        Write-Host ("Other: {0}" -f $otherCount)
    }

    Write-Host ("OutputPath: {0}" -f $OutputPath)
}

function Export-SfAdDeltaReport {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$ConfigPath,
        [AllowNull()]
        [AllowEmptyString()]
        [string]$OutputPath,
        [bool]$IncludeDisabledAdUsers = $true,
        [int]$PageSize
    )

    $repoRoot = Get-RepoRoot
    $resolvedConfigPath = Resolve-ConfigPath -ConfigPath $ConfigPath -RepoRoot $repoRoot
    $runtimeConfig = Read-SyncRuntimeConfig -ConfigPath $resolvedConfigPath
    $effectivePageSize = if ($PSBoundParameters.ContainsKey('PageSize') -and $PageSize -gt 0) {
        $PageSize
    }
    else {
        $runtimeConfig.SuccessFactors.Query.PageSize
    }

    $sfRecords = Get-SuccessFactorsWorkers -RuntimeConfig $runtimeConfig -EffectivePageSize $effectivePageSize
    $adRecords = Get-ActiveDirectoryUsers -RuntimeConfig $runtimeConfig -IncludeDisabledAdUsers:$IncludeDisabledAdUsers
    $rows = Get-SfAdDeltaRows -SfRecords $sfRecords -AdRecords $adRecords
    $resolvedOutputPath = Resolve-OutputPath -OutputPath $OutputPath -RuntimeConfig $runtimeConfig -RepoRoot $repoRoot

    Write-DiffCsv -Rows $rows -OutputPath $resolvedOutputPath
    Write-DiffSummary -Rows $rows -OutputPath $resolvedOutputPath

    return $rows
}

if ($MyInvocation.InvocationName -ne '.') {
    Export-SfAdDeltaReport @PSBoundParameters | Out-Null
}
