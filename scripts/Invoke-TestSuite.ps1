[CmdletBinding()]
param(
    [string]$Path = './tests',
    [switch]$Detailed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$output = if ($Detailed) { 'Detailed' } else { 'Normal' }
Invoke-Pester -Path $Path -Output $output
