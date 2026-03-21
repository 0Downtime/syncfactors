[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [Parameter(Mandatory)]
    [string]$MappingConfigPath,
    [string]$OutputDirectory,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Path $PSScriptRoot -Parent
$moduleRoot = Join-Path -Path $projectRoot -ChildPath 'src/Modules/SyncFactors'
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking

$resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
$resolvedMappingConfigPath = (Resolve-Path -Path $MappingConfigPath).Path
$effectiveConfigPath = $resolvedConfigPath

if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $config = Get-SyncFactorsConfig -Path $resolvedConfigPath
    $config.reporting.reviewOutputDirectory = (Resolve-Path -Path (New-Item -Path $OutputDirectory -ItemType Directory -Force)).Path
    $overlayPath = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ("syncfactors-review-config-{0}.json" -f ([guid]::NewGuid().Guid))
    try {
        $config | ConvertTo-Json -Depth 30 | Set-Content -Path $overlayPath
        $effectiveConfigPath = $overlayPath
    } catch {
        if (Test-Path -Path $overlayPath -PathType Leaf) {
            Remove-Item -Path $overlayPath -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

$invokePath = Join-Path -Path $projectRoot -ChildPath 'src/Invoke-SyncFactors.ps1'
$reportPath = & $invokePath -ConfigPath $effectiveConfigPath -MappingConfigPath $resolvedMappingConfigPath -Mode Review
$report = Get-Content -Path $reportPath -Raw | ConvertFrom-Json -Depth 30

$result = [pscustomobject]@{
    reportPath = $reportPath
    runId = $report.runId
    mode = $report.mode
    status = $report.status
    reviewSummary = $report.reviewSummary
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 20
    return
}

Write-Host 'SuccessFactors First Sync Review'
Write-Host "Report: $($result.reportPath)"
Write-Host "Run ID: $($result.runId)"
Write-Host "Status: $($result.status)"
if ($result.reviewSummary) {
    Write-Host "Existing matched: $($result.reviewSummary.existingUsersMatched)"
    Write-Host "Existing with changes: $($result.reviewSummary.existingUsersWithAttributeChanges)"
    Write-Host "Creates: $($result.reviewSummary.proposedCreates)"
    Write-Host "Offboarding: $($result.reviewSummary.proposedOffboarding)"
    Write-Host "Quarantined: $($result.reviewSummary.quarantined)"
    Write-Host "Conflicts: $($result.reviewSummary.conflicts)"
}
