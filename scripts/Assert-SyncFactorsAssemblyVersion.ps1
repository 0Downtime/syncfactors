[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AssemblyPath,
    [Parameter(Mandatory)]
    [string]$ExpectedInformationalVersion,
    [Parameter(Mandatory)]
    [string]$ExpectedCommitSha
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -Path $AssemblyPath -PathType Leaf)) {
    throw "Assembly '$AssemblyPath' was not found."
}

$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -Path $AssemblyPath).ProviderPath)
$content = [System.Text.Encoding]::UTF8.GetString($bytes)
$resolvedCommitSha = $ExpectedCommitSha.Trim()
$expectedShortSha = $resolvedCommitSha.Substring(0, [Math]::Min(7, $resolvedCommitSha.Length)).ToLowerInvariant()

if (-not $content.Contains($ExpectedInformationalVersion)) {
    throw "Assembly '$AssemblyPath' does not contain informational version '$ExpectedInformationalVersion'."
}

if (-not $content.Contains($resolvedCommitSha) -and -not $content.Contains($resolvedCommitSha.ToLowerInvariant()) -and -not $content.Contains($expectedShortSha)) {
    throw "Assembly '$AssemblyPath' does not contain commit SHA '$ExpectedCommitSha'."
}

[pscustomobject]@{
    assemblyPath = (Resolve-Path -Path $AssemblyPath).ProviderPath
    informationalVersion = $ExpectedInformationalVersion
    commitSha = $resolvedCommitSha.ToLowerInvariant()
}
