[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputPath,
    [Parameter(Mandatory)]
    [string]$Version,
    [string]$RepoRoot,
    [string]$ApiPublishPath,
    [string]$WorkerPublishPath,
    [string]$CommitSha
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Path $PSScriptRoot -Parent
}

$resolvedRepoRoot = (Resolve-Path -Path $RepoRoot).ProviderPath
if (-not $ApiPublishPath) {
    $ApiPublishPath = Join-Path $resolvedRepoRoot 'artifacts/publish/api'
}

if (-not $WorkerPublishPath) {
    $WorkerPublishPath = Join-Path $resolvedRepoRoot 'artifacts/publish/worker'
}

$resolvedApiPublishPath = (Resolve-Path -Path $ApiPublishPath).ProviderPath
$resolvedWorkerPublishPath = (Resolve-Path -Path $WorkerPublishPath).ProviderPath
if (-not $CommitSha) {
    $CommitSha = (git -C $resolvedRepoRoot rev-parse HEAD).Trim()
}

$includedRepoPaths = @(
    'config/sample.codex-run.json',
    'config/sample.empjob-confirmed.mapping-config.json',
    'config/sample.mock-successfactors.real-ad.sync-config.json',
    'config/sample.real-successfactors.real-ad.sync-config.json',
    'config/mock-successfactors',
    'docs',
    'scripts',
    'README.md',
    'LICENSE',
    'SECURITY.md',
    'CONTRIBUTING.md',
    'VERSION'
)

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("syncfactors-release-{0}" -f [System.Guid]::NewGuid().ToString('N'))
$outputDirectory = Split-Path -Path $OutputPath -Parent
if ($outputDirectory -and -not (Test-Path -Path $outputDirectory -PathType Container)) {
    New-Item -Path $outputDirectory -ItemType Directory -Force | Out-Null
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)

Add-Type -AssemblyName System.IO.Compression.FileSystem

try {
    New-Item -Path $stagingRoot -ItemType Directory -Force | Out-Null
    New-Item -Path (Join-Path $stagingRoot 'app') -ItemType Directory -Force | Out-Null

    Copy-Item -Path $resolvedApiPublishPath -Destination (Join-Path $stagingRoot 'app/api') -Recurse -Force
    Copy-Item -Path $resolvedWorkerPublishPath -Destination (Join-Path $stagingRoot 'app/worker') -Recurse -Force

    foreach ($item in $includedRepoPaths) {
        $sourcePath = Join-Path $resolvedRepoRoot $item
        if (-not (Test-Path -Path $sourcePath)) {
            throw "Release bundle item '$item' was not found at '$sourcePath'."
        }

        $destinationPath = Join-Path $stagingRoot $item
        $destinationParent = Split-Path -Path $destinationPath -Parent
        if ($destinationParent -and -not (Test-Path -Path $destinationParent -PathType Container)) {
            New-Item -Path $destinationParent -ItemType Directory -Force | Out-Null
        }

        Copy-Item -Path $sourcePath -Destination $destinationPath -Recurse -Force
    }

    $manifest = [ordered]@{
        name = 'SyncFactors'
        version = $Version
        commitSha = "$CommitSha".Trim().ToLowerInvariant()
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        applications = @(
            [ordered]@{ name = 'SyncFactors.Api'; path = 'app/api/SyncFactors.Api.dll' },
            [ordered]@{ name = 'SyncFactors.Worker'; path = 'app/worker/SyncFactors.Worker.dll' }
        )
        includedPaths = @('app/api', 'app/worker') + $includedRepoPaths
    }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $stagingRoot 'release-manifest.json') -Encoding UTF8

    if (Test-Path -Path $resolvedOutputPath -PathType Leaf) {
        Remove-Item -Path $resolvedOutputPath -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory($stagingRoot, $resolvedOutputPath)
}
finally {
    if (Test-Path -Path $stagingRoot -PathType Container) {
        Remove-Item -Path $stagingRoot -Recurse -Force
    }
}

[pscustomobject]@{
    bundlePath = $resolvedOutputPath
    includedPaths = @('app/api', 'app/worker') + $includedRepoPaths
}
