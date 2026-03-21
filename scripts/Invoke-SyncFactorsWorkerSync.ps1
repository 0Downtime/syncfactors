[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [Parameter(Mandatory)]
    [string]$MappingConfigPath,
    [Parameter(Mandatory)]
    [string]$WorkerId,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Path $PSScriptRoot -Parent
$invokePath = Join-Path -Path $projectRoot -ChildPath 'src/Invoke-SyncFactors.ps1'
$resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
$resolvedMappingConfigPath = (Resolve-Path -Path $MappingConfigPath).Path

$reportPath = & $invokePath -ConfigPath $resolvedConfigPath -MappingConfigPath $resolvedMappingConfigPath -Mode Full -WorkerId $WorkerId
$report = Get-Content -Path $reportPath -Raw | ConvertFrom-Json -Depth 30

$result = [pscustomobject]@{
    reportPath = $reportPath
    runId = $report.runId
    mode = $report.mode
    status = $report.status
    artifactType = $report.artifactType
    workerScope = $report.workerScope
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 20
    return
}

Write-Host 'SuccessFactors Single Worker Sync'
Write-Host "Report: $($result.reportPath)"
Write-Host "Run ID: $($result.runId)"
Write-Host "Status: $($result.status)"
Write-Host "Worker ID: $($result.workerScope.workerId)"
