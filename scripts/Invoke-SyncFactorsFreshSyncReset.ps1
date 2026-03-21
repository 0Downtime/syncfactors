[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath,
    [string]$LogPath,
    [string]$PreviewReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleRoot = Join-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -ChildPath 'src/Modules/SyncFactors'
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'State.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'ActiveDirectorySync.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'Reporting.psm1') -Force -DisableNameChecking

function Read-SyncFactorsResetConfirmation {
    param(
        [Parameter(Mandatory)]
        [string]$Prompt,
        [Parameter(Mandatory)]
        [string]$ExpectedValue
    )

    $response = Read-Host -Prompt $Prompt
    return "$response".Trim() -ceq $ExpectedValue
}

function Get-SyncFactorsFreshResetLogPath {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $requestedDirectory = Split-Path -Path $RequestedPath -Parent
        if (-not [string]::IsNullOrWhiteSpace($requestedDirectory) -and -not (Test-Path -Path $requestedDirectory -PathType Container)) {
            New-Item -Path $requestedDirectory -ItemType Directory -Force | Out-Null
        }

        return $RequestedPath
    }

    $directory = if (
    $Config.PSObject.Properties.Name -contains 'reporting' -and
    $Config.reporting -and
    $Config.reporting.PSObject.Properties.Name -contains 'outputDirectory' -and
    -not [string]::IsNullOrWhiteSpace("$($Config.reporting.outputDirectory)")
    ) {
        "$($Config.reporting.outputDirectory)"
    } else {
        [System.IO.Path]::GetTempPath()
    }

    if (-not (Test-Path -Path $directory -PathType Container)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    return Join-Path -Path $directory -ChildPath "syncfactors-fresh-reset-$timestamp.log"
}

function Get-SyncFactorsFreshResetPreviewReportPath {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $requestedDirectory = Split-Path -Path $RequestedPath -Parent
        if (-not [string]::IsNullOrWhiteSpace($requestedDirectory) -and -not (Test-Path -Path $requestedDirectory -PathType Container)) {
            New-Item -Path $requestedDirectory -ItemType Directory -Force | Out-Null
        }

        return $RequestedPath
    }

    $directory = if (
        $Config.PSObject.Properties.Name -contains 'reporting' -and
        $Config.reporting -and
        $Config.reporting.PSObject.Properties.Name -contains 'outputDirectory' -and
        -not [string]::IsNullOrWhiteSpace("$($Config.reporting.outputDirectory)")
    ) {
        "$($Config.reporting.outputDirectory)"
    } else {
        [System.IO.Path]::GetTempPath()
    }

    if (-not (Test-Path -Path $directory -PathType Container)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    return Join-Path -Path $directory -ChildPath "syncfactors-ResetPreview-$timestamp.json"
}

function Write-SyncFactorsFreshResetLog {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Message,
        [string]$Level = 'INFO'
    )

    $line = "[{0}][{1}] {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level.ToUpperInvariant(), $Message
    Add-Content -Path $Path -Value $line
    Write-Host $line
}

function Get-SyncFactorsFreshResetUserLabel {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$User
    )

    $samAccountName = if ($User.PSObject.Properties.Name -contains 'SamAccountName' -and -not [string]::IsNullOrWhiteSpace("$($User.SamAccountName)")) {
        "$($User.SamAccountName)"
    } else {
        '(unknown-sam)'
    }
    $distinguishedName = if ($User.PSObject.Properties.Name -contains 'DistinguishedName' -and -not [string]::IsNullOrWhiteSpace("$($User.DistinguishedName)")) {
        "$($User.DistinguishedName)"
    } else {
        '(unknown-dn)'
    }

    return "samAccountName=$samAccountName dn=$distinguishedName"
}

function Get-SyncFactorsFreshResetParentOu {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$User
    )

    if (-not ($User.PSObject.Properties.Name -contains 'DistinguishedName') -or [string]::IsNullOrWhiteSpace("$($User.DistinguishedName)")) {
        return $null
    }

    $parts = "$($User.DistinguishedName)" -split ',', 2
    if ($parts.Count -lt 2) {
        return $null
    }

    return $parts[1]
}

function New-SyncFactorsFreshResetPreviewReport {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config,
        [Parameter(Mandatory)]
        [string]$ResolvedConfigPath,
        [Parameter(Mandatory)]
        [object[]]$Users
    )

    $report = New-SyncFactorsReport -Mode 'Full' -DryRun -ConfigPath $ResolvedConfigPath -MappingConfigPath '' -StatePath $Config.state.path -ArtifactType 'FreshSyncResetPreview'
    foreach ($user in $Users) {
        $samAccountName = if ($user.PSObject.Properties.Name -contains 'SamAccountName' -and -not [string]::IsNullOrWhiteSpace("$($user.SamAccountName)")) {
            "$($user.SamAccountName)"
        } else {
            "(unknown-$([guid]::NewGuid().Guid.Substring(0, 8)))"
        }
        $distinguishedName = if ($user.PSObject.Properties.Name -contains 'DistinguishedName') { "$($user.DistinguishedName)" } else { $null }
        $parentOu = Get-SyncFactorsFreshResetParentOu -User $user
        Add-SyncFactorsReportEntry -Report $report -Bucket 'deletions' -Entry @{
            workerId = $samAccountName
            samAccountName = $samAccountName
            distinguishedName = $distinguishedName
            parentOu = $parentOu
            reason = 'FreshSyncReset'
        }
        Add-SyncFactorsReportOperation -Report $report -OperationType 'DeleteUser' -WorkerId $samAccountName -Bucket 'deletions' -Target @{
            samAccountName = $samAccountName
            distinguishedName = $distinguishedName
        } -Before ([pscustomobject]@{
                samAccountName = $samAccountName
                distinguishedName = $distinguishedName
                parentOu = $parentOu
            }) -After $null -Status 'Preview' | Out-Null
    }

    $report.status = 'Preview'
    $report.reviewSummary = [pscustomobject]@{
        proposedDeletes = @($Users).Count
        managedOus = @(Get-SyncFactorsManagedOus -Config $Config)
    }
    return $report
}

function Save-SyncFactorsFreshResetPreviewReport {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [string]$Path
    )

    $directory = Split-Path -Path $Path -Parent
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -Path $directory -PathType Container)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }

    $Report['completedAt'] = (Get-Date).ToString('o')
    [void]$Report.Remove('operationSequence')
    $Report | ConvertTo-Json -Depth 20 | Set-Content -Path $Path
    return $Path
}

$resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
$config = Get-SyncFactorsConfig -Path $resolvedConfigPath
$logPath = Get-SyncFactorsFreshResetLogPath -Config $config -RequestedPath $LogPath
$previewReportPath = Get-SyncFactorsFreshResetPreviewReportPath -Config $config -RequestedPath $PreviewReportPath
$managedOus = @(Get-SyncFactorsManagedOus -Config $config)
$users = @(Get-SyncFactorsUsersInOrganizationalUnits -Config $config -OrganizationalUnits $managedOus)
$previewReport = New-SyncFactorsFreshResetPreviewReport -Config $config -ResolvedConfigPath $resolvedConfigPath -Users $users
[void](Save-SyncFactorsFreshResetPreviewReport -Report $previewReport -Path $previewReportPath)

Write-SyncFactorsFreshResetLog -Path $logPath -Message 'SuccessFactors Fresh Sync Reset'
Write-SyncFactorsFreshResetLog -Path $logPath -Message "Config: $resolvedConfigPath"
Write-SyncFactorsFreshResetLog -Path $logPath -Message "Log: $logPath"
Write-SyncFactorsFreshResetLog -Path $logPath -Message "Preview report: $previewReportPath"
Write-Host ''
Write-Host 'Managed sync OUs'
foreach ($ou in $managedOus) {
    Write-Host "- $ou"
    Write-SyncFactorsFreshResetLog -Path $logPath -Message "Managed OU: $ou"
}
Write-Host ''
Write-Host "Discovered AD user objects: $($users.Count)"
Write-SyncFactorsFreshResetLog -Path $logPath -Message "Discovered AD user objects: $($users.Count)"
foreach ($user in $users) {
    Write-SyncFactorsFreshResetLog -Path $logPath -Message "Discovered user: $(Get-SyncFactorsFreshResetUserLabel -User $user)"
}
Write-Host "Preview report: $previewReportPath"
Write-Host 'Deletion preview'
foreach ($user in $users | Select-Object -First 10) {
    Write-Host "- $(Get-SyncFactorsFreshResetUserLabel -User $user)"
}
if ($users.Count -gt 10) {
    Write-Host "... $($users.Count - 10) more users in preview report"
}
Write-Host ''
Write-Host 'Warning 1: This permanently deletes AD user objects found recursively under the managed sync OUs above.' -ForegroundColor Yellow
Write-Host 'Warning 2: This is intended for a true fresh sync reset and cannot be undone by a normal sync run.' -ForegroundColor Yellow
Write-Host 'Warning 3: This also resets the local sync state checkpoint and tracked worker state.' -ForegroundColor Yellow
Write-Host ''

if (-not (Read-SyncFactorsResetConfirmation -Prompt 'Type DELETE to continue' -ExpectedValue 'DELETE')) {
    Write-SyncFactorsFreshResetLog -Path $logPath -Message 'Fresh sync reset cancelled at confirmation 1.'
    Write-Host 'Fresh sync reset cancelled at confirmation 1.'
    return
}

if (-not (Read-SyncFactorsResetConfirmation -Prompt "Type $($users.Count) to confirm the discovered AD user count" -ExpectedValue "$($users.Count)")) {
    Write-SyncFactorsFreshResetLog -Path $logPath -Message 'Fresh sync reset cancelled at confirmation 2.'
    Write-Host 'Fresh sync reset cancelled at confirmation 2.'
    return
}

$finalPhrase = 'DELETE ALL SYNCED OU USERS'
if (-not (Read-SyncFactorsResetConfirmation -Prompt "Type '$finalPhrase' to permanently delete the users and reset local sync state" -ExpectedValue $finalPhrase)) {
    Write-SyncFactorsFreshResetLog -Path $logPath -Message 'Fresh sync reset cancelled at confirmation 3.'
    Write-Host 'Fresh sync reset cancelled at confirmation 3.'
    return
}

$deleteFailures = [System.Collections.Generic.List[string]]::new()
foreach ($user in $users) {
    $userLabel = Get-SyncFactorsFreshResetUserLabel -User $user
    try {
        Write-SyncFactorsFreshResetLog -Path $logPath -Message "Deleting user: $userLabel"
        Remove-SyncFactorsUser -Config $config -User $user
        Write-SyncFactorsFreshResetLog -Path $logPath -Message "Deleted user: $userLabel"
    } catch {
        $failureMessage = "Failed to delete user: $userLabel :: $($_.Exception.Message)"
        $deleteFailures.Add($failureMessage)
        Write-SyncFactorsFreshResetLog -Path $logPath -Message $failureMessage -Level 'ERROR'
    }
}

if ($deleteFailures.Count -gt 0) {
    Write-SyncFactorsFreshResetLog -Path $logPath -Message 'Fresh sync reset aborted before state reset because one or more AD deletions failed.' -Level 'ERROR'
    throw "Fresh sync reset failed before state reset. See log: $logPath"
}

$emptyState = [pscustomobject]@{
    checkpoint = $null
    workers = @{}
}
Save-SyncFactorsState -State $emptyState -Path $config.state.path
Write-SyncFactorsFreshResetLog -Path $logPath -Message "Reset sync state: $($config.state.path)"

Write-Host ''
Write-SyncFactorsFreshResetLog -Path $logPath -Message 'Fresh sync reset completed.'
Write-SyncFactorsFreshResetLog -Path $logPath -Message "Deleted AD user objects: $($users.Count)"
Write-Host 'Fresh sync reset completed.'
Write-Host "Deleted AD user objects: $($users.Count)"
Write-Host "Reset sync state: $($config.state.path)"
Write-Host "Log: $logPath"
