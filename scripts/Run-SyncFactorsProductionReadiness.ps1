[CmdletBinding()]
param(
    [string[]]$Tags = @(),
    [string]$ReportPath,
    [switch]$StartStack,
    [switch]$AllowAdReset,
    [switch]$IncludeScale,
    [switch]$IncludeRecovery,
    [switch]$IncludeDestructive,
    [switch]$SkipBuild,
    [switch]$SkipUnitTests,
    [int]$TimeoutMinutes = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')
. (Join-Path $PSScriptRoot 'Test-SyncFactorsActiveDirectoryOuAccess.ps1')

$projectRoot = Resolve-ProjectRoot
$worktreeEnvScript = Join-Path $projectRoot 'scripts/codex/Load-WorktreeEnv.ps1'
if (Test-Path $worktreeEnvScript) {
    . $worktreeEnvScript
}

$readinessStarted = Get-Date
$summary = [System.Collections.Generic.List[object]]::new()

function Get-ReadinessFailureHint {
    param([string]$Name)

    if ($Name -like '*stack*') {
        return [pscustomobject]@{ LikelyFailed = 'local API/mock/worker startup'; Check = 'scripts/codex/run.ps1 output, port conflicts, HTTPS cert, profile=mock config.' }
    }

    if ($Name -like '*build*') {
        return [pscustomobject]@{ LikelyFailed = 'compile or restore'; Check = 'dotnet build output and first compiler error.' }
    }

    if ($Name -like '*tests*') {
        return [pscustomobject]@{ LikelyFailed = 'unit/integration regression'; Check = 'failed test name and test project output.' }
    }

    if ($Name -like '*simulator*') {
        return [pscustomobject]@{ LikelyFailed = 'fast lifecycle simulator expectation'; Check = 'state/runtime lifecycle simulation report and mock scenario expectations.' }
    }

    if ($Name -like '*AD OU*' -or $Name -like '*precheck*') {
        return [pscustomobject]@{ LikelyFailed = 'AD bind/search/write access or unsafe OU config'; Check = 'config/local.mock-successfactors.real-ad.sync-config.json, AD credentials, managed OU existence, write permissions.' }
    }

    if ($Name -like '*scenario*') {
        return [pscustomobject]@{ LikelyFailed = 'real API/worker/AD E2E scenario'; Check = 'E2E report, worker logs, run queue request, run detail entries, and AD managed OU snapshot.' }
    }

    return [pscustomobject]@{ LikelyFailed = 'readiness stage dependency'; Check = 'stage output above and generated readiness JSON.' }
}

function Invoke-ReadinessStage {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [scriptblock]$ScriptBlock
    )

    $started = Get-Date
    Write-Host "==> $Name" -ForegroundColor Cyan
    try {
        & $ScriptBlock
        $summary.Add([pscustomobject]@{
            Name = $Name
            Passed = $true
            StartedAt = $started.ToUniversalTime().ToString('O')
            CompletedAt = (Get-Date).ToUniversalTime().ToString('O')
            Error = $null
        })
        Write-Host "PASS: $Name" -ForegroundColor Green
    }
    catch {
        $hint = Get-ReadinessFailureHint -Name $Name
        $summary.Add([pscustomobject]@{
            Name = $Name
            Passed = $false
            StartedAt = $started.ToUniversalTime().ToString('O')
            CompletedAt = (Get-Date).ToUniversalTime().ToString('O')
            Error = [string]$_.Exception.Message
            LikelyFailed = $hint.LikelyFailed
            Check = $hint.Check
        })
        Write-Host "FAIL: $Name - $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "NEXT Likely failed: $($hint.LikelyFailed)" -ForegroundColor Cyan
        Write-Host "NEXT Check: $($hint.Check)" -ForegroundColor Cyan
        throw
    }
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $projectRoot ("state/runtime/automation-reports/production-readiness-{0}.md" -f (Get-Date -Format 'yyyyMMddHHmmss'))
}

$reportDirectory = Split-Path -Parent $ReportPath
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$e2eReportPath = Join-Path $reportDirectory (([IO.Path]::GetFileNameWithoutExtension($ReportPath)) + '-e2e.md')
$jsonReportPath = [IO.Path]::ChangeExtension($ReportPath, '.json')

$effectiveTags = [System.Collections.Generic.List[string]]::new()
if ($Tags.Count -gt 0) {
    foreach ($tag in $Tags) { $effectiveTags.Add($tag) }
}
else {
    foreach ($tag in @('smoke', 'identity', 'routing')) { $effectiveTags.Add($tag) }
}

if ($IncludeScale -and -not $effectiveTags.Contains('scale')) { $effectiveTags.Add('scale') }
if ($IncludeRecovery -and -not $effectiveTags.Contains('recovery')) { $effectiveTags.Add('recovery') }
if ($IncludeDestructive -and -not $effectiveTags.Contains('guardrails')) { $effectiveTags.Add('guardrails') }

$scenarioGlob = Join-Path $projectRoot 'config/automation/*.json'
$configPath = if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS)) {
    Join-Path $projectRoot 'config/local.mock-successfactors.real-ad.sync-config.json'
}
else {
    $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS
}

Write-Host "SyncFactors production-readiness gate" -ForegroundColor Cyan
Write-Host "Project root: $projectRoot"
Write-Host "Scenario glob: $scenarioGlob"
Write-Host "Tags: $($effectiveTags -join ', ')"
Write-Host "Report: $ReportPath"
Write-Host "E2E report: $e2eReportPath"

Push-Location $projectRoot
try {
    Initialize-DotnetEnvironment -ProjectRoot $projectRoot

    if ($StartStack) {
        Invoke-ReadinessStage -Name 'start local mock stack' -ScriptBlock {
            $runScript = Join-Path $projectRoot 'scripts/codex/run.ps1'
            $stackArgs = @('-Service', 'stack', '-Profile', 'mock')
            if ($SkipBuild) {
                $stackArgs += '-SkipBuild'
            }

            & pwsh $runScript @stackArgs
            if ($LASTEXITCODE -ne 0) {
                throw "Local stack startup failed."
            }
        }
    }

    if (-not $SkipBuild) {
        Invoke-ReadinessStage -Name 'solution build' -ScriptBlock {
            Invoke-SolutionBuild -ProjectRoot $projectRoot
        }
    }

    if (-not $SkipUnitTests) {
        Invoke-ReadinessStage -Name 'unit and integration tests' -ScriptBlock {
            dotnet test (Join-Path $projectRoot 'SyncFactors.Next.sln') --no-build
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet test failed."
            }
        }
    }

    Invoke-ReadinessStage -Name 'lifecycle simulator population suite' -ScriptBlock {
        & pwsh (Join-Path $projectRoot 'scripts/Test-SyncFactorsLifecycleSimulation.ps1') -Sample population -SkipBuild
        if ($LASTEXITCODE -ne 0) {
            throw "Lifecycle simulator failed."
        }
    }

    Invoke-ReadinessStage -Name 'config and AD OU precheck' -ScriptBlock {
        Assert-SyncFactorsConfiguredAdOusAccessible -ConfigPath $configPath
    }

    Invoke-ReadinessStage -Name 'real API worker AD scenario suites' -ScriptBlock {
        & (Join-Path $projectRoot 'scripts/Run-SyncFactorsE2EAutomation.ps1') `
            -Scenario $scenarioGlob `
            -ReportPath $e2eReportPath `
            -SkipBuild `
            -TimeoutMinutes $TimeoutMinutes `
            -Tags ($effectiveTags -join ',') `
            -AllowAdReset:$AllowAdReset `
            -IncludeDestructive:$IncludeDestructive `
            -IncludeScale:$IncludeScale `
            -IncludeRecovery:$IncludeRecovery
        if ($LASTEXITCODE -ne 0) {
            throw "Real AD E2E automation failed."
        }
    }
}
finally {
    Pop-Location
}

$passed = -not ($summary | Where-Object { -not $_.Passed })
$readinessReport = [pscustomobject]@{
    StartedAtUtc = $readinessStarted.ToUniversalTime().ToString('O')
    CompletedAtUtc = (Get-Date).ToUniversalTime().ToString('O')
    Passed = $passed
    Tags = $effectiveTags.ToArray()
    E2EReportPath = $e2eReportPath
    Stages = $summary.ToArray()
    RerunCommand = "pwsh ./scripts/Run-SyncFactorsProductionReadiness.ps1 -AllowAdReset -IncludeDestructive"
}

$readinessReport | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonReportPath

$markdown = [System.Text.StringBuilder]::new()
$readinessStatus = if ($passed) { 'PASSED' } else { 'FAILED' }
[void]$markdown.AppendLine('# SyncFactors Production Readiness Report')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine("Result: $readinessStatus")
[void]$markdown.AppendLine("Started: $($readinessReport.StartedAtUtc)")
[void]$markdown.AppendLine("Completed: $($readinessReport.CompletedAtUtc)")
[void]$markdown.AppendLine("Tags: $($effectiveTags -join ', ')")
[void]$markdown.AppendLine('E2E report: `' + $e2eReportPath + '`')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Stages')
foreach ($stage in $summary) {
    $stageStatus = if ($stage.Passed) { 'PASS' } else { 'FAIL' }
    $stageError = if ([string]::IsNullOrWhiteSpace([string]$stage.Error)) { '' } else { ': ' + [string]$stage.Error }
    [void]$markdown.AppendLine(('- {0} {1}{2}' -f $stageStatus, $stage.Name, $stageError))
    if (-not $stage.Passed) {
        [void]$markdown.AppendLine("  likely failed: $($stage.LikelyFailed)")
        [void]$markdown.AppendLine("  check: $($stage.Check)")
    }
}
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Rerun')
[void]$markdown.AppendLine('```powershell')
[void]$markdown.AppendLine($readinessReport.RerunCommand)
[void]$markdown.AppendLine('```')
$markdown.ToString() | Set-Content -Path $ReportPath

$readinessColor = if ($passed) { 'Green' } else { 'Red' }
Write-Host "Production readiness result: $readinessStatus" -ForegroundColor $readinessColor
Write-Host "Markdown Report: $ReportPath"
Write-Host "Json Report: $jsonReportPath"
