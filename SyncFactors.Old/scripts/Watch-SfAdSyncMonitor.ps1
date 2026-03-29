[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [ValidateRange(1, 3600)]
    [int]$RefreshIntervalSeconds = 60,
    [ValidateRange(1, 1000)]
    [int]$HistoryLimit = 10,
    [switch]$PauseAutoRefresh,
    [switch]$RunOnce,
    [switch]$AsText
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$currentMonitorPath = Join-Path -Path $PSScriptRoot -ChildPath 'Watch-SyncFactorsMonitor.ps1'
if (-not (Test-Path -Path $currentMonitorPath -PathType Leaf)) {
    throw "Dashboard script was not found at '$currentMonitorPath'."
}

$forwardArguments = @{
    ConfigPath = $ConfigPath
    RefreshIntervalSeconds = $RefreshIntervalSeconds
    HistoryLimit = $HistoryLimit
}

if ($PSBoundParameters.ContainsKey('MappingConfigPath')) {
    $forwardArguments['MappingConfigPath'] = $MappingConfigPath
}

if ($PauseAutoRefresh) {
    $forwardArguments['PauseAutoRefresh'] = $true
}

if ($RunOnce) {
    $forwardArguments['RunOnce'] = $true
}

if ($AsText) {
    $forwardArguments['AsText'] = $true
}

& $currentMonitorPath @forwardArguments
