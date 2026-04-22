[CmdletBinding()]
param(
    [string]$Urls = 'http://127.0.0.1:18080',
    [switch]$SkipBuild,
    [switch]$Restart
)

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

$projectRoot = Resolve-ProjectRoot
$mockProjectPath = Join-Path $projectRoot 'src/SyncFactors.MockSuccessFactors/SyncFactors.MockSuccessFactors.csproj'
$fixturePath = Join-Path $projectRoot 'config/mock-successfactors/baseline-fixtures.json'
$syntheticPopulationEnabled = if ([string]::IsNullOrWhiteSpace($env:MOCK_SF_SYNTHETIC_POPULATION_ENABLED)) { 'true' } else { $env:MOCK_SF_SYNTHETIC_POPULATION_ENABLED }
$targetWorkerCount = if ([string]::IsNullOrWhiteSpace($env:MOCK_SF_TARGET_WORKER_COUNT)) { '1000' } else { $env:MOCK_SF_TARGET_WORKER_COUNT }

function Get-PortNumbersFromUrls {
    param(
        [Parameter(Mandatory)]
        [string]$Urls
    )

    $ports = foreach ($url in ($Urls -split ';' | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $uri = $null
        if ([Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri) -and $uri.Port -gt 0) {
            [string]$uri.Port
        }
    }

    return @($ports | Sort-Object -Unique)
}

if ($Restart) {
    $terminalLabel = 'SyncFactors mock API'
    $hostedTerminalProcessIds = @(
        Get-ProcessIdsByCommandFragments -Fragments @('scripts/codex/run.ps1', '-Service', 'mock')
        Get-ProcessIdsByCommandFragments -Fragments @('Start-SyncFactorsMockSuccessFactors.ps1')
    ) | Where-Object { $_ -ne $PID } | Sort-Object -Unique

    Close-HostedTerminals -Labels @($terminalLabel) -HostProcessIds $hostedTerminalProcessIds

    $listeningPorts = Get-PortNumbersFromUrls -Urls $Urls
    $mockPids = @(
        foreach ($port in $listeningPorts) {
            Get-ListeningProcessIds -Port $port
        }

        Get-ProcessIdsByCommandPattern -Patterns @($mockProjectPath, 'SyncFactors.MockSuccessFactors')
        Get-ProcessIdsByCommandFragments -Fragments @('Start-SyncFactorsMockSuccessFactors.ps1')
    ) | Where-Object { $_ -ne $PID } | Sort-Object -Unique

    Stop-LocalProcesses -Name 'SyncFactors mock API' -ProcessIds $mockPids
}

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
Write-Host "Restart previous mock terminals/processes: $(if ($Restart) { 'enabled' } else { 'disabled' })"

Invoke-DotnetProjectRun -ProjectPath $mockProjectPath -ProjectRoot $projectRoot -SkipBuild:$SkipBuild -Arguments @('--no-launch-profile')
