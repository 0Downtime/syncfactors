[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [Parameter(Mandatory)]
    [string]$MappingConfigPath,
    [ValidateSet('Delta','Full','Review')]
    [string]$Mode = 'Delta',
    [switch]$DryRun,
    [string]$WorkerId,
    [switch]$BypassApprovalMode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleRoot = Join-Path -Path $PSScriptRoot -ChildPath 'Modules/SyncFactors'
Import-Module (Join-Path $moduleRoot 'Sync.psm1') -Force -DisableNameChecking

Invoke-SyncFactorsRun -ConfigPath $ConfigPath -MappingConfigPath $MappingConfigPath -Mode $Mode -DryRun:$DryRun -WorkerId $WorkerId -BypassApprovalMode:$BypassApprovalMode
