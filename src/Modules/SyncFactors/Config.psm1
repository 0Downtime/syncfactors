Set-StrictMode -Version Latest

function Get-SyncFactorsResolvedSetting {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$Value,
        [string]$EnvironmentVariableName
    )

    if ($EnvironmentVariableName) {
        $environmentValue = [System.Environment]::GetEnvironmentVariable($EnvironmentVariableName)
        if (-not [string]::IsNullOrWhiteSpace($environmentValue)) {
            return $environmentValue
        }
    }

    return $Value
}

function Test-SyncFactorsHasProperty {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$InputObject,
        [Parameter(Mandatory)]
        [string]$PropertyName
    )

    if ($null -eq $InputObject) {
        return $false
    }

    return $null -ne $InputObject.PSObject.Properties[$PropertyName]
}

function Assert-SyncFactorsRequiredString {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$Value,
        [Parameter(Mandatory)]
        [string]$PropertyPath
    )

    if ([string]::IsNullOrWhiteSpace("$Value")) {
        throw "Sync config must define $PropertyPath."
    }
}

function Set-SyncFactorsPropertyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$InputObject,
        [Parameter(Mandatory)]
        [string]$PropertyName,
        [AllowNull()]
        [object]$Value
    )

    if (Test-SyncFactorsHasProperty -InputObject $InputObject -PropertyName $PropertyName) {
        $InputObject.$PropertyName = $Value
        return
    }

    $InputObject | Add-Member -MemberType NoteProperty -Name $PropertyName -Value $Value -Force
}

function Get-SyncFactorsDefaultSqlitePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StatePath
    )

    $stateDirectory = Split-Path -Path $StatePath -Parent
    if ([string]::IsNullOrWhiteSpace($stateDirectory)) {
        return 'syncfactors.db'
    }

    if ($stateDirectory.StartsWith('/')) {
        return "$stateDirectory/syncfactors.db"
    }

    return Join-Path -Path $stateDirectory -ChildPath 'syncfactors.db'
}

function Get-SyncFactorsAuthMode {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $successFactors = $Config.successFactors
    $hasAuth = Test-SyncFactorsHasProperty -InputObject $successFactors -PropertyName 'auth'
    $hasLegacyOAuth = Test-SyncFactorsHasProperty -InputObject $successFactors -PropertyName 'oauth'

    if ($hasAuth -and (Test-SyncFactorsHasProperty -InputObject $successFactors.auth -PropertyName 'mode') -and -not [string]::IsNullOrWhiteSpace("$($successFactors.auth.mode)")) {
        return "$($successFactors.auth.mode)".ToLowerInvariant()
    }

    if ($hasLegacyOAuth) {
        return 'oauth'
    }

    if ($hasAuth -and (Test-SyncFactorsHasProperty -InputObject $successFactors.auth -PropertyName 'basic')) {
        return 'basic'
    }

    return 'basic'
}

function Get-SyncFactorsSuccessFactorsAuthSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $authMode = Get-SyncFactorsAuthMode -Config $Config
    if ($authMode -eq 'basic') {
        return 'basic'
    }

    $auth = Initialize-SyncFactorsSuccessFactorsAuthConfig -Config $Config
    $oauth = $auth.oauth
    $clientAuthentication = if (
        (Test-SyncFactorsHasProperty -InputObject $oauth -PropertyName 'clientAuthentication') -and
        -not [string]::IsNullOrWhiteSpace("$($oauth.clientAuthentication)")
    ) {
        "$($oauth.clientAuthentication)".ToLowerInvariant()
    } else {
        'body'
    }

    return "oauth ($clientAuthentication client auth)"
}

function Initialize-SyncFactorsSuccessFactorsAuthConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $successFactors = $Config.successFactors
    if (-not (Test-SyncFactorsHasProperty -InputObject $successFactors -PropertyName 'auth') -or $null -eq $successFactors.auth) {
        $successFactors | Add-Member -MemberType NoteProperty -Name 'auth' -Value ([pscustomobject]@{}) -Force
    }

    $auth = $successFactors.auth
    if (-not (Test-SyncFactorsHasProperty -InputObject $auth -PropertyName 'basic') -or $null -eq $auth.basic) {
        $auth | Add-Member -MemberType NoteProperty -Name 'basic' -Value ([pscustomobject]@{}) -Force
    }

    if (-not (Test-SyncFactorsHasProperty -InputObject $auth -PropertyName 'oauth') -or $null -eq $auth.oauth) {
        $oauthValue = if (Test-SyncFactorsHasProperty -InputObject $successFactors -PropertyName 'oauth') { $successFactors.oauth } else { [pscustomobject]@{} }
        $auth | Add-Member -MemberType NoteProperty -Name 'oauth' -Value $oauthValue -Force
    }

    Set-SyncFactorsPropertyValue -InputObject $auth -PropertyName 'mode' -Value (Get-SyncFactorsAuthMode -Config $Config)
    return $auth
}

function Resolve-SyncFactorsSecrets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $secrets = if (Test-SyncFactorsHasProperty -InputObject $Config -PropertyName 'secrets') { $Config.secrets } else { $null }

    $auth = Initialize-SyncFactorsSuccessFactorsAuthConfig -Config $Config
    $basic = $auth.basic
    $oauth = $auth.oauth
    Set-SyncFactorsPropertyValue -InputObject $basic -PropertyName 'username' -Value (Get-SyncFactorsResolvedSetting -Value $(if (Test-SyncFactorsHasProperty -InputObject $basic -PropertyName 'username') { $basic.username } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SyncFactorsHasProperty -InputObject $secrets -PropertyName 'successFactorsUsernameEnv')) { $secrets.successFactorsUsernameEnv } else { 'SF_AD_SYNC_SF_USERNAME' }))
    Set-SyncFactorsPropertyValue -InputObject $basic -PropertyName 'password' -Value (Get-SyncFactorsResolvedSetting -Value $(if (Test-SyncFactorsHasProperty -InputObject $basic -PropertyName 'password') { $basic.password } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SyncFactorsHasProperty -InputObject $secrets -PropertyName 'successFactorsPasswordEnv')) { $secrets.successFactorsPasswordEnv } else { 'SF_AD_SYNC_SF_PASSWORD' }))
    Set-SyncFactorsPropertyValue -InputObject $oauth -PropertyName 'clientId' -Value (Get-SyncFactorsResolvedSetting -Value $(if (Test-SyncFactorsHasProperty -InputObject $oauth -PropertyName 'clientId') { $oauth.clientId } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SyncFactorsHasProperty -InputObject $secrets -PropertyName 'successFactorsClientIdEnv')) { $secrets.successFactorsClientIdEnv } else { 'SF_AD_SYNC_SF_CLIENT_ID' }))
    Set-SyncFactorsPropertyValue -InputObject $oauth -PropertyName 'clientSecret' -Value (Get-SyncFactorsResolvedSetting -Value $(if (Test-SyncFactorsHasProperty -InputObject $oauth -PropertyName 'clientSecret') { $oauth.clientSecret } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SyncFactorsHasProperty -InputObject $secrets -PropertyName 'successFactorsClientSecretEnv')) { $secrets.successFactorsClientSecretEnv } else { 'SF_AD_SYNC_SF_CLIENT_SECRET' }))
    Set-SyncFactorsPropertyValue -InputObject $Config.ad -PropertyName 'server' -Value (Get-SyncFactorsResolvedSetting -Value $(if (Test-SyncFactorsHasProperty -InputObject $Config.ad -PropertyName 'server') { $Config.ad.server } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SyncFactorsHasProperty -InputObject $secrets -PropertyName 'adServerEnv')) { $secrets.adServerEnv } else { 'SF_AD_SYNC_AD_SERVER' }))
    Set-SyncFactorsPropertyValue -InputObject $Config.ad -PropertyName 'username' -Value (Get-SyncFactorsResolvedSetting -Value $(if (Test-SyncFactorsHasProperty -InputObject $Config.ad -PropertyName 'username') { $Config.ad.username } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SyncFactorsHasProperty -InputObject $secrets -PropertyName 'adUsernameEnv')) { $secrets.adUsernameEnv } else { 'SF_AD_SYNC_AD_USERNAME' }))
    Set-SyncFactorsPropertyValue -InputObject $Config.ad -PropertyName 'bindPassword' -Value (Get-SyncFactorsResolvedSetting -Value $(if (Test-SyncFactorsHasProperty -InputObject $Config.ad -PropertyName 'bindPassword') { $Config.ad.bindPassword } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SyncFactorsHasProperty -InputObject $secrets -PropertyName 'adBindPasswordEnv')) { $secrets.adBindPasswordEnv } else { 'SF_AD_SYNC_AD_BIND_PASSWORD' }))
    Set-SyncFactorsPropertyValue -InputObject $Config.ad -PropertyName 'defaultPassword' -Value (Get-SyncFactorsResolvedSetting -Value $(if (Test-SyncFactorsHasProperty -InputObject $Config.ad -PropertyName 'defaultPassword') { $Config.ad.defaultPassword } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SyncFactorsHasProperty -InputObject $secrets -PropertyName 'defaultAdPasswordEnv')) { $secrets.defaultAdPasswordEnv } else { 'SF_AD_SYNC_AD_DEFAULT_PASSWORD' }))

    return $Config
}

function Test-SyncFactorsMappingConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    if (-not $Config.mappings) {
        throw "Mapping config must contain a 'mappings' array."
    }

    $supportedTransforms = @('Trim', 'Upper', 'Lower', 'DateOnly', $null, '')
    $index = 0
    foreach ($mapping in @($Config.mappings)) {
        if ([string]::IsNullOrWhiteSpace("$($mapping.source)")) {
            throw "Mapping at index $index must define source."
        }

        if ([string]::IsNullOrWhiteSpace("$($mapping.target)")) {
            throw "Mapping at index $index must define target."
        }

        if (-not (Test-SyncFactorsHasProperty -InputObject $mapping -PropertyName 'enabled')) {
            throw "Mapping at index $index must define enabled."
        }

        if (-not (Test-SyncFactorsHasProperty -InputObject $mapping -PropertyName 'required')) {
            throw "Mapping at index $index must define required."
        }

        if ($supportedTransforms -notcontains $mapping.transform) {
            throw "Mapping at index $index has unsupported transform '$($mapping.transform)'."
        }

        $index += 1
    }
}

function Get-SyncFactorsConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        throw "Sync config file not found: $Path"
    }

    $config = Get-Content -Path $Path -Raw | ConvertFrom-Json -Depth 20
    $config = Resolve-SyncFactorsSecrets -Config $config
    Test-SyncFactorsConfig -Config $config
    return $config
}

function Get-SyncFactorsMappingConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        throw "Mapping config file not found: $Path"
    }

    $config = Get-Content -Path $Path -Raw | ConvertFrom-Json -Depth 20
    Test-SyncFactorsMappingConfig -Config $config
    return $config
}

function Test-SyncFactorsConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $requiredProperties = @(
        'successFactors',
        'ad',
        'sync',
        'state',
        'reporting'
    )

    foreach ($property in $requiredProperties) {
        if (-not $Config.PSObject.Properties.Name.Contains($property)) {
            throw "Sync config is missing required property '$property'."
        }
    }

    Assert-SyncFactorsRequiredString -Value $Config.successFactors.baseUrl -PropertyPath 'successFactors.baseUrl'
    $auth = Initialize-SyncFactorsSuccessFactorsAuthConfig -Config $Config
    $authMode = Get-SyncFactorsAuthMode -Config $Config

    if (@('basic', 'oauth') -notcontains $authMode) {
        throw "Sync config must define successFactors.auth.mode as 'basic' or 'oauth'."
    }

    if ($authMode -eq 'basic') {
        Assert-SyncFactorsRequiredString -Value $auth.basic.username -PropertyPath 'successFactors.auth.basic.username'
        Assert-SyncFactorsRequiredString -Value $auth.basic.password -PropertyPath 'successFactors.auth.basic.password'
    }

    if ($authMode -eq 'oauth') {
        Assert-SyncFactorsRequiredString -Value $auth.oauth.tokenUrl -PropertyPath 'successFactors.auth.oauth.tokenUrl'
        Assert-SyncFactorsRequiredString -Value $auth.oauth.clientId -PropertyPath 'successFactors.auth.oauth.clientId'
        Assert-SyncFactorsRequiredString -Value $auth.oauth.clientSecret -PropertyPath 'successFactors.auth.oauth.clientSecret'
    }

    Assert-SyncFactorsRequiredString -Value $Config.successFactors.query.entitySet -PropertyPath 'successFactors.query.entitySet'
    Assert-SyncFactorsRequiredString -Value $Config.successFactors.query.identityField -PropertyPath 'successFactors.query.identityField'
    Assert-SyncFactorsRequiredString -Value $Config.successFactors.query.deltaField -PropertyPath 'successFactors.query.deltaField'

    if (@($Config.successFactors.query.select).Count -eq 0) {
        throw "Sync config must define successFactors.query.select."
    }

    if (@($Config.successFactors.query.expand).Count -eq 0) {
        throw "Sync config must define successFactors.query.expand."
    }

    if ((Test-SyncFactorsHasProperty -InputObject $Config.successFactors -PropertyName 'previewQuery') -and $null -ne $Config.successFactors.previewQuery) {
        $previewQuery = $Config.successFactors.previewQuery
        if ((Test-SyncFactorsHasProperty -InputObject $previewQuery -PropertyName 'entitySet') -and -not [string]::IsNullOrWhiteSpace("$($previewQuery.entitySet)")) {
            Assert-SyncFactorsRequiredString -Value $previewQuery.entitySet -PropertyPath 'successFactors.previewQuery.entitySet'
        }

        if ((Test-SyncFactorsHasProperty -InputObject $previewQuery -PropertyName 'identityField') -and -not [string]::IsNullOrWhiteSpace("$($previewQuery.identityField)")) {
            Assert-SyncFactorsRequiredString -Value $previewQuery.identityField -PropertyPath 'successFactors.previewQuery.identityField'
        }
    }

    Assert-SyncFactorsRequiredString -Value $Config.ad.identityAttribute -PropertyPath 'ad.identityAttribute'
    Assert-SyncFactorsRequiredString -Value $Config.ad.defaultActiveOu -PropertyPath 'ad.defaultActiveOu'
    Assert-SyncFactorsRequiredString -Value $Config.ad.graveyardOu -PropertyPath 'ad.graveyardOu'
    Assert-SyncFactorsRequiredString -Value $Config.ad.defaultPassword -PropertyPath 'ad.defaultPassword'

    $hasAdServer = Test-SyncFactorsHasProperty -InputObject $Config.ad -PropertyName 'server'
    $hasAdUsername = Test-SyncFactorsHasProperty -InputObject $Config.ad -PropertyName 'username'
    $hasAdBindPassword = Test-SyncFactorsHasProperty -InputObject $Config.ad -PropertyName 'bindPassword'
    $adServer = if ($hasAdServer) { "$($Config.ad.server)" } else { '' }
    $adUsername = if ($hasAdUsername) { "$($Config.ad.username)" } else { '' }
    $adBindPassword = if ($hasAdBindPassword) { "$($Config.ad.bindPassword)" } else { '' }

    if (-not [string]::IsNullOrWhiteSpace($adUsername) -and [string]::IsNullOrWhiteSpace($adBindPassword)) {
        throw 'Sync config must define ad.bindPassword when ad.username is provided.'
    }

    if (-not [string]::IsNullOrWhiteSpace($adBindPassword) -and [string]::IsNullOrWhiteSpace($adUsername)) {
        throw 'Sync config must define ad.username when ad.bindPassword is provided.'
    }

    if (-not [string]::IsNullOrWhiteSpace($adUsername) -and [string]::IsNullOrWhiteSpace($adServer)) {
        throw 'Sync config must define ad.server when alternate AD credentials are provided.'
    }

    Assert-SyncFactorsRequiredString -Value $Config.state.path -PropertyPath 'state.path'
    if (-not (Test-SyncFactorsHasProperty -InputObject $Config -PropertyName 'persistence') -or $null -eq $Config.persistence) {
        $Config | Add-Member -MemberType NoteProperty -Name 'persistence' -Value ([pscustomobject]@{}) -Force
    }

    if (-not (Test-SyncFactorsHasProperty -InputObject $Config.persistence -PropertyName 'sqlitePath') -or [string]::IsNullOrWhiteSpace("$($Config.persistence.sqlitePath)")) {
        Set-SyncFactorsPropertyValue -InputObject $Config.persistence -PropertyName 'sqlitePath' -Value (Get-SyncFactorsDefaultSqlitePath -StatePath $Config.state.path)
    }

    Assert-SyncFactorsRequiredString -Value $Config.reporting.outputDirectory -PropertyPath 'reporting.outputDirectory'
    if (-not (Test-SyncFactorsHasProperty -InputObject $Config.reporting -PropertyName 'reviewOutputDirectory') -or [string]::IsNullOrWhiteSpace("$($Config.reporting.reviewOutputDirectory)")) {
        $reviewDirectory = Join-Path -Path $Config.reporting.outputDirectory -ChildPath 'review'
        Set-SyncFactorsPropertyValue -InputObject $Config.reporting -PropertyName 'reviewOutputDirectory' -Value $reviewDirectory
    }

    if ([int]$Config.sync.enableBeforeStartDays -lt 0) {
        throw 'Sync config must define sync.enableBeforeStartDays as a non-negative integer.'
    }

    if ([int]$Config.sync.deletionRetentionDays -lt 0) {
        throw 'Sync config must define sync.deletionRetentionDays as a non-negative integer.'
    }

    if (Test-SyncFactorsHasProperty -InputObject $Config -PropertyName 'safety') {
        foreach ($threshold in @('maxCreatesPerRun', 'maxDisablesPerRun', 'maxDeletionsPerRun')) {
            if (-not (Test-SyncFactorsHasProperty -InputObject $Config.safety -PropertyName $threshold)) {
                continue
            }

            $value = $Config.safety.$threshold
            if ($null -eq $value -or "$value" -eq '') {
                continue
            }

            if ([int]$value -lt 0) {
                throw "Sync config must define safety.$threshold as a non-negative integer when provided."
            }
        }
    }
}

Export-ModuleMember -Function Get-SyncFactorsResolvedSetting, Get-SyncFactorsSuccessFactorsAuthSummary, Resolve-SyncFactorsSecrets, Get-SyncFactorsConfig, Get-SyncFactorsMappingConfig, Test-SyncFactorsConfig, Test-SyncFactorsMappingConfig, Get-SyncFactorsDefaultSqlitePath
