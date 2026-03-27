[CmdletBinding()]
param(
    [string]$Urls = 'http://127.0.0.1:18080',
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
}

$projectRoot = Resolve-ProjectRoot
$mockProjectPath = Join-Path $projectRoot 'src/SyncFactors.MockSuccessFactors/SyncFactors.MockSuccessFactors.csproj'
$fixturePath = Join-Path $projectRoot 'config/mock-successfactors/baseline-fixtures.json'

$env:ASPNETCORE_URLS = $Urls
$env:Logging__LogLevel__Default = 'Information'
$env:Logging__LogLevel__Microsoft_AspNetCore = 'Warning'

Write-Host "Starting SyncFactors Mock SuccessFactors API" -ForegroundColor Cyan
Write-Host "URL: $Urls"
Write-Host "Fixtures: $fixturePath"
Write-Host "Build: $(if ($SkipBuild) { 'skipped' } else { 'enabled' })"

Push-Location $projectRoot
try {
    if (-not $SkipBuild) {
        dotnet build (Join-Path $projectRoot 'SyncFactors.Next.sln')
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed."
        }
    }

    dotnet run --no-launch-profile --project $mockProjectPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet run failed."
    }
}
finally {
    Pop-Location
}
