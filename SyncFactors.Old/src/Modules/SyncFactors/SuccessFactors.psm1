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
    $username = "$($basic.username)"
    $companyId = $null
    if (
        $Config.successFactors.PSObject.Properties.Name -contains 'auth' -and
        $Config.successFactors.auth -and
        $Config.successFactors.auth.PSObject.Properties.Name -contains 'basic' -and
        $Config.successFactors.auth.basic -and
        $Config.successFactors.auth.basic.PSObject.Properties.Name -contains 'companyId' -and
        -not [string]::IsNullOrWhiteSpace("$($Config.successFactors.auth.basic.companyId)")
    ) {
        $companyId = "$($Config.successFactors.auth.basic.companyId)"
    } elseif (
        $Config.successFactors.PSObject.Properties.Name -contains 'auth' -and
        $Config.successFactors.auth -and
        $Config.successFactors.auth.PSObject.Properties.Name -contains 'oauth' -and
        $Config.successFactors.auth.oauth -and
        $Config.successFactors.auth.oauth.PSObject.Properties.Name -contains 'companyId' -and
        -not [string]::IsNullOrWhiteSpace("$($Config.successFactors.auth.oauth.companyId)")
    ) {
        $companyId = "$($Config.successFactors.auth.oauth.companyId)"
    } elseif (
        $Config.successFactors.PSObject.Properties.Name -contains 'oauth' -and
        $Config.successFactors.oauth -and
        $Config.successFactors.oauth.PSObject.Properties.Name -contains 'companyId' -and
        -not [string]::IsNullOrWhiteSpace("$($Config.successFactors.oauth.companyId)")
    ) {
        $companyId = "$($Config.successFactors.oauth.companyId)"
    }

    if (-not [string]::IsNullOrWhiteSpace($companyId) -and $username -notmatch '@') {
        $username = "$username@$companyId"
    }

    $credentialBytes = [System.Text.Encoding]::UTF8.GetBytes("${username}:$($basic.password)")
    return 'Basic ' + [Convert]::ToBase64String($credentialBytes)
}

function Get-SfOAuthClientAuthenticationMode {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $oauth = if ($Config.successFactors.PSObject.Properties.Name -contains 'auth') { $Config.successFactors.auth.oauth } else { $Config.successFactors.oauth }
    if ($oauth -and $oauth.PSObject.Properties.Name -contains 'clientAuthentication' -and -not [string]::IsNullOrWhiteSpace("$($oauth.clientAuthentication)")) {
        return "$($oauth.clientAuthentication)".ToLowerInvariant()
    }

    return 'body'
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

function Get-SfHttpResponseBody {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Response
    )

    if ($null -eq $Response) {
        return $null
    }

    if ($Response.PSObject.Methods.Name -contains 'GetResponseStream') {
        $responseStream = $Response.GetResponseStream()
        if (-not $responseStream) {
            return $null
        }

        $reader = [System.IO.StreamReader]::new($responseStream)
        try {
            return $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
            $responseStream.Dispose()
        }
    }

    if ($Response -is [System.Net.Http.HttpResponseMessage] -and $null -ne $Response.Content) {
        return $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }

    if ($Response.PSObject.Properties.Name -contains 'Content' -and $null -ne $Response.Content) {
        $content = $Response.Content
        if ($content.PSObject.Methods.Name -contains 'ReadAsStringAsync') {
            return $content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
    }

    return $null
}

function Get-SfExceptionDetails {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Exception]$Exception,
        [AllowNull()]
        [System.Management.Automation.ErrorRecord]$ErrorRecord,
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
    }

    $responseBody = $null
    if ($ErrorRecord -and $ErrorRecord.ErrorDetails -and -not [string]::IsNullOrWhiteSpace($ErrorRecord.ErrorDetails.Message)) {
        $responseBody = $ErrorRecord.ErrorDetails.Message
    } elseif ($response) {
        try {
            $responseBody = Get-SfHttpResponseBody -Response $response
        } catch {
            $details.Add("Response body read failed: $($_.Exception.Message)")
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($responseBody)) {
        $responseBody = Get-SfSanitizedText -Text $responseBody -Secrets $Secrets
        if ($responseBody.Length -gt 500) {
            $responseBody = $responseBody.Substring(0, 500)
        }

        $details.Add("Response body: $responseBody")
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
        [System.Management.Automation.ErrorRecord]$ErrorRecord,
        [AllowNull()]
        [string[]]$AdditionalDetails,
        [AllowNull()]
        [string[]]$Secrets
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("$Operation failed.")
    $lines.Add("URI: $Uri")
    foreach ($detail in @($AdditionalDetails)) {
        if (-not [string]::IsNullOrWhiteSpace($detail)) {
            $lines.Add($detail)
        }
    }

    foreach ($line in Get-SfExceptionDetails -Exception $Exception -ErrorRecord $ErrorRecord -Secrets $Secrets) {
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

    $clientAuthentication = Get-SfOAuthClientAuthenticationMode -Config $Config
    if (@('body', 'basic') -notcontains $clientAuthentication) {
        throw "successFactors.auth.oauth.clientAuthentication must be 'body' or 'basic'."
    }

    $body = @{
        grant_type = 'client_credentials'
    }

    $headers = @{}

    if ($clientAuthentication -eq 'basic') {
        $credentialBytes = [System.Text.Encoding]::UTF8.GetBytes("$($oauth.clientId):$($oauth.clientSecret)")
        $headers.Authorization = 'Basic ' + [Convert]::ToBase64String($credentialBytes)
    } else {
        $body['client_id'] = $oauth.clientId
        $body['client_secret'] = $oauth.clientSecret
    }

    if ($oauth.companyId) {
        $body['company_id'] = $oauth.companyId
    }

    try {
        $invokeParams = @{
            Uri         = $tokenUri
            Method      = 'Post'
            Body        = $body
            ContentType = 'application/x-www-form-urlencoded'
        }

        if ($headers.Count -gt 0) {
            $invokeParams['Headers'] = $headers
        }

        $response = Invoke-RestMethod @invokeParams
    } catch {
        $secrets = @(
            "$($oauth.clientSecret)"
        )
        throw (New-SfRequestFailure -Operation 'SuccessFactors OAuth token request' -Uri $tokenUri -Exception $_.Exception -ErrorRecord $_ -Secrets $secrets)
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

function Get-SfQueryDefinition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [switch]$ForWorkerPreview
    )

    $query = $Config.successFactors.query
    if (
        $ForWorkerPreview -and
        $Config.successFactors.PSObject.Properties.Name -contains 'previewQuery' -and
        $null -ne $Config.successFactors.previewQuery
    ) {
        $previewQuery = $Config.successFactors.previewQuery
        return [pscustomobject]@{
            entitySet     = if ($previewQuery.PSObject.Properties.Name -contains 'entitySet' -and -not [string]::IsNullOrWhiteSpace("$($previewQuery.entitySet)")) { $previewQuery.entitySet } else { $query.entitySet }
            identityField = if ($previewQuery.PSObject.Properties.Name -contains 'identityField' -and -not [string]::IsNullOrWhiteSpace("$($previewQuery.identityField)")) { $previewQuery.identityField } else { $query.identityField }
            deltaField    = if ($previewQuery.PSObject.Properties.Name -contains 'deltaField' -and -not [string]::IsNullOrWhiteSpace("$($previewQuery.deltaField)")) { $previewQuery.deltaField } else { $query.deltaField }
            select        = if ($previewQuery.PSObject.Properties.Name -contains 'select') { @($previewQuery.select) } else { @($query.select) }
            expand        = if ($previewQuery.PSObject.Properties.Name -contains 'expand') { @($previewQuery.expand) } else { @($query.expand) }
            baseFilter    = if ($previewQuery.PSObject.Properties.Name -contains 'baseFilter') { $previewQuery.baseFilter } elseif ($query.PSObject.Properties.Name -contains 'baseFilter') { $query.baseFilter } else { $null }
        }
    }

    return $query
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
            if ([string]::IsNullOrWhiteSpace("$($Query[$key])")) {
                continue
            }
            $encodedKey = [System.Uri]::EscapeDataString($key)
            $encodedValue = [System.Uri]::EscapeDataString("$($Query[$key])")
            "$encodedKey=$encodedValue"
        }

        if (@($pairs).Count -gt 0) {
            $uriBuilder.Query = ($pairs -join '&')
        }
    }

    $requestUri = $uriBuilder.Uri.AbsoluteUri
    $headers = Get-SfAuthHeaders -Config $Config

    try {
        return Invoke-RestMethod -Uri $requestUri -Headers $headers -Method Get
    } catch {
        $authMode = Get-SfAuthMode -Config $Config
        $oauth = if ($Config.successFactors.PSObject.Properties.Name -contains 'auth') { $Config.successFactors.auth.oauth } else { $Config.successFactors.oauth }
        $authScheme = if ($headers.Authorization -match '^(?<scheme>\S+)\s+') { $matches['scheme'] } else { '(missing)' }
        $secrets = @(
            $(if ($authMode -eq 'basic') { "$($Config.successFactors.auth.basic.password)" } else { "$($oauth.clientSecret)" }),
            "$($headers.Authorization)"
        )
        throw (New-SfRequestFailure -Operation 'SuccessFactors OData request' -Uri $requestUri -Exception $_.Exception -ErrorRecord $_ -AdditionalDetails @("Auth mode: $authMode", "Auth scheme: $authScheme") -Secrets $secrets)
    }
}

function Get-SfEntityShortName {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string]$EntityName
    )

    if ([string]::IsNullOrWhiteSpace($EntityName)) {
        return $null
    }

    $segments = $EntityName -split '\.'
    return $segments[$segments.Length - 1]
}

function Get-SfODataMetadataXml {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $baseUrl = $Config.successFactors.baseUrl.TrimEnd('/')
    $requestUri = "$baseUrl/`$metadata"
    $headers = Get-SfAuthHeaders -Config $Config
    $headers.Accept = 'application/xml'

    try {
        $response = Invoke-WebRequest -Uri $requestUri -Headers $headers -Method Get
        return $response.Content
    } catch {
        $authMode = Get-SfAuthMode -Config $Config
        $oauth = if ($Config.successFactors.PSObject.Properties.Name -contains 'auth') { $Config.successFactors.auth.oauth } else { $Config.successFactors.oauth }
        $authScheme = if ($headers.Authorization -match '^(?<scheme>\S+)\s+') { $matches['scheme'] } else { '(missing)' }
        $secrets = @(
            $(if ($authMode -eq 'basic') { "$($Config.successFactors.auth.basic.password)" } else { "$($oauth.clientSecret)" }),
            "$($headers.Authorization)"
        )
        throw (New-SfRequestFailure -Operation 'SuccessFactors metadata request' -Uri $requestUri -Exception $_.Exception -ErrorRecord $_ -AdditionalDetails @("Auth mode: $authMode", "Auth scheme: $authScheme") -Secrets $secrets)
    }
}

function ConvertFrom-SfODataMetadataXml {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$MetadataXml
    )

    $document = [xml]$MetadataXml
    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaceManager.AddNamespace('edmx', 'http://schemas.microsoft.com/ado/2007/06/edmx')
    $namespaceManager.AddNamespace('edm', 'http://schemas.microsoft.com/ado/2008/09/edm')

    $associations = @{}
    foreach ($schema in @($document.SelectNodes('//edm:Schema', $namespaceManager))) {
        $schemaNamespace = "$($schema.Namespace)"
        foreach ($association in @($schema.SelectNodes('edm:Association', $namespaceManager))) {
            $associationName = "$schemaNamespace.$($association.Name)"
            $ends = @()
            foreach ($end in @($association.SelectNodes('edm:End', $namespaceManager))) {
                $ends += [pscustomobject]@{
                    role         = "$($end.Role)"
                    entityType   = Get-SfEntityShortName -EntityName "$($end.Type)"
                    multiplicity = "$($end.Multiplicity)"
                }
            }

            $associations[$associationName] = $ends
        }
    }

    $entities = [ordered]@{}
    foreach ($schema in @($document.SelectNodes('//edm:Schema', $namespaceManager))) {
        foreach ($entityType in @($schema.SelectNodes('edm:EntityType', $namespaceManager))) {
            $propertyNames = @($entityType.SelectNodes('edm:Property', $namespaceManager) | ForEach-Object { "$($_.Name)" })
            $navigationProperties = @()
            foreach ($navigationProperty in @($entityType.SelectNodes('edm:NavigationProperty', $namespaceManager))) {
                $associationName = "$($navigationProperty.Relationship)"
                $targetEntityType = $null
                $multiplicity = $null
                if ($associations.Contains($associationName)) {
                    $targetEnd = @($associations[$associationName] | Where-Object { $_.role -eq "$($navigationProperty.ToRole)" })[0]
                    if ($targetEnd) {
                        $targetEntityType = $targetEnd.entityType
                        $multiplicity = $targetEnd.multiplicity
                    }
                }

                $navigationProperties += [pscustomobject]@{
                    name             = "$($navigationProperty.Name)"
                    relationship     = $associationName
                    toRole           = "$($navigationProperty.ToRole)"
                    fromRole         = "$($navigationProperty.FromRole)"
                    targetEntityType = $targetEntityType
                    multiplicity     = $multiplicity
                }
            }

            $entities[$entityType.Name] = [pscustomobject]@{
                name                 = "$($entityType.Name)"
                keyProperties        = @($entityType.SelectNodes('edm:Key/edm:PropertyRef', $namespaceManager) | ForEach-Object { "$($_.Name)" })
                properties           = $propertyNames
                navigationProperties = $navigationProperties
            }
        }
    }

    $entitySets = [ordered]@{}
    foreach ($schema in @($document.SelectNodes('//edm:Schema', $namespaceManager))) {
        foreach ($entitySet in @($schema.SelectNodes('edm:EntityContainer/edm:EntitySet', $namespaceManager))) {
            $entitySets[$entitySet.Name] = Get-SfEntityShortName -EntityName "$($entitySet.EntityType)"
        }
    }

    return [pscustomobject]@{
        entities   = [pscustomobject]$entities
        entitySets = [pscustomobject]$entitySets
    }
}

function Test-SfODataMetadataPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Schema,
        [Parameter(Mandatory)]
        [string]$EntitySetName,
        [Parameter(Mandatory)]
        [string]$Path,
        [ValidateSet('select', 'expand')]
        [string]$PathType = 'select'
    )

    $rootEntityType = $Schema.entitySets.PSObject.Properties[$EntitySetName]
    if (-not $rootEntityType) {
        return [pscustomobject]@{
            path              = $Path
            pathType          = $PathType
            isValid           = $false
            entitySetName     = $EntitySetName
            entityTypeName    = $null
            resolvedEntityType = $null
            resolvedProperty  = $null
            failureSegment    = $EntitySetName
            failureReason     = 'UnknownEntitySet'
        }
    }

    $currentEntityTypeName = "$($rootEntityType.Value)"
    $segments = @($Path -split '/' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    for ($index = 0; $index -lt $segments.Count; $index += 1) {
        $segment = $segments[$index]
        $entityDefinitionProperty = $Schema.entities.PSObject.Properties[$currentEntityTypeName]
        if (-not $entityDefinitionProperty) {
            return [pscustomobject]@{
                path               = $Path
                pathType           = $PathType
                isValid            = $false
                entitySetName      = $EntitySetName
                entityTypeName     = $currentEntityTypeName
                resolvedEntityType = $null
                resolvedProperty   = $null
                failureSegment     = $segment
                failureReason      = 'UnknownEntityType'
            }
        }

        $entityDefinition = $entityDefinitionProperty.Value
        $navigationMatch = $entityDefinition.navigationProperties | Where-Object { $_.name -eq $segment } | Select-Object -First 1
        $propertyMatch = $entityDefinition.properties | Where-Object { $_ -eq $segment } | Select-Object -First 1
        $isLastSegment = $index -eq ($segments.Count - 1)

        if ($navigationMatch) {
            if ($isLastSegment -and $PathType -eq 'select') {
                return [pscustomobject]@{
                    path               = $Path
                    pathType           = $PathType
                    isValid            = $false
                    entitySetName      = $EntitySetName
                    entityTypeName     = $currentEntityTypeName
                    resolvedEntityType = $navigationMatch.targetEntityType
                    resolvedProperty   = $null
                    failureSegment     = $segment
                    failureReason      = 'SelectEndsOnNavigationProperty'
                }
            }

            if ([string]::IsNullOrWhiteSpace($navigationMatch.targetEntityType)) {
                return [pscustomobject]@{
                    path               = $Path
                    pathType           = $PathType
                    isValid            = $false
                    entitySetName      = $EntitySetName
                    entityTypeName     = $currentEntityTypeName
                    resolvedEntityType = $null
                    resolvedProperty   = $null
                    failureSegment     = $segment
                    failureReason      = 'NavigationTargetUnknown'
                }
            }

            $currentEntityTypeName = $navigationMatch.targetEntityType
            continue
        }

        if ($propertyMatch) {
            if (-not $isLastSegment) {
                return [pscustomobject]@{
                    path               = $Path
                    pathType           = $PathType
                    isValid            = $false
                    entitySetName      = $EntitySetName
                    entityTypeName     = $currentEntityTypeName
                    resolvedEntityType = $currentEntityTypeName
                    resolvedProperty   = $propertyMatch
                    failureSegment     = $segment
                    failureReason      = 'PropertyUsedAsIntermediateSegment'
                }
            }

            return [pscustomobject]@{
                path               = $Path
                pathType           = $PathType
                isValid            = $true
                entitySetName      = $EntitySetName
                entityTypeName     = $currentEntityTypeName
                resolvedEntityType = $currentEntityTypeName
                resolvedProperty   = $propertyMatch
                failureSegment     = $null
                failureReason      = $null
            }
        }

        return [pscustomobject]@{
            path               = $Path
            pathType           = $PathType
            isValid            = $false
            entitySetName      = $EntitySetName
            entityTypeName     = $currentEntityTypeName
            resolvedEntityType = $null
            resolvedProperty   = $null
            failureSegment     = $segment
            failureReason      = 'SegmentNotFound'
        }
    }

    return [pscustomobject]@{
        path               = $Path
        pathType           = $PathType
        isValid            = $true
        entitySetName      = $EntitySetName
        entityTypeName     = $currentEntityTypeName
        resolvedEntityType = $currentEntityTypeName
        resolvedProperty   = $null
        failureSegment     = $null
        failureReason      = $null
    }
}

function Get-SfODataSchemaExport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [string[]]$EntityNames
    )

    $metadataXml = Get-SfODataMetadataXml -Config $Config
    $schema = ConvertFrom-SfODataMetadataXml -MetadataXml $metadataXml
    $queryDefinition = Get-SfQueryDefinition -Config $Config
    $rootEntitySetName = "$($queryDefinition.entitySet)"

    $selectedEntityNames = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in @($EntityNames)) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        if (-not $selectedEntityNames.Contains($candidate)) {
            $selectedEntityNames.Add($candidate)
        }
    }

    if ($selectedEntityNames.Count -eq 0) {
        foreach ($path in @($queryDefinition.select) + @($queryDefinition.expand)) {
            $pathCheck = Test-SfODataMetadataPath -Schema $schema -EntitySetName $rootEntitySetName -Path $path -PathType $(if (@($queryDefinition.expand) -contains $path) { 'expand' } else { 'select' })
            foreach ($entityName in @($pathCheck.entityTypeName, $pathCheck.resolvedEntityType)) {
                if ([string]::IsNullOrWhiteSpace($entityName)) {
                    continue
                }

                if (-not $selectedEntityNames.Contains($entityName)) {
                    $selectedEntityNames.Add($entityName)
                }
            }
        }

        foreach ($defaultEntityName in @($schema.entitySets.PSObject.Properties[$rootEntitySetName].Value, 'PerPerson', 'EmpEmployment', 'EmpJob', 'PerPersonal', 'PerEmail', 'User')) {
            if ([string]::IsNullOrWhiteSpace($defaultEntityName)) {
                continue
            }

            if (-not $selectedEntityNames.Contains($defaultEntityName)) {
                $selectedEntityNames.Add($defaultEntityName)
            }
        }
    }

    $entitySummaries = foreach ($entityName in $selectedEntityNames) {
        $entityProperty = $schema.entities.PSObject.Properties[$entityName]
        if (-not $entityProperty) {
            [pscustomobject]@{
                name                 = $entityName
                exists               = $false
                keyProperties        = @()
                propertyCount        = 0
                properties           = @()
                navigationProperties = @()
            }
            continue
        }

        $entity = $entityProperty.Value
        [pscustomobject]@{
            name                 = $entityName
            exists               = $true
            keyProperties        = @($entity.keyProperties)
            propertyCount        = @($entity.properties).Count
            properties           = @($entity.properties | Sort-Object)
            navigationProperties = @($entity.navigationProperties | Sort-Object name)
        }
    }

    $pathValidations = @()
    foreach ($path in @($queryDefinition.select)) {
        $pathValidations += Test-SfODataMetadataPath -Schema $schema -EntitySetName $rootEntitySetName -Path $path -PathType 'select'
    }
    foreach ($path in @($queryDefinition.expand)) {
        $pathValidations += Test-SfODataMetadataPath -Schema $schema -EntitySetName $rootEntitySetName -Path $path -PathType 'expand'
    }

    return [pscustomobject]@{
        artifactType     = 'SchemaExport'
        exportedAt       = (Get-Date).ToString('o')
        metadataUri      = "$($Config.successFactors.baseUrl.TrimEnd('/'))/`$metadata"
        entitySetName    = $rootEntitySetName
        entityTypeName   = "$($schema.entitySets.PSObject.Properties[$rootEntitySetName].Value)"
        configuredSelect = @($queryDefinition.select)
        configuredExpand = @($queryDefinition.expand)
        pathValidations  = $pathValidations
        entities         = $entitySummaries
        metadataXml      = $metadataXml
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

    $queryDefinition = Get-SfQueryDefinition -Config $Config
    $workerQuery = @{}
    if (@($queryDefinition.select).Count -gt 0) {
        $workerQuery['$select'] = ($queryDefinition.select -join ',')
    }
    if (@($queryDefinition.expand).Count -gt 0) {
        $workerQuery['$expand'] = ($queryDefinition.expand -join ',')
    }

    if ($Mode -eq 'Delta' -and $Checkpoint) {
        $workerQuery['$filter'] = "$($queryDefinition.deltaField) ge datetime'$Checkpoint'"
    } elseif ($queryDefinition.baseFilter) {
        $workerQuery['$filter'] = $queryDefinition.baseFilter
    }

    $response = Invoke-SfODataGet -Config $Config -RelativePath $queryDefinition.entitySet -Query $workerQuery
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

    $queryDefinition = Get-SfQueryDefinition -Config $Config -ForWorkerPreview
    $query = @{
        '$filter' = "$($queryDefinition.identityField) eq '$WorkerId'"
    }
    if (@($queryDefinition.select).Count -gt 0) {
        $query['$select'] = ($queryDefinition.select -join ',')
    }
    if (@($queryDefinition.expand).Count -gt 0) {
        $query['$expand'] = ($queryDefinition.expand -join ',')
    }

    $response = Invoke-SfODataGet -Config $Config -RelativePath $queryDefinition.entitySet -Query $query
    if ($response.PSObject.Properties.Name -contains 'd' -and $response.d -and $response.d.results -and $response.d.results.Count -gt 0) {
        return $response.d.results[0]
    }

    if ($response.PSObject.Properties.Name -contains 'value' -and $response.value -and $response.value.Count -gt 0) {
        return $response.value[0]
    }

    return $null
}

Export-ModuleMember -Function Get-SfOAuthToken, Get-SfAuthHeaders, Invoke-SfODataGet, Get-SfODataMetadataXml, ConvertFrom-SfODataMetadataXml, Test-SfODataMetadataPath, Get-SfODataSchemaExport, Get-SfWorkers, Get-SfWorkerById
