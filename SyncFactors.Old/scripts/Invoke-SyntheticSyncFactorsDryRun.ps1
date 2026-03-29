[CmdletBinding()]
param(
    [string]$ConfigPath = '../config/sample.real-successfactors.real-ad.sync-config.json',
    [string]$MappingConfigPath = '../config/sample.empjob-confirmed.mapping-config.json',
    [ValidateRange(1, 50000)]
    [int]$UserCount = 1000,
    [ValidateRange(0, 5000)]
    [int]$InactiveCount = 0,
    [ValidateRange(0, 5000)]
    [int]$DuplicateWorkerIdCount = 0,
    [ValidateRange(1, 5000)]
    [int]$ManagerCount = 50,
    [ValidateRange(0, 5000)]
    [int]$ExistingUpnCollisionCount = 0,
    [ValidateRange(0, 50000)]
    [int]$MaxCreatesPerRun = 0,
    [string]$OutputDirectory = './reports/synthetic',
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Path $PSScriptRoot -Parent
$moduleRoot = Join-Path -Path $projectRoot -ChildPath 'src/Modules/SyncFactors'
$bundledPesterManifest = Join-Path $projectRoot '.tools/Pester/5.7.1/Pester.psd1'

if (-not (Get-Command Invoke-Pester -ErrorAction SilentlyContinue)) {
    if (Test-Path -Path $bundledPesterManifest -PathType Leaf) {
        Import-Module $bundledPesterManifest -Force
    } else {
        $installedPester = Get-Module -ListAvailable Pester | Sort-Object Version -Descending | Select-Object -First 1
        if (-not $installedPester) {
            throw 'Pester is not installed and no bundled copy was found.'
        }

        Import-Module $installedPester.Path -Force
    }
}

Import-Module (Join-Path $moduleRoot 'SyntheticHarness.psm1') -Force

function Get-ResolvedPathOrJoin {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path -Path $BasePath -ChildPath $Path
}

$resolvedConfigPath = (Resolve-Path -Path (Get-ResolvedPathOrJoin -Path $ConfigPath -BasePath $projectRoot)).Path
$resolvedMappingConfigPath = (Resolve-Path -Path (Get-ResolvedPathOrJoin -Path $MappingConfigPath -BasePath $projectRoot)).Path
$resolvedOutputDirectory = Get-ResolvedPathOrJoin -Path $OutputDirectory -BasePath $projectRoot

if (-not (Test-Path -Path $resolvedOutputDirectory -PathType Container)) {
    New-Item -Path $resolvedOutputDirectory -ItemType Directory -Force | Out-Null
}

$syntheticDirectory = New-SyncFactorsSyntheticWorkers -UserCount $UserCount -InactiveCount $InactiveCount -DuplicateWorkerIdCount $DuplicateWorkerIdCount -ManagerCount $ManagerCount
$workers = @($syntheticDirectory.workers)
$managerDirectory = @($syntheticDirectory.managers)
$collisionUpns = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($worker in $workers | Select-Object -First $ExistingUpnCollisionCount) {
    [void]$collisionUpns.Add($worker.email)
}

$config = Get-Content -Path $resolvedConfigPath -Raw | ConvertFrom-Json -Depth 20
$effectiveCreateGuardrail = if ($MaxCreatesPerRun -gt 0) { $MaxCreatesPerRun } else { [math]::Max($UserCount + 50, 100) }

if (-not $config.PSObject.Properties.Name.Contains('safety')) {
    $config | Add-Member -MemberType NoteProperty -Name safety -Value ([pscustomobject]@{}) -Force
}

$config.safety.maxCreatesPerRun = $effectiveCreateGuardrail
$config.reporting.outputDirectory = $resolvedOutputDirectory
$config.state.path = Join-Path -Path $resolvedOutputDirectory -ChildPath 'synthetic-state.json'

$tempConfigPath = Join-Path -Path $resolvedOutputDirectory -ChildPath 'synthetic.sync-config.json'
$config | ConvertTo-Json -Depth 20 | Set-Content -Path $tempConfigPath

$workersPath = Join-Path -Path $resolvedOutputDirectory -ChildPath 'synthetic-workers.json'
$managerDirectoryPath = Join-Path -Path $resolvedOutputDirectory -ChildPath 'synthetic-managers.json'
$collisionUpnsPath = Join-Path -Path $resolvedOutputDirectory -ChildPath 'synthetic-collision-upns.json'
$reportPathFile = Join-Path -Path $resolvedOutputDirectory -ChildPath 'synthetic-report-path.txt'
$pesterHarnessPath = Join-Path -Path $resolvedOutputDirectory -ChildPath 'SyntheticDryRunHarness.Tests.ps1'

ConvertTo-Json -InputObject $workers -Depth 20 | Set-Content -Path $workersPath
ConvertTo-Json -InputObject $managerDirectory -Depth 10 | Set-Content -Path $managerDirectoryPath
ConvertTo-Json -InputObject @($collisionUpns) -Depth 5 | Set-Content -Path $collisionUpnsPath

$pesterHarness = @"
Describe 'Synthetic dry-run harness' {
    BeforeAll {
        Import-Module '$moduleRoot/Sync.psm1' -Force -DisableNameChecking
        `$script:SyntheticWorkers = @(Get-Content -Path '$workersPath' -Raw | ConvertFrom-Json -Depth 20)
        `$script:SyntheticManagers = @(Get-Content -Path '$managerDirectoryPath' -Raw | ConvertFrom-Json -Depth 10)
        `$script:CollisionEmails = @(Get-Content -Path '$collisionUpnsPath' -Raw | ConvertFrom-Json -Depth 5)
    }

    It 'runs the synthetic sync dry-run' {
        InModuleScope Sync {
            param(
                [string]`$HarnessConfigPath,
                [string]`$HarnessMappingConfigPath,
                [object[]]`$HarnessWorkers,
                [object[]]`$HarnessManagers,
                [string[]]`$HarnessCollisionEmails,
                [string]`$HarnessReportPathFile
            )

            Mock Get-SfWorkers { `$HarnessWorkers }
            Mock Get-SyncFactorsTargetUser {
                param([pscustomobject]`$Config, [string]`$WorkerId)

                `$managerRecord = @(`$HarnessManagers | Where-Object { `$_.employeeId -eq `$WorkerId })
                if (`$managerRecord.Count -gt 0) {
                    return [pscustomobject]@{
                        SamAccountName = `$managerRecord[0].samAccountName
                        DistinguishedName = `$managerRecord[0].distinguishedName
                    }
                }

                return `$null
            }
            Mock Get-SyncFactorsUserBySamAccountName { `$null }
            Mock Get-SyncFactorsUserByUserPrincipalName {
                param([string]`$UserPrincipalName)

                if (`$HarnessCollisionEmails -contains `$UserPrincipalName) {
                    return [pscustomobject]@{
                        SamAccountName = 'existing-user'
                        UserPrincipalName = `$UserPrincipalName
                    }
                }

                return `$null
            }
            Mock Ensure-ActiveDirectoryModule {}

            `$reportPath = Invoke-SyncFactorsRun -ConfigPath `$HarnessConfigPath -MappingConfigPath `$HarnessMappingConfigPath -Mode Full -DryRun
            Set-Content -Path `$HarnessReportPathFile -Value `$reportPath
        } -Parameters @{
            HarnessConfigPath = '$tempConfigPath'
            HarnessMappingConfigPath = '$resolvedMappingConfigPath'
            HarnessWorkers = `$script:SyntheticWorkers
            HarnessManagers = `$script:SyntheticManagers
            HarnessCollisionEmails = `$script:CollisionEmails
            HarnessReportPathFile = '$reportPathFile'
        }
    }
}
"@

Set-Content -Path $pesterHarnessPath -Value $pesterHarness
$pesterConfiguration = New-PesterConfiguration
$pesterConfiguration.Run.Path = $pesterHarnessPath
$pesterConfiguration.Run.PassThru = $true
$pesterConfiguration.TestRegistry.Enabled = $false
$pesterResult = Invoke-Pester -Configuration $pesterConfiguration
if ($pesterResult.FailedCount -gt 0) {
    throw 'Synthetic dry-run harness failed.'
}

$reportPath = (Get-Content -Path $reportPathFile -Raw).Trim()
$legacyReportPath = if (Test-Path -Path $reportPath -PathType Leaf) { $reportPath } else { $null }
$report = $null
if ($legacyReportPath) {
    $report = Get-Content -Path $legacyReportPath -Raw | ConvertFrom-Json -Depth 30
} else {
$tempConfig = Get-Content -Path $tempConfigPath -Raw | ConvertFrom-Json -Depth 30
$runId = if ($reportPath.StartsWith('run:')) { $reportPath.Substring(4) } else { $null }
if ([string]::IsNullOrWhiteSpace($runId)) {
    throw "Synthetic dry-run expected a SQLite run reference but received '$reportPath'."
}
$sqlitePath = Join-Path -Path (Split-Path -Path $tempConfig.state.path -Parent) -ChildPath 'syncfactors.db'
$reportJson = sqlite3 $sqlitePath "SELECT report_json FROM runs WHERE run_id = '$($runId.Replace("'", "''"))' LIMIT 1;"
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($reportJson -join ''))) {
    throw "Synthetic dry-run report '$runId' was not found in SQLite."
}
$report = (($reportJson -join [Environment]::NewLine) | ConvertFrom-Json -Depth 30)
}
$summary = [pscustomobject]@{
    userCount = $UserCount
    inactiveCount = $InactiveCount
    duplicateWorkerIdCount = $DuplicateWorkerIdCount
    managerCount = $ManagerCount
    existingUpnCollisionCount = $ExistingUpnCollisionCount
    maxCreatesPerRun = $effectiveCreateGuardrail
    reportPath = $reportPath
    status = $report.status
    creates = @($report.creates).Count
    updates = @($report.updates).Count
    disables = @($report.disables).Count
    deletions = @($report.deletions).Count
    conflicts = @($report.conflicts).Count
    guardrailFailures = @($report.guardrailFailures).Count
    quarantined = @($report.quarantined).Count
    unchanged = @($report.unchanged).Count
}

if ($AsJson) {
    $summary | ConvertTo-Json -Depth 10
    return
}

Write-Host 'SuccessFactors Synthetic Dry-Run Summary'
Write-Host "Users generated: $($summary.userCount)"
Write-Host "Inactive users: $($summary.inactiveCount)"
Write-Host "Duplicate worker IDs: $($summary.duplicateWorkerIdCount)"
Write-Host "Managers available: $($summary.managerCount)"
Write-Host "Existing UPN collisions: $($summary.existingUpnCollisionCount)"
Write-Host "Max creates per run: $($summary.maxCreatesPerRun)"
Write-Host "Report status: $($summary.status)"
Write-Host "Creates: $($summary.creates)"
Write-Host "Updates: $($summary.updates)"
Write-Host "Disables: $($summary.disables)"
Write-Host "Deletions: $($summary.deletions)"
Write-Host "Conflicts: $($summary.conflicts)"
Write-Host "Guardrail failures: $($summary.guardrailFailures)"
Write-Host "Quarantined: $($summary.quarantined)"
Write-Host "Unchanged: $($summary.unchanged)"
Write-Host "Report: $($summary.reportPath)"
