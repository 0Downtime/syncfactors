[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleRoot = Join-Path -Path (Split-Path -Path $PSScriptRoot -Parent) -ChildPath 'src/Modules/SfAdSync'
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'State.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'ActiveDirectorySync.psm1') -Force -DisableNameChecking

function Read-SfAdResetConfirmation {
    param(
        [Parameter(Mandatory)]
        [string]$Prompt,
        [Parameter(Mandatory)]
        [string]$ExpectedValue
    )

    $response = Read-Host -Prompt $Prompt
    return "$response".Trim() -ceq $ExpectedValue
}

$resolvedConfigPath = (Resolve-Path -Path $ConfigPath).Path
$config = Get-SfAdSyncConfig -Path $resolvedConfigPath
$managedOus = @(Get-SfAdManagedOus -Config $config)
$users = @(Get-SfAdUsersInOrganizationalUnits -Config $config -OrganizationalUnits $managedOus)

Write-Host 'SuccessFactors Fresh Sync Reset'
Write-Host "Config: $resolvedConfigPath"
Write-Host ''
Write-Host 'Managed sync OUs'
foreach ($ou in $managedOus) {
    Write-Host "- $ou"
}
Write-Host ''
Write-Host "Discovered AD user objects: $($users.Count)"
Write-Host ''
Write-Host 'Warning 1: This permanently deletes AD user objects found recursively under the managed sync OUs above.' -ForegroundColor Yellow
Write-Host 'Warning 2: This is intended for a true fresh sync reset and cannot be undone by a normal sync run.' -ForegroundColor Yellow
Write-Host 'Warning 3: This also resets the local sync state checkpoint and tracked worker state.' -ForegroundColor Yellow
Write-Host ''

if (-not (Read-SfAdResetConfirmation -Prompt 'Type DELETE to continue' -ExpectedValue 'DELETE')) {
    Write-Host 'Fresh sync reset cancelled at confirmation 1.'
    return
}

if (-not (Read-SfAdResetConfirmation -Prompt "Type $($users.Count) to confirm the discovered AD user count" -ExpectedValue "$($users.Count)")) {
    Write-Host 'Fresh sync reset cancelled at confirmation 2.'
    return
}

$finalPhrase = 'DELETE ALL SYNCED OU USERS'
if (-not (Read-SfAdResetConfirmation -Prompt "Type '$finalPhrase' to permanently delete the users and reset local sync state" -ExpectedValue $finalPhrase)) {
    Write-Host 'Fresh sync reset cancelled at confirmation 3.'
    return
}

foreach ($user in $users) {
    Remove-SfAdUser -Config $config -User $user
}

$emptyState = [pscustomobject]@{
    checkpoint = $null
    workers = @{}
}
Save-SfAdSyncState -State $emptyState -Path $config.state.path

Write-Host ''
Write-Host 'Fresh sync reset completed.'
Write-Host "Deleted AD user objects: $($users.Count)"
Write-Host "Reset sync state: $($config.state.path)"
