[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [string]$SqlitePath = 'state/runtime/syncfactors.db',
    [switch]$SkipBuild
)

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

$projectRoot = Resolve-ProjectRoot
$workerProjectPath = Join-Path $projectRoot 'src/SyncFactors.Worker/SyncFactors.Worker.csproj'

$resolvedConfigPath = Resolve-RequiredPath -Path $ConfigPath -Label 'Sync config'
$resolvedMappingConfigPath = Resolve-RequiredPath -Path $MappingConfigPath -Label 'Mapping config'

$env:SyncFactors__ConfigPath = $resolvedConfigPath
$env:SyncFactors__MappingConfigPath = $resolvedMappingConfigPath
$env:SyncFactors__SqlitePath = $SqlitePath
Set-StandardLoggingEnvironment -DefaultLevel 'Information' -Overrides @{
    'Logging__LogLevel__SyncFactors' = 'Debug'
}

Write-Host "Starting SyncFactors.Worker" -ForegroundColor Cyan
Write-Host "Config: $resolvedConfigPath"
Write-Host "Mapping Config: $resolvedMappingConfigPath"
Write-Host "SQLite: $SqlitePath"
Write-Host "Logging: SyncFactors=Debug" -ForegroundColor Cyan

Invoke-DotnetProjectRun -ProjectPath $workerProjectPath -ProjectRoot $projectRoot -SkipBuild:$SkipBuild
