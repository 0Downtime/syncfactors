[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [switch]$SkipState,
    [switch]$SkipRuntimeStatus,
    [switch]$SkipReports,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleRoot = Join-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -ChildPath 'src/Modules/SyncFactors'
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'Persistence.psm1') -Force -DisableNameChecking

$resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
$config = Get-SyncFactorsConfig -Path $resolvedConfigPath
$result = Import-SyncFactorsJsonArtifactsToSqlite -Config $config -SkipState:$SkipState -SkipRuntimeStatus:$SkipRuntimeStatus -SkipReports:$SkipReports

if ($AsJson) {
    $result | ConvertTo-Json -Depth 10
    return
}

Write-Host "SQLite path: $($result.sqlitePath)"
Write-Host "State imported: $($result.stateImported)"
Write-Host "Runtime status imported: $($result.runtimeStatusImported)"
Write-Host "Reports imported: $($result.reportsImported)"
