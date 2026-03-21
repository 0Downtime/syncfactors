[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [string]$OutputDirectory,
    [string[]]$EntityNames,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

 $projectRoot = Split-Path -Path $PSScriptRoot -Parent
$moduleRoot = Join-Path -Path $projectRoot -ChildPath 'src/Modules/SyncFactors'
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'SuccessFactors.psm1') -Force -DisableNameChecking

$resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
$config = Get-SyncFactorsConfig -Path $resolvedConfigPath

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $defaultOutputRoot = if (
        $config.PSObject.Properties.Name -contains 'reporting' -and
        $config.reporting -and
        $config.reporting.PSObject.Properties.Name -contains 'outputDirectory' -and
        -not [string]::IsNullOrWhiteSpace("$($config.reporting.outputDirectory)")
    ) {
        "$($config.reporting.outputDirectory)"
    } else {
        Join-Path -Path (Split-Path -Path $resolvedConfigPath -Parent) -ChildPath 'reports'
    }

    $OutputDirectory = Join-Path -Path $defaultOutputRoot -ChildPath 'schema'
}

if (-not (Test-Path -Path $OutputDirectory -PathType Container)) {
    New-Item -Path $OutputDirectory -ItemType Directory -Force | Out-Null
}

$export = Get-SfODataSchemaExport -Config $config -EntityNames $EntityNames
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$metadataPath = Join-Path -Path $OutputDirectory -ChildPath "successfactors-metadata-$timestamp.xml"
$summaryPath = Join-Path -Path $OutputDirectory -ChildPath "successfactors-schema-summary-$timestamp.json"

$export.metadataXml | Set-Content -Path $metadataPath -Encoding UTF8

$summary = [pscustomobject]@{
    artifactType     = $export.artifactType
    exportedAt       = $export.exportedAt
    configPath       = $resolvedConfigPath
    outputDirectory  = [System.IO.Path]::GetFullPath($OutputDirectory)
    metadataUri      = $export.metadataUri
    metadataPath     = [System.IO.Path]::GetFullPath($metadataPath)
    summaryPath      = [System.IO.Path]::GetFullPath($summaryPath)
    entitySetName    = $export.entitySetName
    entityTypeName   = $export.entityTypeName
    configuredSelect = $export.configuredSelect
    configuredExpand = $export.configuredExpand
    pathValidations  = $export.pathValidations
    entities         = $export.entities
}

$summary | ConvertTo-Json -Depth 20 | Set-Content -Path $summaryPath -Encoding UTF8

if ($AsJson) {
    $summary | ConvertTo-Json -Depth 20
    return
}

Write-Host 'SuccessFactors Schema Export'
Write-Host "Config: $($summary.configPath)"
Write-Host "Metadata URI: $($summary.metadataUri)"
Write-Host "Metadata XML: $($summary.metadataPath)"
Write-Host "Summary JSON: $($summary.summaryPath)"
Write-Host "Root entity set: $($summary.entitySetName)"
Write-Host "Root entity type: $($summary.entityTypeName)"
Write-Host ''
Write-Host 'Configured path validation:'
foreach ($validation in @($summary.pathValidations)) {
    $status = if ($validation.isValid) { 'OK' } else { 'MISSING' }
    $reason = if ($validation.failureReason) { " [$($validation.failureReason): $($validation.failureSegment)]" } else { '' }
    Write-Host " - [$status] $($validation.pathType) $($validation.path)$reason"
}
Write-Host ''
Write-Host 'Entity summary:'
foreach ($entity in @($summary.entities)) {
    if (-not $entity.exists) {
        Write-Host " - $($entity.name): not present in tenant metadata"
        continue
    }

    Write-Host " - $($entity.name): $($entity.propertyCount) properties, $(@($entity.navigationProperties).Count) nav properties"
}
