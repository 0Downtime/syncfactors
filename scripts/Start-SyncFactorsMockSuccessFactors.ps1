[CmdletBinding()]
param(
    [string]$Urls = 'http://127.0.0.1:18080',
    [switch]$SkipBuild
)

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

$projectRoot = Resolve-ProjectRoot
$mockProjectPath = Join-Path $projectRoot 'src/SyncFactors.MockSuccessFactors/SyncFactors.MockSuccessFactors.csproj'
$fixturePath = Join-Path $projectRoot 'config/mock-successfactors/baseline-fixtures.json'
$syntheticPopulationEnabled = if ([string]::IsNullOrWhiteSpace($env:MOCK_SF_SYNTHETIC_POPULATION_ENABLED)) { 'true' } else { $env:MOCK_SF_SYNTHETIC_POPULATION_ENABLED }
$targetWorkerCount = if ([string]::IsNullOrWhiteSpace($env:MOCK_SF_TARGET_WORKER_COUNT)) { '1000' } else { $env:MOCK_SF_TARGET_WORKER_COUNT }

$env:ASPNETCORE_URLS = $Urls
$env:MockSuccessFactors__SyntheticPopulation__Enabled = $syntheticPopulationEnabled
$env:MockSuccessFactors__SyntheticPopulation__TargetWorkerCount = $targetWorkerCount
Set-StandardLoggingEnvironment -DefaultLevel 'Information' -Overrides @{
    'Logging__LogLevel__Microsoft_AspNetCore' = 'Warning'
}

Write-Host "Starting SyncFactors Mock SuccessFactors API" -ForegroundColor Cyan
Write-Host "URL: $Urls"
Write-Host "Fixtures: $fixturePath"
Write-Host "Synthetic population: $syntheticPopulationEnabled"
Write-Host "Target worker count: $targetWorkerCount"
Write-Host "Build: $(if ($SkipBuild) { 'skipped' } else { 'enabled' })"

Invoke-DotnetProjectRun -ProjectPath $mockProjectPath -ProjectRoot $projectRoot -SkipBuild:$SkipBuild -Arguments @('--no-launch-profile')
