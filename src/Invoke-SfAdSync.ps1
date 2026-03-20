[CmdletBinding(SupportsShouldProcess)]
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

$moduleRoot = Join-Path -Path $PSScriptRoot -ChildPath 'Modules/SfAdSync'
Import-Module (Join-Path $moduleRoot 'Sync.psm1') -Force -DisableNameChecking

Invoke-SfAdSyncRun -ConfigPath $ConfigPath -MappingConfigPath $MappingConfigPath -Mode $Mode -DryRun:$DryRun -WorkerId $WorkerId
