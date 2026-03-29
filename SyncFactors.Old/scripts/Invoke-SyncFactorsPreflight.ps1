[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [Parameter(Mandatory)]
    [string]$MappingConfigPath,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleRoot = Join-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -ChildPath 'src/Modules/SyncFactors'
Import-Module (Join-Path $moduleRoot 'Sync.psm1') -Force -DisableNameChecking

$result = Test-SyncFactorsPreflight -ConfigPath $ConfigPath -MappingConfigPath $MappingConfigPath

if ($AsJson) {
    $result | ConvertTo-Json -Depth 10
    return
}

Write-Host 'SuccessFactors AD Sync Preflight'
Write-Host "Config: $($result.configPath)"
Write-Host "Mapping config: $($result.mappingConfigPath)"
Write-Host "Identity field: $($result.identityField)"
Write-Host "Identity attribute: $($result.identityAttribute)"
Write-Host "State path: $($result.statePath)"
Write-Host "State directory exists: $($result.stateDirectoryExists)"
Write-Host "Report directory: $($result.reportDirectory)"
Write-Host "Report directory exists: $($result.reportDirectoryExists)"
Write-Host "Mappings: $($result.mappingCount)"
