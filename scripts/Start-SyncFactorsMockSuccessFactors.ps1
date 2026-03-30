[CmdletBinding()]
param(
    [string]$Urls = 'http://127.0.0.1:18080',
    [switch]$SkipBuild
)

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

$projectRoot = Resolve-ProjectRoot
$mockProjectPath = Join-Path $projectRoot 'src/SyncFactors.MockSuccessFactors/SyncFactors.MockSuccessFactors.csproj'
$fixturePath = Join-Path $projectRoot 'config/mock-successfactors/baseline-fixtures.json'

$env:ASPNETCORE_URLS = $Urls
Set-StandardLoggingEnvironment -DefaultLevel 'Information' -Overrides @{
    'Logging__LogLevel__Microsoft_AspNetCore' = 'Warning'
}

Write-Host "Starting SyncFactors Mock SuccessFactors API" -ForegroundColor Cyan
Write-Host "URL: $Urls"
Write-Host "Fixtures: $fixturePath"
Write-Host "Build: $(if ($SkipBuild) { 'skipped' } else { 'enabled' })"

Invoke-DotnetProjectRun -ProjectPath $mockProjectPath -ProjectRoot $projectRoot -SkipBuild:$SkipBuild -Arguments @('--no-launch-profile')
