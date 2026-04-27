[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Prerelease', 'Stable')]
    [string]$Channel,
    [string]$VersionPath,
    [string]$Version,
    [string]$CommitSha,
    [int]$RunNumber
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
if (-not $VersionPath) {
    $VersionPath = Join-Path $repoRoot 'VERSION'
}

if (-not (Test-Path -Path $VersionPath -PathType Leaf)) {
    throw "Version file '$VersionPath' was not found."
}

$baseVersion = (Get-Content -Path $VersionPath -Raw).Trim()
if ($baseVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version file '$VersionPath' must contain a stable SemVer version like 0.1.0."
}

if (-not $CommitSha) {
    if ($env:GITHUB_SHA) {
        $CommitSha = $env:GITHUB_SHA
    }
    else {
        $CommitSha = git rev-parse HEAD
    }
}

$resolvedCommitSha = "$CommitSha".Trim()
if ($resolvedCommitSha -notmatch '^[0-9a-fA-F]{7,40}$') {
    throw "Commit SHA '$resolvedCommitSha' is not a valid Git SHA."
}

$shortShaLength = [Math]::Min(7, $resolvedCommitSha.Length)
$shortSha = $resolvedCommitSha.Substring(0, $shortShaLength).ToLowerInvariant()

switch ($Channel) {
    'Prerelease' {
        $resolvedRunNumber = $RunNumber
        if ($resolvedRunNumber -le 0) {
            if ($env:GITHUB_RUN_NUMBER) {
                $resolvedRunNumber = [int]$env:GITHUB_RUN_NUMBER
            }
            else {
                throw 'RunNumber is required for prerelease version generation.'
            }
        }

        $resolvedVersion = "$baseVersion-dev.$resolvedRunNumber+sha.$shortSha"
        $isPrerelease = $true
    }
    'Stable' {
        $resolvedVersion = if ($Version) { "$Version".Trim() } else { $baseVersion }
        if ($resolvedVersion -notmatch '^\d+\.\d+\.\d+$') {
            throw "Stable release version '$resolvedVersion' must be a SemVer version like 0.1.0."
        }

        if ($resolvedVersion -ne $baseVersion) {
            throw "Stable release version '$resolvedVersion' must match the VERSION file value '$baseVersion'."
        }

        $isPrerelease = $false
    }
}

[pscustomobject]@{
    channel = $Channel
    baseVersion = $baseVersion
    version = $resolvedVersion
    tag = "v$resolvedVersion"
    isPrerelease = $isPrerelease
    commitSha = $resolvedCommitSha.ToLowerInvariant()
    shortSha = $shortSha
}
