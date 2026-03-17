[CmdletBinding()]
param(
    [string]$ConfigPath = './config/local.mock-successfactors.real-ad.sync-config.json',
    [ValidateRange(1, 5000)]
    [int]$ManagerCount = 50,
    [string]$TargetOu,
    [switch]$DryRun,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Path $PSScriptRoot -Parent
$moduleRoot = Join-Path -Path $projectRoot -ChildPath 'src/Modules/SfAdSync'

Import-Module (Join-Path $moduleRoot 'SyntheticHarness.psm1') -Force
Import-Module (Join-Path $moduleRoot 'Config.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $moduleRoot 'ActiveDirectorySync.psm1') -Force -DisableNameChecking

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

function ConvertTo-ManagerSeedSecureString {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    return ConvertTo-SecureString -String $Value -AsPlainText -Force
}

function Get-ManagerSeedDirectoryContextParameters {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Config
    )

    $parameters = @{}

    $server = if ($Config.ad.PSObject.Properties.Name -contains 'server') { "$($Config.ad.server)" } else { '' }
    $username = if ($Config.ad.PSObject.Properties.Name -contains 'username') { "$($Config.ad.username)" } else { '' }
    $bindPassword = if ($Config.ad.PSObject.Properties.Name -contains 'bindPassword') { "$($Config.ad.bindPassword)" } else { '' }

    if (-not [string]::IsNullOrWhiteSpace($server)) {
        $parameters['Server'] = $server
    }

    if (-not [string]::IsNullOrWhiteSpace($username)) {
        $securePassword = ConvertTo-ManagerSeedSecureString -Value $bindPassword
        $parameters['Credential'] = [pscredential]::new($username, $securePassword)
    }

    return $parameters
}

$resolvedConfigPath = (Resolve-Path -Path (Get-ResolvedPathOrJoin -Path $ConfigPath -BasePath $projectRoot)).Path
$config = Get-SfAdSyncConfig -Path $resolvedConfigPath

Ensure-ActiveDirectoryModule
$directoryContext = Get-ManagerSeedDirectoryContextParameters -Config $config
$resolvedTargetOu = if ([string]::IsNullOrWhiteSpace($TargetOu)) { $config.ad.defaultActiveOu } else { $TargetOu }
$securePassword = ConvertTo-ManagerSeedSecureString -Value $config.ad.defaultPassword
$managers = @(New-SfAdSyntheticManagerDirectory -ManagerCount $ManagerCount)

$summary = [ordered]@{
    configPath = $resolvedConfigPath
    managerCount = $ManagerCount
    targetOu = $resolvedTargetOu
    dryRun = [bool]$DryRun
    created = @()
    skippedExisting = @()
    conflicts = @()
}

foreach ($manager in $managers) {
    $existingByEmployeeId = @(Get-SfAdTargetUser -Config $config -WorkerId $manager.employeeId)
    if ($existingByEmployeeId.Count -gt 0) {
        $summary.skippedExisting += [pscustomobject]@{
            employeeId = $manager.employeeId
            samAccountName = $manager.samAccountName
            reason = 'EmployeeIdAlreadyExists'
        }
        continue
    }

    $existingBySamAccountName = @(Get-SfAdUserBySamAccountName -Config $config -SamAccountName $manager.samAccountName)
    if ($existingBySamAccountName.Count -gt 0) {
        $summary.conflicts += [pscustomobject]@{
            employeeId = $manager.employeeId
            samAccountName = $manager.samAccountName
            reason = 'SamAccountNameAlreadyExists'
        }
        continue
    }

    $newUserParams = @{
        Name                  = "Manager $($manager.employeeId)"
        SamAccountName        = $manager.samAccountName
        GivenName             = 'Manager'
        Surname               = $manager.employeeId
        DisplayName           = "Manager $($manager.employeeId)"
        Enabled               = $false
        Path                  = $resolvedTargetOu
        AccountPassword       = $securePassword
        ChangePasswordAtLogon = $false
        OtherAttributes       = @{
            employeeID = $manager.employeeId
            company = $manager.company
            department = $manager.department
            description = 'Synthetic mock SuccessFactors manager'
        }
    }

    if ($DryRun) {
        $summary.created += [pscustomobject]@{
            employeeId = $manager.employeeId
            samAccountName = $manager.samAccountName
            targetOu = $resolvedTargetOu
            action = 'WouldCreate'
        }
        continue
    }

    New-ADUser @newUserParams @directoryContext
    $summary.created += [pscustomobject]@{
        employeeId = $manager.employeeId
        samAccountName = $manager.samAccountName
        targetOu = $resolvedTargetOu
        action = 'Created'
    }
}

if ($AsJson) {
    [pscustomobject]$summary | ConvertTo-Json -Depth 10
    return
}

Write-Host 'Mock SuccessFactors Manager Seed Summary'
Write-Host "Config: $($summary.configPath)"
Write-Host "Target OU: $($summary.targetOu)"
Write-Host "Requested managers: $($summary.managerCount)"
Write-Host "Created: $(@($summary.created).Count)"
Write-Host "Skipped existing: $(@($summary.skippedExisting).Count)"
Write-Host "Conflicts: $(@($summary.conflicts).Count)"
