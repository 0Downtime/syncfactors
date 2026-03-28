[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [string]$InstallDirectory,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$CommandName = 'syncfactors',
    [switch]$ShowCurrent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$currentHelperPath = Join-Path -Path $PSScriptRoot -ChildPath 'Set-SyncFactorsTerminalCommandConfig.ps1'
if (-not (Test-Path -Path $currentHelperPath -PathType Leaf)) {
    throw "Terminal command config helper was not found at '$currentHelperPath'."
}

$forwardArguments = @{
    CommandName = $CommandName
}

if ($PSBoundParameters.ContainsKey('ConfigPath')) {
    $forwardArguments['ConfigPath'] = $ConfigPath
}

if ($PSBoundParameters.ContainsKey('MappingConfigPath')) {
    $forwardArguments['MappingConfigPath'] = $MappingConfigPath
}

if ($PSBoundParameters.ContainsKey('InstallDirectory')) {
    $forwardArguments['InstallDirectory'] = $InstallDirectory
}

if ($ShowCurrent) {
    $forwardArguments['ShowCurrent'] = $true
}

& $currentHelperPath @forwardArguments
