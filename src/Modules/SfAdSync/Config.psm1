Set-StrictMode -Version Latest

function Get-SfAdResolvedSetting {
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

function Test-SfAdHasProperty {
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

function Assert-SfAdRequiredString {
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

function Set-SfAdPropertyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$InputObject,
        [Parameter(Mandatory)]
        [string]$PropertyName,
        [AllowNull()]
        [object]$Value
    )

    if (Test-SfAdHasProperty -InputObject $InputObject -PropertyName $PropertyName) {
        $InputObject.$PropertyName = $Value
        return
    }

    $InputObject | Add-Member -MemberType NoteProperty -Name $PropertyName -Value $Value -Force
}

function Get-SfAdAuthMode {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $successFactors = $Config.successFactors
    $hasAuth = Test-SfAdHasProperty -InputObject $successFactors -PropertyName 'auth'
    $hasLegacyOAuth = Test-SfAdHasProperty -InputObject $successFactors -PropertyName 'oauth'

    if ($hasAuth -and (Test-SfAdHasProperty -InputObject $successFactors.auth -PropertyName 'mode') -and -not [string]::IsNullOrWhiteSpace("$($successFactors.auth.mode)")) {
        return "$($successFactors.auth.mode)".ToLowerInvariant()
    }

    if ($hasLegacyOAuth) {
        return 'oauth'
    }

    if ($hasAuth -and (Test-SfAdHasProperty -InputObject $successFactors.auth -PropertyName 'basic')) {
        return 'basic'
    }

    return 'basic'
}

function Get-SfAdSuccessFactorsAuthSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $authMode = Get-SfAdAuthMode -Config $Config
    if ($authMode -eq 'basic') {
        return 'basic'
    }

    $auth = Initialize-SfAdSuccessFactorsAuthConfig -Config $Config
    $oauth = $auth.oauth
    $clientAuthentication = if (
        (Test-SfAdHasProperty -InputObject $oauth -PropertyName 'clientAuthentication') -and
        -not [string]::IsNullOrWhiteSpace("$($oauth.clientAuthentication)")
    ) {
        "$($oauth.clientAuthentication)".ToLowerInvariant()
    } else {
        'body'
    }

    return "oauth ($clientAuthentication client auth)"
}

function Initialize-SfAdSuccessFactorsAuthConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $successFactors = $Config.successFactors
    if (-not (Test-SfAdHasProperty -InputObject $successFactors -PropertyName 'auth') -or $null -eq $successFactors.auth) {
        $successFactors | Add-Member -MemberType NoteProperty -Name 'auth' -Value ([pscustomobject]@{}) -Force
    }

    $auth = $successFactors.auth
    if (-not (Test-SfAdHasProperty -InputObject $auth -PropertyName 'basic') -or $null -eq $auth.basic) {
        $auth | Add-Member -MemberType NoteProperty -Name 'basic' -Value ([pscustomobject]@{}) -Force
    }

    if (-not (Test-SfAdHasProperty -InputObject $auth -PropertyName 'oauth') -or $null -eq $auth.oauth) {
        $oauthValue = if (Test-SfAdHasProperty -InputObject $successFactors -PropertyName 'oauth') { $successFactors.oauth } else { [pscustomobject]@{} }
        $auth | Add-Member -MemberType NoteProperty -Name 'oauth' -Value $oauthValue -Force
    }

    Set-SfAdPropertyValue -InputObject $auth -PropertyName 'mode' -Value (Get-SfAdAuthMode -Config $Config)
    return $auth
}

function Resolve-SfAdSyncSecrets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $secrets = if (Test-SfAdHasProperty -InputObject $Config -PropertyName 'secrets') { $Config.secrets } else { $null }

    $auth = Initialize-SfAdSuccessFactorsAuthConfig -Config $Config
    $basic = $auth.basic
    $oauth = $auth.oauth
    Set-SfAdPropertyValue -InputObject $basic -PropertyName 'username' -Value (Get-SfAdResolvedSetting -Value $(if (Test-SfAdHasProperty -InputObject $basic -PropertyName 'username') { $basic.username } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SfAdHasProperty -InputObject $secrets -PropertyName 'successFactorsUsernameEnv')) { $secrets.successFactorsUsernameEnv } else { 'SF_AD_SYNC_SF_USERNAME' }))
    Set-SfAdPropertyValue -InputObject $basic -PropertyName 'password' -Value (Get-SfAdResolvedSetting -Value $(if (Test-SfAdHasProperty -InputObject $basic -PropertyName 'password') { $basic.password } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SfAdHasProperty -InputObject $secrets -PropertyName 'successFactorsPasswordEnv')) { $secrets.successFactorsPasswordEnv } else { 'SF_AD_SYNC_SF_PASSWORD' }))
    Set-SfAdPropertyValue -InputObject $oauth -PropertyName 'clientId' -Value (Get-SfAdResolvedSetting -Value $(if (Test-SfAdHasProperty -InputObject $oauth -PropertyName 'clientId') { $oauth.clientId } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SfAdHasProperty -InputObject $secrets -PropertyName 'successFactorsClientIdEnv')) { $secrets.successFactorsClientIdEnv } else { 'SF_AD_SYNC_SF_CLIENT_ID' }))
    Set-SfAdPropertyValue -InputObject $oauth -PropertyName 'clientSecret' -Value (Get-SfAdResolvedSetting -Value $(if (Test-SfAdHasProperty -InputObject $oauth -PropertyName 'clientSecret') { $oauth.clientSecret } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SfAdHasProperty -InputObject $secrets -PropertyName 'successFactorsClientSecretEnv')) { $secrets.successFactorsClientSecretEnv } else { 'SF_AD_SYNC_SF_CLIENT_SECRET' }))
    Set-SfAdPropertyValue -InputObject $Config.ad -PropertyName 'server' -Value (Get-SfAdResolvedSetting -Value $(if (Test-SfAdHasProperty -InputObject $Config.ad -PropertyName 'server') { $Config.ad.server } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SfAdHasProperty -InputObject $secrets -PropertyName 'adServerEnv')) { $secrets.adServerEnv } else { 'SF_AD_SYNC_AD_SERVER' }))
    Set-SfAdPropertyValue -InputObject $Config.ad -PropertyName 'username' -Value (Get-SfAdResolvedSetting -Value $(if (Test-SfAdHasProperty -InputObject $Config.ad -PropertyName 'username') { $Config.ad.username } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SfAdHasProperty -InputObject $secrets -PropertyName 'adUsernameEnv')) { $secrets.adUsernameEnv } else { 'SF_AD_SYNC_AD_USERNAME' }))
    Set-SfAdPropertyValue -InputObject $Config.ad -PropertyName 'bindPassword' -Value (Get-SfAdResolvedSetting -Value $(if (Test-SfAdHasProperty -InputObject $Config.ad -PropertyName 'bindPassword') { $Config.ad.bindPassword } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SfAdHasProperty -InputObject $secrets -PropertyName 'adBindPasswordEnv')) { $secrets.adBindPasswordEnv } else { 'SF_AD_SYNC_AD_BIND_PASSWORD' }))
    Set-SfAdPropertyValue -InputObject $Config.ad -PropertyName 'defaultPassword' -Value (Get-SfAdResolvedSetting -Value $(if (Test-SfAdHasProperty -InputObject $Config.ad -PropertyName 'defaultPassword') { $Config.ad.defaultPassword } else { $null }) -EnvironmentVariableName $(if ($secrets -and (Test-SfAdHasProperty -InputObject $secrets -PropertyName 'defaultAdPasswordEnv')) { $secrets.defaultAdPasswordEnv } else { 'SF_AD_SYNC_AD_DEFAULT_PASSWORD' }))

    return $Config
}

function Test-SfAdSyncMappingConfig {
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

        if (-not (Test-SfAdHasProperty -InputObject $mapping -PropertyName 'enabled')) {
            throw "Mapping at index $index must define enabled."
        }

        if (-not (Test-SfAdHasProperty -InputObject $mapping -PropertyName 'required')) {
            throw "Mapping at index $index must define required."
        }

        if ($supportedTransforms -notcontains $mapping.transform) {
            throw "Mapping at index $index has unsupported transform '$($mapping.transform)'."
        }

        $index += 1
    }
}

function Get-SfAdSyncConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        throw "Sync config file not found: $Path"
    }

    $config = Get-Content -Path $Path -Raw | ConvertFrom-Json -Depth 20
    $config = Resolve-SfAdSyncSecrets -Config $config
    Test-SfAdSyncConfig -Config $config
    return $config
}

function Get-SfAdSyncMappingConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        throw "Mapping config file not found: $Path"
    }

    $config = Get-Content -Path $Path -Raw | ConvertFrom-Json -Depth 20
    Test-SfAdSyncMappingConfig -Config $config
    return $config
}

function Test-SfAdSyncConfig {
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

    Assert-SfAdRequiredString -Value $Config.successFactors.baseUrl -PropertyPath 'successFactors.baseUrl'
    $auth = Initialize-SfAdSuccessFactorsAuthConfig -Config $Config
    $authMode = Get-SfAdAuthMode -Config $Config

    if (@('basic', 'oauth') -notcontains $authMode) {
        throw "Sync config must define successFactors.auth.mode as 'basic' or 'oauth'."
    }

    if ($authMode -eq 'basic') {
        Assert-SfAdRequiredString -Value $auth.basic.username -PropertyPath 'successFactors.auth.basic.username'
        Assert-SfAdRequiredString -Value $auth.basic.password -PropertyPath 'successFactors.auth.basic.password'
    }

    if ($authMode -eq 'oauth') {
        Assert-SfAdRequiredString -Value $auth.oauth.tokenUrl -PropertyPath 'successFactors.auth.oauth.tokenUrl'
        Assert-SfAdRequiredString -Value $auth.oauth.clientId -PropertyPath 'successFactors.auth.oauth.clientId'
        Assert-SfAdRequiredString -Value $auth.oauth.clientSecret -PropertyPath 'successFactors.auth.oauth.clientSecret'
    }

    Assert-SfAdRequiredString -Value $Config.successFactors.query.entitySet -PropertyPath 'successFactors.query.entitySet'
    Assert-SfAdRequiredString -Value $Config.successFactors.query.identityField -PropertyPath 'successFactors.query.identityField'
    Assert-SfAdRequiredString -Value $Config.successFactors.query.deltaField -PropertyPath 'successFactors.query.deltaField'

    if (@($Config.successFactors.query.select).Count -eq 0) {
        throw "Sync config must define successFactors.query.select."
    }

    if (@($Config.successFactors.query.expand).Count -eq 0) {
        throw "Sync config must define successFactors.query.expand."
    }

    if ((Test-SfAdHasProperty -InputObject $Config.successFactors -PropertyName 'previewQuery') -and $null -ne $Config.successFactors.previewQuery) {
        $previewQuery = $Config.successFactors.previewQuery
        if ((Test-SfAdHasProperty -InputObject $previewQuery -PropertyName 'entitySet') -and -not [string]::IsNullOrWhiteSpace("$($previewQuery.entitySet)")) {
            Assert-SfAdRequiredString -Value $previewQuery.entitySet -PropertyPath 'successFactors.previewQuery.entitySet'
        }

        if ((Test-SfAdHasProperty -InputObject $previewQuery -PropertyName 'identityField') -and -not [string]::IsNullOrWhiteSpace("$($previewQuery.identityField)")) {
            Assert-SfAdRequiredString -Value $previewQuery.identityField -PropertyPath 'successFactors.previewQuery.identityField'
        }
    }

    Assert-SfAdRequiredString -Value $Config.ad.identityAttribute -PropertyPath 'ad.identityAttribute'
    Assert-SfAdRequiredString -Value $Config.ad.defaultActiveOu -PropertyPath 'ad.defaultActiveOu'
    Assert-SfAdRequiredString -Value $Config.ad.graveyardOu -PropertyPath 'ad.graveyardOu'
    Assert-SfAdRequiredString -Value $Config.ad.defaultPassword -PropertyPath 'ad.defaultPassword'

    $hasAdServer = Test-SfAdHasProperty -InputObject $Config.ad -PropertyName 'server'
    $hasAdUsername = Test-SfAdHasProperty -InputObject $Config.ad -PropertyName 'username'
    $hasAdBindPassword = Test-SfAdHasProperty -InputObject $Config.ad -PropertyName 'bindPassword'
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

    Assert-SfAdRequiredString -Value $Config.state.path -PropertyPath 'state.path'
    Assert-SfAdRequiredString -Value $Config.reporting.outputDirectory -PropertyPath 'reporting.outputDirectory'
    if (-not (Test-SfAdHasProperty -InputObject $Config.reporting -PropertyName 'reviewOutputDirectory') -or [string]::IsNullOrWhiteSpace("$($Config.reporting.reviewOutputDirectory)")) {
        $reviewDirectory = Join-Path -Path $Config.reporting.outputDirectory -ChildPath 'review'
        Set-SfAdPropertyValue -InputObject $Config.reporting -PropertyName 'reviewOutputDirectory' -Value $reviewDirectory
    }

    if ([int]$Config.sync.enableBeforeStartDays -lt 0) {
        throw 'Sync config must define sync.enableBeforeStartDays as a non-negative integer.'
    }

    if ([int]$Config.sync.deletionRetentionDays -lt 0) {
        throw 'Sync config must define sync.deletionRetentionDays as a non-negative integer.'
    }

    if (Test-SfAdHasProperty -InputObject $Config -PropertyName 'safety') {
        foreach ($threshold in @('maxCreatesPerRun', 'maxDisablesPerRun', 'maxDeletionsPerRun')) {
            if (-not (Test-SfAdHasProperty -InputObject $Config.safety -PropertyName $threshold)) {
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

Export-ModuleMember -Function Get-SfAdResolvedSetting, Get-SfAdSuccessFactorsAuthSummary, Resolve-SfAdSyncSecrets, Get-SfAdSyncConfig, Get-SfAdSyncMappingConfig, Test-SfAdSyncConfig, Test-SfAdSyncMappingConfig
