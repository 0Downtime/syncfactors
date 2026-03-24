[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [string]$Urls = 'http://127.0.0.1:5087',
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

$projectRoot = Resolve-ProjectRoot
$apiProjectPath = Join-Path $projectRoot 'rewrite/SyncFactors.Next/src/SyncFactors.Api/SyncFactors.Api.csproj'

$resolvedConfigPath = Resolve-OptionalPath -Path $ConfigPath -Fallback 'rewrite/SyncFactors.Next/config/local.real-successfactors.real-ad.sync-config.json'
if ($null -eq $resolvedConfigPath) {
    throw "Sync config path could not be resolved. Pass -ConfigPath or create 'rewrite/SyncFactors.Next/config/local.real-successfactors.real-ad.sync-config.json'."
}

$resolvedMappingConfigPath = Resolve-OptionalPath -Path $MappingConfigPath -Fallback 'rewrite/SyncFactors.Next/config/local.syncfactors.mapping-config.json'
if ($null -eq $resolvedMappingConfigPath) {
    throw "Mapping config path could not be resolved. Pass -MappingConfigPath or create 'rewrite/SyncFactors.Next/config/local.syncfactors.mapping-config.json'."
}

$env:ASPNETCORE_URLS = $Urls
$env:SyncFactors__ConfigPath = $resolvedConfigPath
$env:SyncFactors__MappingConfigPath = $resolvedMappingConfigPath

Write-Host "Starting SyncFactors.Next API" -ForegroundColor Cyan
Write-Host "URL: $Urls"
Write-Host "Config: $resolvedConfigPath"
Write-Host "Mapping Config: $resolvedMappingConfigPath"

Push-Location $projectRoot
try {
    if (-not $SkipBuild) {
        dotnet build (Join-Path $projectRoot 'rewrite/SyncFactors.Next/SyncFactors.Next.sln')
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed."
        }
    }

    dotnet run --project $apiProjectPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet run failed."
    }
}
finally {
    Pop-Location
}
