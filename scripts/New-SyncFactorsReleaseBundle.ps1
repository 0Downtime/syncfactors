[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputPath,
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Path $PSScriptRoot -Parent
}

$resolvedRepoRoot = (Resolve-Path -Path $RepoRoot).ProviderPath
$bundleItems = @(
    'src',
    'scripts',
    'config',
    'README.md',
    'LICENSE',
    'SECURITY.md',
    'CONTRIBUTING.md'
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

    foreach ($item in $bundleItems) {
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

    if (Test-Path -Path $resolvedOutputPath -PathType Leaf) {
        Remove-Item -Path $resolvedOutputPath -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory($stagingRoot, $resolvedOutputPath)
} finally {
    if (Test-Path -Path $stagingRoot -PathType Container) {
        Remove-Item -Path $stagingRoot -Recurse -Force
    }
}

[pscustomobject]@{
    bundlePath = $resolvedOutputPath
    includedPaths = $bundleItems
}
