[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipSolutionTests,
    [switch]$SkipSimulationMasterSuite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

$projectRoot = Resolve-ProjectRoot
$solutionPath = Join-Path $projectRoot 'SyncFactors.Next.sln'
$mockTestsProjectPath = Join-Path $projectRoot 'tests/SyncFactors.MockSuccessFactors.Tests/SyncFactors.MockSuccessFactors.Tests.csproj'
$simulationMasterFilter = 'FullyQualifiedName~RunAsync_CheckedInSimulationMasterSuite_CoversAllCheckedInScenarios'

Write-Host "Running SyncFactors validation" -ForegroundColor Cyan
Write-Host "Project Root: $projectRoot"
Write-Host "Build: $(if ($SkipBuild) { 'skipped' } else { 'enabled' })"
Write-Host "Solution Tests: $(if ($SkipSolutionTests) { 'skipped' } else { 'enabled' })"
Write-Host "Simulation Master Suite: $(if ($SkipSimulationMasterSuite) { 'skipped' } else { 'enabled' })"

Push-Location $projectRoot
try {
    Initialize-DotnetEnvironment -ProjectRoot $projectRoot

    if (-not $SkipBuild) {
        Write-Host "`n==> Building solution" -ForegroundColor Cyan
        Invoke-SolutionBuild -ProjectRoot $projectRoot
    }

    if (-not $SkipSolutionTests) {
        Write-Host "`n==> Running solution test suite" -ForegroundColor Cyan
        dotnet test $solutionPath --no-build
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet test on the solution failed."
        }
    }

    if (-not $SkipSimulationMasterSuite) {
        Write-Host "`n==> Running lifecycle simulation master suite" -ForegroundColor Cyan
        dotnet test $mockTestsProjectPath --no-build --filter $simulationMasterFilter
        if ($LASTEXITCODE -ne 0) {
            throw "Lifecycle simulation master suite failed."
        }
    }
}
finally {
    Pop-Location
}
