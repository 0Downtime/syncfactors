[CmdletBinding()]
param(
    [string]$Path = './tests',
    [switch]$Detailed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
$bundledPesterManifest = Join-Path $repoRoot '.tools/Pester/5.7.1/Pester.psd1'
if (Test-Path -Path $bundledPesterManifest -PathType Leaf) {
    Import-Module $bundledPesterManifest -Force
}

$output = if ($Detailed) { 'Detailed' } else { 'Normal' }
Invoke-Pester -Path $Path -Output $output
