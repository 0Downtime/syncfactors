[CmdletBinding()]
param(
    [string]$Path = './tests',
    [switch]$Detailed,
    [switch]$Coverage,
    [string]$CoverageSummaryPath,
    [string]$CoverageReportPath,
    [ValidateSet('JaCoCo', 'CoverageGutters', 'Cobertura')]
    [string]$CoverageReportFormat = 'JaCoCo',
    [string]$TestResultPath,
    [ValidateSet('NUnitXml', 'NUnit2.5', 'NUnit3', 'JUnitXml')]
    [string]$TestResultFormat = 'JUnitXml'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
$coverageBaselinePath = Join-Path $repoRoot 'tests/coverage-baseline.json'
$bundledPesterManifest = Join-Path $repoRoot '.tools/Pester/5.7.1/Pester.psd1'
if (Test-Path -Path $bundledPesterManifest -PathType Leaf) {
    Import-Module $bundledPesterManifest -Force
} else {
    $installedPester = Get-Module -ListAvailable Pester | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $installedPester) {
        throw 'Pester is not installed and no bundled copy was found.'
    }

    Import-Module $installedPester.Path -Force
}

$invokePester = Get-Command Invoke-Pester -ErrorAction Stop
$moduleVersion = if ($invokePester.Version) { [version]$invokePester.Version } else { [version]'0.0' }

if ($moduleVersion.Major -ge 5 -and (Get-Command New-PesterConfiguration -ErrorAction SilentlyContinue)) {
    $configuration = New-PesterConfiguration
    $configuration.Run.Path = $Path
    $configuration.Run.PassThru = $true
    $configuration.Output.Verbosity = if ($Detailed) { 'Detailed' } else { 'Normal' }
    $configuration.TestRegistry.Enabled = $false

    if ($Coverage) {
        $configuration.CodeCoverage.Enabled = $true
        $configuration.CodeCoverage.CoveragePercentTarget = 0
        $configuration.CodeCoverage.Path = @(
            (Join-Path $repoRoot 'src/Modules/SfAdSync'),
            (Join-Path $repoRoot 'src'),
            (Join-Path $repoRoot 'scripts')
        )

        if ($CoverageReportPath) {
            $configuration.CodeCoverage.OutputPath = $CoverageReportPath
            $configuration.CodeCoverage.OutputFormat = $CoverageReportFormat
        }
    }

    if ($TestResultPath) {
        $configuration.TestResult.Enabled = $true
        $configuration.TestResult.OutputPath = $TestResultPath
        $configuration.TestResult.OutputFormat = $TestResultFormat
    }

    $result = Invoke-Pester -Configuration $configuration
} else {
    $parameters = @{
        Path = $Path
    }

    if ($invokePester.Parameters.ContainsKey('PassThru')) {
        $parameters['PassThru'] = $true
    }

    if ($invokePester.Parameters.ContainsKey('Output') -and -not $invokePester.Parameters.ContainsKey('Show')) {
        $parameters['Output'] = if ($Detailed) { 'Detailed' } else { 'Normal' }
    }

    if ($invokePester.Parameters.ContainsKey('Show')) {
        $parameters['Show'] = if ($Detailed) { 'All' } else { 'Fails' }
    }

    if ($Coverage -and $invokePester.Parameters.ContainsKey('CodeCoverage')) {
        $parameters['CodeCoverage'] = @(
            Join-Path $repoRoot 'src/Modules/SfAdSync/*.psm1',
            Join-Path $repoRoot 'src/*.ps1',
            Join-Path $repoRoot 'scripts/*.ps1'
        )
    }

    if ($TestResultPath -and $invokePester.Parameters.ContainsKey('OutputFile')) {
        $parameters['OutputFile'] = $TestResultPath
    }

    if ($TestResultPath -and $invokePester.Parameters.ContainsKey('OutputFormat')) {
        $parameters['OutputFormat'] = $TestResultFormat
    }

    $result = Invoke-Pester @parameters
}

if ($Coverage -and $result -and $result.CodeCoverage) {
    $coverageResult = $result.CodeCoverage
    $coveragePercent = $null
    if ($coverageResult.PSObject.Properties.Name -contains 'CoveragePercent') {
        $coveragePercent = [double]$coverageResult.CoveragePercent
        Write-Host ("Coverage summary: {0:N2}%." -f $coveragePercent)
    } else {
        Write-Host "Coverage summary: $coverageResult"
    }

    $baselinePercent = $null
    if (Test-Path -Path $coverageBaselinePath -PathType Leaf) {
        $baselineConfig = Get-Content -Path $coverageBaselinePath -Raw | ConvertFrom-Json -Depth 10
        if ($baselineConfig.PSObject.Properties.Name -contains 'minimumCoveragePercent') {
            $baselinePercent = [double]$baselineConfig.minimumCoveragePercent
            if ($null -ne $coveragePercent) {
                $coverageDelta = [math]::Round(($coveragePercent - $baselinePercent), 2)
                Write-Host ("Coverage baseline: {0:N2}% ({1:+0.00;-0.00;0.00} points)." -f $baselinePercent, $coverageDelta)
            }
        }
    }

    if ($CoverageSummaryPath) {
        $summaryDirectory = Split-Path -Path $CoverageSummaryPath -Parent
        if ($summaryDirectory -and -not (Test-Path -Path $summaryDirectory -PathType Container)) {
            New-Item -Path $summaryDirectory -ItemType Directory -Force | Out-Null
        }

        [pscustomobject]@{
            generatedAt = (Get-Date).ToString('o')
            coveragePercent = $coveragePercent
            minimumCoveragePercent = $baselinePercent
            testCount = if ($result.PSObject.Properties.Name -contains 'TotalCount') { $result.TotalCount } else { $null }
            passedCount = if ($result.PSObject.Properties.Name -contains 'PassedCount') { $result.PassedCount } else { $null }
            failedCount = if ($result.PSObject.Properties.Name -contains 'FailedCount') { $result.FailedCount } else { $null }
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $CoverageSummaryPath
    }
}

if ($result -and $result.FailedCount -gt 0) {
    exit 1
}
