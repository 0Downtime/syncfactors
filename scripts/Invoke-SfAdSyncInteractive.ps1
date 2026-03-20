[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [Parameter(Mandatory)]
    [string]$MappingConfigPath,
    [ValidateSet('Delta','Full','Review')]
    [string]$Mode = 'Delta',
    [switch]$DryRun,
    [string]$WorkerId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-OptionalPropertyValue {
    param(
        [AllowNull()]
        [object]$InputObject,
        [Parameter(Mandatory)]
        [string]$PropertyPath
    )

    $current = $InputObject
    foreach ($segment in $PropertyPath.Split('.')) {
        if ($null -eq $current -or $current.PSObject.Properties.Name -notcontains $segment) {
            return $null
        }

        $current = $current.$segment
    }

    return $current
}

function Get-EffectiveEnvironmentVariableName {
    param(
        [AllowNull()]
        [object]$Secrets,
        [Parameter(Mandatory)]
        [string]$SecretPropertyName,
        [Parameter(Mandatory)]
        [string]$DefaultEnvironmentVariableName
    )

    if ($null -ne $Secrets -and $Secrets.PSObject.Properties.Name -contains $SecretPropertyName) {
        $value = "$($Secrets.$SecretPropertyName)"
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
    }

    return $DefaultEnvironmentVariableName
}

function Test-NeedsRuntimePrompt {
    param(
        [AllowNull()]
        [object]$Value
    )

    $text = "$Value"
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $true
    }

    return $text -match '^(?i:replace-me|replace-this-.*|companyid)$'
}

function Read-RequiredPromptValue {
    param(
        [Parameter(Mandatory)]
        [string]$Prompt,
        [switch]$AsSecureString
    )

    while ($true) {
        if ($AsSecureString) {
            $secureValue = Read-Host -Prompt $Prompt -AsSecureString
            $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
            try {
                $plainValue = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
            } finally {
                [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
            }
        } else {
            $plainValue = Read-Host -Prompt $Prompt
        }

        if (-not [string]::IsNullOrWhiteSpace($plainValue)) {
            return $plainValue
        }

        Write-Host 'A value is required to continue.'
    }
}

$resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
$config = Get-Content -Path $resolvedConfigPath -Raw | ConvertFrom-Json -Depth 20
$secrets = Get-OptionalPropertyValue -InputObject $config -PropertyPath 'secrets'
$adConfig = Get-OptionalPropertyValue -InputObject $config -PropertyPath 'ad'

$runtimePrompts = @(
    @{
        Name = 'SuccessFactors client id'
        PropertyPath = 'successFactors.oauth.clientId'
        EnvironmentVariableName = Get-EffectiveEnvironmentVariableName -Secrets $secrets -SecretPropertyName 'successFactorsClientIdEnv' -DefaultEnvironmentVariableName 'SF_AD_SYNC_SF_CLIENT_ID'
        Prompt = 'Enter the SuccessFactors OAuth client id'
        Secure = $false
    },
    @{
        Name = 'SuccessFactors client secret'
        PropertyPath = 'successFactors.oauth.clientSecret'
        EnvironmentVariableName = Get-EffectiveEnvironmentVariableName -Secrets $secrets -SecretPropertyName 'successFactorsClientSecretEnv' -DefaultEnvironmentVariableName 'SF_AD_SYNC_SF_CLIENT_SECRET'
        Prompt = 'Enter the SuccessFactors OAuth client secret'
        Secure = $true
    },
    @{
        Name = 'AD default password'
        PropertyPath = 'ad.defaultPassword'
        EnvironmentVariableName = Get-EffectiveEnvironmentVariableName -Secrets $secrets -SecretPropertyName 'defaultAdPasswordEnv' -DefaultEnvironmentVariableName 'SF_AD_SYNC_AD_DEFAULT_PASSWORD'
        Prompt = 'Enter the default AD password for newly created users'
        Secure = $true
    }
)

$hostIsDomainJoined = $false
try {
    $computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
    $hostIsDomainJoined = [bool]$computerSystem.PartOfDomain
} catch {
    $hostIsDomainJoined = $false
}

$explicitAdBindingRequested = (
    -not [string]::IsNullOrWhiteSpace("$(Get-OptionalPropertyValue -InputObject $adConfig -PropertyPath 'server')") -or
    -not [string]::IsNullOrWhiteSpace("$(Get-OptionalPropertyValue -InputObject $adConfig -PropertyPath 'username')") -or
    -not [string]::IsNullOrWhiteSpace("$(Get-OptionalPropertyValue -InputObject $adConfig -PropertyPath 'bindPassword')") -or
    -not [string]::IsNullOrWhiteSpace([System.Environment]::GetEnvironmentVariable((Get-EffectiveEnvironmentVariableName -Secrets $secrets -SecretPropertyName 'adServerEnv' -DefaultEnvironmentVariableName 'SF_AD_SYNC_AD_SERVER'))) -or
    -not [string]::IsNullOrWhiteSpace([System.Environment]::GetEnvironmentVariable((Get-EffectiveEnvironmentVariableName -Secrets $secrets -SecretPropertyName 'adUsernameEnv' -DefaultEnvironmentVariableName 'SF_AD_SYNC_AD_USERNAME'))) -or
    -not [string]::IsNullOrWhiteSpace([System.Environment]::GetEnvironmentVariable((Get-EffectiveEnvironmentVariableName -Secrets $secrets -SecretPropertyName 'adBindPasswordEnv' -DefaultEnvironmentVariableName 'SF_AD_SYNC_AD_BIND_PASSWORD')))
)

if (-not $hostIsDomainJoined -or $explicitAdBindingRequested) {
    $runtimePrompts += @(
        @{
            Name = 'AD server'
            PropertyPath = 'ad.server'
            EnvironmentVariableName = Get-EffectiveEnvironmentVariableName -Secrets $secrets -SecretPropertyName 'adServerEnv' -DefaultEnvironmentVariableName 'SF_AD_SYNC_AD_SERVER'
            Prompt = 'Enter the AD server or domain controller hostname'
            Secure = $false
        },
        @{
            Name = 'AD bind username'
            PropertyPath = 'ad.username'
            EnvironmentVariableName = Get-EffectiveEnvironmentVariableName -Secrets $secrets -SecretPropertyName 'adUsernameEnv' -DefaultEnvironmentVariableName 'SF_AD_SYNC_AD_USERNAME'
            Prompt = 'Enter the AD bind username'
            Secure = $false
        },
        @{
            Name = 'AD bind password'
            PropertyPath = 'ad.bindPassword'
            EnvironmentVariableName = Get-EffectiveEnvironmentVariableName -Secrets $secrets -SecretPropertyName 'adBindPasswordEnv' -DefaultEnvironmentVariableName 'SF_AD_SYNC_AD_BIND_PASSWORD'
            Prompt = 'Enter the AD bind password'
            Secure = $true
        }
    )
}

foreach ($promptDefinition in $runtimePrompts) {
    $environmentVariableName = $promptDefinition.EnvironmentVariableName
    $currentValue = [System.Environment]::GetEnvironmentVariable($environmentVariableName)
    if (Test-NeedsRuntimePrompt -Value $currentValue) {
        $currentValue = Get-OptionalPropertyValue -InputObject $config -PropertyPath $promptDefinition.PropertyPath
    }

    if (-not (Test-NeedsRuntimePrompt -Value $currentValue)) {
        continue
    }

    $value = Read-RequiredPromptValue -Prompt $promptDefinition.Prompt -AsSecureString:([bool]$promptDefinition.Secure)
    [System.Environment]::SetEnvironmentVariable($environmentVariableName, $value, 'Process')
}

$invokePath = Join-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -ChildPath 'src/Invoke-SfAdSync.ps1'
& $invokePath -ConfigPath $resolvedConfigPath -MappingConfigPath $MappingConfigPath -Mode $Mode -DryRun:$DryRun -WorkerId $WorkerId
