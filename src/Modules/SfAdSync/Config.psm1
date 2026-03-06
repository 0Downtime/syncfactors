Set-StrictMode -Version Latest

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
    if (-not $config.mappings) {
        throw "Mapping config must contain a 'mappings' array."
    }

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

    if (-not $Config.successFactors.baseUrl) {
        throw "Sync config must define successFactors.baseUrl."
    }

    if (-not $Config.ad.graveyardOu) {
        throw "Sync config must define ad.graveyardOu."
    }
}

Export-ModuleMember -Function Get-SfAdSyncConfig, Get-SfAdSyncMappingConfig, Test-SfAdSyncConfig

