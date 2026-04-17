[CmdletBinding()]
param(
    [string]$LocalPath,
    [string]$SamplePath,
    [switch]$AllProfiles,
    [switch]$NoBackup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).ProviderPath

function Read-JsonObject {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        throw "JSON file not found: $Path"
    }

    $raw = Get-Content -Raw -Path $Path
    $parsed = ConvertFrom-Json -InputObject $raw -AsHashtable
    if ($null -eq $parsed) {
        throw "Failed to parse JSON file: $Path"
    }

    return $parsed
}

function Copy-Node {
    param(
        [Parameter(Mandatory)]
        $Value
    )

    if ($Value -is [System.Collections.IDictionary]) {
        $copy = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $copy[$key] = Copy-Node $Value[$key]
        }

        return $copy
    }

    if ($Value -is [System.Collections.IList] -and $Value -isnot [string]) {
        $copy = New-Object System.Collections.ArrayList
        foreach ($item in $Value) {
            [void]$copy.Add((Copy-Node $item))
        }

        return ,$copy.ToArray()
    }

    return $Value
}

function Merge-MissingKeys {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Sample,
        [Parameter(Mandatory)]
        [hashtable]$Local
    )

    foreach ($key in $Sample.Keys) {
        if (-not $Local.ContainsKey($key)) {
            $Local[$key] = Copy-Node $Sample[$key]
            continue
        }

        $sampleValue = $Sample[$key]
        $localValue = $Local[$key]
        if ($sampleValue -is [System.Collections.IDictionary] -and $localValue -is [System.Collections.IDictionary]) {
            Merge-MissingKeys -Sample $sampleValue -Local $localValue
        }
    }
}

function Update-ConfigFile {
    param(
        [Parameter(Mandatory)]
        [string]$SampleConfigPath,
        [Parameter(Mandatory)]
        [string]$LocalConfigPath
    )

    $sample = Read-JsonObject -Path $SampleConfigPath
    $local = Read-JsonObject -Path $LocalConfigPath

    if (-not $NoBackup) {
        $timestamp = Get-Date -Format 'yyyyMMddHHmmss'
        $backupPath = "$LocalConfigPath.$timestamp.bak"
        Copy-Item -Path $LocalConfigPath -Destination $backupPath
        Write-Host "Backed up $(Split-Path -Leaf $LocalConfigPath) -> $(Split-Path -Leaf $backupPath)"
    }

    Merge-MissingKeys -Sample $sample -Local $local

    $json = $local | ConvertTo-Json -Depth 100
    Set-Content -Path $LocalConfigPath -Value $json
    Write-Host "Updated $(Split-Path -Leaf $LocalConfigPath) from $(Split-Path -Leaf $SampleConfigPath)"
}

$defaultPairs = @(
    @{
        Sample = Join-Path $repoRoot 'config/sample.mock-successfactors.real-ad.sync-config.json'
        Local  = Join-Path $repoRoot 'config/local.mock-successfactors.real-ad.sync-config.json'
    },
    @{
        Sample = Join-Path $repoRoot 'config/sample.real-successfactors.real-ad.sync-config.json'
        Local  = Join-Path $repoRoot 'config/local.real-successfactors.real-ad.sync-config.json'
    }
)

if ($AllProfiles) {
    foreach ($pair in $defaultPairs) {
        Update-ConfigFile -SampleConfigPath $pair.Sample -LocalConfigPath $pair.Local
    }

    return
}

if ([string]::IsNullOrWhiteSpace($LocalPath) -or [string]::IsNullOrWhiteSpace($SamplePath)) {
    throw "Pass -AllProfiles, or provide both -LocalPath and -SamplePath."
}

$resolvedLocalPath = if ([System.IO.Path]::IsPathRooted($LocalPath)) { $LocalPath } else { Join-Path $repoRoot $LocalPath }
$resolvedSamplePath = if ([System.IO.Path]::IsPathRooted($SamplePath)) { $SamplePath } else { Join-Path $repoRoot $SamplePath }

Update-ConfigFile -SampleConfigPath $resolvedSamplePath -LocalConfigPath $resolvedLocalPath
