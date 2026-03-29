[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleRoot = Join-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -ChildPath 'src/Modules/SyncFactors'
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'Persistence.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'ActiveDirectorySync.psm1') -Force -DisableNameChecking

$resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
$config = Get-SyncFactorsConfig -Path $resolvedConfigPath
$sqlitePath = Get-SyncFactorsSqlitePath -Config $config

if ([string]::IsNullOrWhiteSpace($sqlitePath) -or -not (Test-Path -Path $sqlitePath -PathType Leaf)) {
    throw "SQLite operational store not found at '$sqlitePath'. Populate it before running verification."
}

$trackedWorkers = @(Get-SyncFactorsTrackedWorkersFromSqlite -StatePath $config.state.path -DatabasePath $sqlitePath)
$results = @()

foreach ($trackedWorker in $trackedWorkers) {
    $workerId = "$($trackedWorker.workerId)"
    $lookupMode = $null
    $adUser = $null

    if (-not [string]::IsNullOrWhiteSpace("$($trackedWorker.adObjectGuid)")) {
        $lookupMode = 'ObjectGuid'
        $adUser = Get-SyncFactorsUserByObjectGuid -Config $config -ObjectGuid "$($trackedWorker.adObjectGuid)"
    }

    if (-not $adUser) {
        $lookupMode = 'WorkerId'
        $adUser = Get-SyncFactorsTargetUser -Config $config -WorkerId $workerId
    }

    $status = 'Match'
    $drift = @()

    if (-not $adUser) {
        $status = 'MissingInAd'
        $drift += 'notFound'
    } else {
        $currentGuid = if ($adUser.PSObject.Properties.Name -contains 'ObjectGuid' -and $adUser.ObjectGuid) { $adUser.ObjectGuid.Guid } else { $null }
        $currentDn = if ($adUser.PSObject.Properties.Name -contains 'DistinguishedName') { "$($adUser.DistinguishedName)" } else { $null }
        $currentEnabled = if ($adUser.PSObject.Properties.Name -contains 'Enabled') { [bool]$adUser.Enabled } else { $null }

        if (-not [string]::IsNullOrWhiteSpace("$($trackedWorker.adObjectGuid)") -and "$currentGuid" -ne "$($trackedWorker.adObjectGuid)") {
            $status = 'Drift'
            $drift += 'objectGuid'
        }

        if (-not [string]::IsNullOrWhiteSpace("$($trackedWorker.distinguishedName)") -and "$currentDn" -ne "$($trackedWorker.distinguishedName)") {
            $status = 'Drift'
            $drift += 'distinguishedName'
        }

        if ($trackedWorker.suppressed -and $currentEnabled -eq $true) {
            $status = 'Drift'
            $drift += 'suppressedButEnabled'
        }

        if (-not $trackedWorker.suppressed -and $currentEnabled -eq $false) {
            $status = 'Drift'
            $drift += 'activeButDisabled'
        }
    }

    $results += [pscustomobject]@{
        workerId = $workerId
        lookupMode = $lookupMode
        status = $status
        drift = @($drift)
        tracked = [pscustomobject]@{
            adObjectGuid = $trackedWorker.adObjectGuid
            distinguishedName = $trackedWorker.distinguishedName
            suppressed = [bool]$trackedWorker.suppressed
            lastSeenStatus = $trackedWorker.lastSeenStatus
        }
        activeDirectory = if ($adUser) {
            [pscustomobject]@{
                objectGuid = if ($adUser.ObjectGuid) { $adUser.ObjectGuid.Guid } else { $null }
                distinguishedName = $adUser.DistinguishedName
                enabled = if ($adUser.PSObject.Properties.Name -contains 'Enabled') { [bool]$adUser.Enabled } else { $null }
                samAccountName = if ($adUser.PSObject.Properties.Name -contains 'SamAccountName') { $adUser.SamAccountName } else { $null }
            }
        } else {
            $null
        }
    }
}

$summary = [pscustomobject]@{
    sqlitePath = $sqlitePath
    totalTrackedWorkers = @($results).Count
    matches = @($results | Where-Object { $_.status -eq 'Match' }).Count
    drifted = @($results | Where-Object { $_.status -eq 'Drift' }).Count
    missingInAd = @($results | Where-Object { $_.status -eq 'MissingInAd' }).Count
}

$output = [pscustomobject]@{
    configPath = $resolvedConfigPath
    summary = $summary
    results = $results
}

if ($AsJson) {
    $output | ConvertTo-Json -Depth 20
    return
}

Write-Host "SQLite path: $($summary.sqlitePath)"
Write-Host "Tracked workers: $($summary.totalTrackedWorkers)"
Write-Host "Matches: $($summary.matches)"
Write-Host "Drifted: $($summary.drifted)"
Write-Host "Missing in AD: $($summary.missingInAd)"

if (@($results).Count -gt 0) {
    Write-Host ''
    foreach ($result in @($results | Where-Object { $_.status -ne 'Match' })) {
        Write-Host ("[{0}] {1} ({2})" -f $result.status, $result.workerId, ($result.drift -join ', '))
    }
}
