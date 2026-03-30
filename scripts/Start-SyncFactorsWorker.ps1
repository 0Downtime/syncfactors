[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
}

function Resolve-OptionalPath {
    param(
        [AllowNull()]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Fallback
    )

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        return (Resolve-Path $Path).ProviderPath
    }

    $candidate = Join-Path (Resolve-ProjectRoot) $Fallback
    if (Test-Path $candidate) {
        return (Resolve-Path $candidate).ProviderPath
    }

    return $null
}

function Resolve-OptionalPathFromCandidates {
    param(
        [AllowNull()]
        [string]$Path,
        [Parameter(Mandatory)]
        [string[]]$Fallbacks
    )

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        return (Resolve-Path $Path).ProviderPath
    }

    foreach ($fallback in $Fallbacks) {
        $candidate = Join-Path (Resolve-ProjectRoot) $fallback
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).ProviderPath
        }
    }

    return $null
}

$projectRoot = Resolve-ProjectRoot
$workerProjectPath = Join-Path $projectRoot 'src/SyncFactors.Worker/SyncFactors.Worker.csproj'

$resolvedConfigPath = Resolve-OptionalPathFromCandidates -Path $ConfigPath -Fallbacks @(
    'config/local.mock-successfactors.real-ad.sync-config.json',
    'config/local.real-successfactors.real-ad.sync-config.json'
)
if ($null -eq $resolvedConfigPath) {
    throw "Sync config path could not be resolved. Pass -ConfigPath or create 'config/local.mock-successfactors.real-ad.sync-config.json' or 'config/local.real-successfactors.real-ad.sync-config.json'."
}

$resolvedMappingConfigPath = Resolve-OptionalPath -Path $MappingConfigPath -Fallback 'config/local.syncfactors.mapping-config.json'
if ($null -eq $resolvedMappingConfigPath) {
    throw "Mapping config path could not be resolved. Pass -MappingConfigPath or create 'config/local.syncfactors.mapping-config.json'."
}

$env:SyncFactors__ConfigPath = $resolvedConfigPath
$env:SyncFactors__MappingConfigPath = $resolvedMappingConfigPath
$env:Logging__LogLevel__Default = 'Information'
$env:Logging__LogLevel__SyncFactors = 'Debug'

Write-Host "Starting SyncFactors.Worker" -ForegroundColor Cyan
Write-Host "Config: $resolvedConfigPath"
Write-Host "Mapping Config: $resolvedMappingConfigPath"
Write-Host "Logging: SyncFactors=Debug" -ForegroundColor Cyan

Push-Location $projectRoot
try {
    if (-not $SkipBuild) {
        dotnet build (Join-Path $projectRoot 'SyncFactors.Next.sln')
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed."
        }
    }

    dotnet run --project $workerProjectPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet run failed."
    }
}
finally {
    Pop-Location
}
