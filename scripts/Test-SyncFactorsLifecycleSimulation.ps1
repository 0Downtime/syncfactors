[CmdletBinding()]
param(
    [string]$ScenarioPath,
    [string]$FixturePath,
    [string]$ReportPath,
    [switch]$ExpectedFailure,
    [ValidateSet('single', 'multi', 'population', 'failure')]
    [string]$Sample = 'population',
    [int]$Iterations,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

$projectRoot = Resolve-ProjectRoot
$mockProjectPath = Join-Path $projectRoot 'src/SyncFactors.MockSuccessFactors/SyncFactors.MockSuccessFactors.csproj'

if ([string]::IsNullOrWhiteSpace($ScenarioPath)) {
    $scenarioFile = if ($ExpectedFailure) {
        'sample-lifecycle-expected-failure.json'
    }
    elseif ($Sample -eq 'single') {
        'sample-lifecycle-scenario.json'
    }
    elseif ($Sample -eq 'multi') {
        'sample-lifecycle-multiuser-scenario.json'
    }
    elseif ($Sample -eq 'failure') {
        'sample-lifecycle-failure-scenario.json'
    }
    else {
        'sample-lifecycle-population-scenario.json'
    }
    $ScenarioPath = Join-Path $projectRoot "config/mock-successfactors/$scenarioFile"
}

if ([string]::IsNullOrWhiteSpace($FixturePath)) {
    $fixtureFile = if ($ExpectedFailure -or $Sample -eq 'single') {
        'sample-lifecycle-fixtures.json'
    }
    elseif ($Sample -eq 'failure') {
        'sample-lifecycle-failure-fixtures.json'
    }
    else {
        'sample-lifecycle-multiuser-fixtures.json'
    }
    $FixturePath = Join-Path $projectRoot "config/mock-successfactors/$fixtureFile"
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportName = if ($ExpectedFailure) {
        'lifecycle-simulation-expected-failure-report.md'
    }
    elseif ($Sample -eq 'single') {
        'lifecycle-simulation-report.md'
    }
    elseif ($Sample -eq 'multi') {
        'lifecycle-simulation-multiuser-report.md'
    }
    elseif ($Sample -eq 'failure') {
        'lifecycle-simulation-failure-report.md'
    }
    else {
        'lifecycle-simulation-population-report.md'
    }
    $ReportPath = Join-Path $projectRoot "state/runtime/$reportName"
}

$arguments = @(
    'simulate-lifecycle',
    '--scenario', (Resolve-Path $ScenarioPath).Path,
    '--fixtures', (Resolve-Path $FixturePath).Path,
    '--report', $ReportPath
)

if ($PSBoundParameters.ContainsKey('Iterations') -and $Iterations -gt 0) {
    $arguments += @('--iterations', $Iterations.ToString([System.Globalization.CultureInfo]::InvariantCulture))
}

Write-Host "Running SyncFactors lifecycle simulation" -ForegroundColor Cyan
Write-Host "Scenario: $ScenarioPath"
Write-Host "Fixtures: $FixturePath"
Write-Host "Report: $ReportPath"
Write-Host "Expected failure: $ExpectedFailure"
Write-Host "Sample: $Sample"
Write-Host "Build: $(if ($SkipBuild) { 'skipped' } else { 'enabled' })"

$runnerArguments = @('--no-launch-profile', '--') + $arguments

if (-not $ExpectedFailure) {
    Invoke-DotnetProjectRun -ProjectPath $mockProjectPath -ProjectRoot $projectRoot -SkipBuild:$SkipBuild -Arguments $runnerArguments
    return
}

Push-Location $projectRoot
try {
    Initialize-DotnetEnvironment -ProjectRoot $projectRoot

    $dotnetRunArguments = @()
    if ($SkipBuild) {
        $dotnetRunArguments += '--no-build'
    }
    else {
        Invoke-SolutionBuild -ProjectRoot $projectRoot
        $dotnetRunArguments += '--no-restore'
    }

    $dotnetRunArguments += @('--project', $mockProjectPath)
    $dotnetRunArguments += $runnerArguments

    & dotnet run @dotnetRunArguments
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($exitCode -eq 0) {
    throw "Expected the lifecycle simulation to fail, but it succeeded."
}

Write-Host "Expected failure observed (exit code $exitCode)." -ForegroundColor Yellow
