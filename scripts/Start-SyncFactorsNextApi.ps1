[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [string]$SqlitePath = 'state/runtime/syncfactors.db',
    [string]$Urls = 'https://127.0.0.1:5087',
    [switch]$SkipBuild
)

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

$projectRoot = Resolve-ProjectRoot
$apiProjectPath = Join-Path $projectRoot 'src/SyncFactors.Api/SyncFactors.Api.csproj'

$resolvedConfigPath = Resolve-RequiredPath -Path $ConfigPath -Label 'Sync config'
$resolvedMappingConfigPath = Resolve-RequiredPath -Path $MappingConfigPath -Label 'Mapping config'

if (-not $SkipBuild) {
    Invoke-FrontendBuild -ProjectRoot $projectRoot
}

Initialize-SyncFactorsHttpsEnvironment -ProjectRoot $projectRoot -Urls $Urls
$env:ASPNETCORE_URLS = $Urls
$env:SyncFactors__ConfigPath = $resolvedConfigPath
$env:SyncFactors__MappingConfigPath = $resolvedMappingConfigPath
$env:SyncFactors__SqlitePath = $SqlitePath
Set-StandardLoggingEnvironment -DefaultLevel 'Information' -Overrides @{
    'Logging__LogLevel__SyncFactors' = 'Debug'
}

Write-Host "Starting SyncFactors API" -ForegroundColor Cyan
Write-Host "URL: $Urls"
Write-Host "Config: $resolvedConfigPath"
Write-Host "Mapping Config: $resolvedMappingConfigPath"
Write-Host "SQLite: $SqlitePath"
Write-Host "Logging: SyncFactors=Debug" -ForegroundColor Cyan
if (Test-SyncFactorsLocalFileLoggingEnabled) {
    Write-Host "Local file logging: $(Get-SyncFactorsLocalLogDirectory -ProjectRoot $projectRoot)" -ForegroundColor Cyan
}
if (-not [string]::IsNullOrWhiteSpace($env:SYNCFACTORS_LAUNCHER_PORTAL_URL)) {
    Write-Host "Portal UI: $($env:SYNCFACTORS_LAUNCHER_PORTAL_URL)" -ForegroundColor Cyan
}
if (-not [string]::IsNullOrWhiteSpace($env:SYNCFACTORS_LAUNCHER_MOCK_ADMIN_URL)) {
    Write-Host "Mock SF Admin UI: $($env:SYNCFACTORS_LAUNCHER_MOCK_ADMIN_URL)" -ForegroundColor Cyan
}

Invoke-DotnetProjectRun -ProjectPath $apiProjectPath -ProjectRoot $projectRoot -SkipBuild:$SkipBuild
