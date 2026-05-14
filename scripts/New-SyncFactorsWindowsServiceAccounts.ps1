[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = 'C:\SyncFactors',
    [string]$RuntimeRoot,
    [string]$DeployAccount,
    [securestring]$DeployAccountPassword,
    [switch]$CreateLocalDeployAccount,
    [string]$RuntimeAccount,
    [securestring]$RuntimeAccountPassword,
    [switch]$CreateLocalRuntimeAccount,
    [string]$PfxPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsWindowsHost {
    return [string]::Equals($env:OS, 'Windows_NT', [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-IsWindowsAdministrator {
    if (-not (Test-IsWindowsHost)) {
        return $false
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-AccountSid {
    param(
        [Parameter(Mandatory)]
        [string]$Identity
    )

    $account = [System.Security.Principal.NTAccount]::new($Identity)
    return $account.Translate([System.Security.Principal.SecurityIdentifier])
}

function Resolve-LocalAccountName {
    param(
        [Parameter(Mandatory)]
        [string]$Identity
    )

    $machineName = $env:COMPUTERNAME
    $normalized = $Identity.Trim()
    if ($normalized.StartsWith('.\')) {
        return $normalized.Substring(2)
    }

    $prefix = "$machineName\"
    if ($normalized.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $normalized.Substring($prefix.Length)
    }

    if ($normalized.Contains('\') -or $normalized.Contains('@')) {
        throw "Only local accounts can be created by this script. Create domain account '$Identity' in IAM/AD first, then run without the matching CreateLocal switch."
    }

    return $normalized
}

function Ensure-LocalAccount {
    param(
        [Parameter(Mandatory)]
        [string]$Identity,
        [Parameter(Mandatory)]
        [securestring]$Password,
        [Parameter(Mandatory)]
        [string]$FullName,
        [Parameter(Mandatory)]
        [string]$Description
    )

    $localName = Resolve-LocalAccountName -Identity $Identity
    $existing = Get-LocalUser -Name $localName -ErrorAction SilentlyContinue
    if ($existing) {
        return ".\$localName"
    }

    New-LocalUser `
        -Name $localName `
        -Password $Password `
        -FullName $FullName `
        -Description $Description `
        -PasswordNeverExpires `
        -UserMayNotChangePassword | Out-Null

    return ".\$localName"
}

function Add-AccountToLocalGroup {
    param(
        [Parameter(Mandatory)]
        [string]$Identity,
        [Parameter(Mandatory)]
        [string]$GroupName
    )

    $group = Get-LocalGroup -Name $GroupName -ErrorAction SilentlyContinue
    if (-not $group) {
        return $false
    }

    $sid = ConvertTo-AccountSid -Identity $Identity
    $existingMember = Get-LocalGroupMember -Group $GroupName -ErrorAction SilentlyContinue |
        Where-Object { $_.SID -eq $sid.Value }
    if ($existingMember) {
        return $true
    }

    Add-LocalGroupMember -Group $GroupName -Member $Identity
    return $true
}

function Grant-LogOnAsServiceRight {
    param(
        [Parameter(Mandatory)]
        [string]$Identity
    )

    $sid = ConvertTo-AccountSid -Identity $Identity
    $sidValue = "*$($sid.Value)"
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("syncfactors-secedit-{0}" -f [System.Guid]::NewGuid().ToString('N'))
    New-Item -Path $tempRoot -ItemType Directory -Force | Out-Null
    $exportPath = Join-Path $tempRoot 'export.inf'
    $importPath = Join-Path $tempRoot 'import.inf'
    $databasePath = Join-Path $tempRoot 'secedit.sdb'

    try {
        & secedit.exe /export /cfg $exportPath /areas USER_RIGHTS | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "secedit export failed with code $LASTEXITCODE."
        }

        $lines = Get-Content -Path $exportPath
        $privilegeSectionIndex = [Array]::IndexOf($lines, '[Privilege Rights]')
        if ($privilegeSectionIndex -lt 0) {
            $lines += ''
            $lines += '[Privilege Rights]'
            $privilegeSectionIndex = $lines.Count - 1
        }

        $rightIndex = -1
        for ($i = $privilegeSectionIndex + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\[') {
                break
            }

            if ($lines[$i] -match '^SeServiceLogonRight\s*=') {
                $rightIndex = $i
                break
            }
        }

        if ($rightIndex -ge 0) {
            $currentValues = @()
            $valueText = ($lines[$rightIndex] -replace '^SeServiceLogonRight\s*=\s*', '').Trim()
            if (-not [string]::IsNullOrWhiteSpace($valueText)) {
                $currentValues = @($valueText -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            }

            if ($currentValues -notcontains $sidValue) {
                $currentValues += $sidValue
                $lines[$rightIndex] = "SeServiceLogonRight = $($currentValues -join ',')"
            }
        }
        else {
            $before = @()
            $after = @()
            if ($privilegeSectionIndex -gt 0) {
                $before = $lines[0..$privilegeSectionIndex]
            }
            else {
                $before = @($lines[0])
            }

            if ($privilegeSectionIndex + 1 -lt $lines.Count) {
                $after = $lines[($privilegeSectionIndex + 1)..($lines.Count - 1)]
            }

            $lines = @($before + "SeServiceLogonRight = $sidValue" + $after)
        }

        Set-Content -Path $importPath -Value $lines -Encoding Unicode
        & secedit.exe /configure /db $databasePath /cfg $importPath /areas USER_RIGHTS | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "secedit configure failed with code $LASTEXITCODE."
        }
    }
    finally {
        Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Grant-PathAccess {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Identity,
        [Parameter(Mandatory)]
        [System.Security.AccessControl.FileSystemRights]$Rights
    )

    if (-not (Test-Path -Path $Path)) {
        throw "Path '$Path' was not found."
    }

    $item = Get-Item -Path $Path
    $inheritanceFlags = [System.Security.AccessControl.InheritanceFlags]::None
    if ($item.PSIsContainer) {
        $inheritanceFlags = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    }

    $sid = ConvertTo-AccountSid -Identity $Identity
    $acl = Get-Acl -Path $Path
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $sid,
        $Rights,
        $inheritanceFlags,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.SetAccessRule($rule)
    Set-Acl -Path $Path -AclObject $acl
}

if (-not (Test-IsWindowsHost)) {
    throw 'SyncFactors service-account setup can only run on Windows.'
}

if (-not (Test-IsWindowsAdministrator)) {
    throw 'Run this script from an elevated PowerShell session on the target SyncFactors server.'
}

if ([string]::IsNullOrWhiteSpace($DeployAccount) -and [string]::IsNullOrWhiteSpace($RuntimeAccount)) {
    throw 'Specify DeployAccount, RuntimeAccount, or both.'
}

if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) {
    $RuntimeRoot = Join-Path $InstallRoot 'state'
}

$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$RuntimeRoot = [System.IO.Path]::GetFullPath($RuntimeRoot)
$runtimeDbRoot = Join-Path $RuntimeRoot 'runtime'
$runtimeLogRoot = Join-Path $RuntimeRoot 'logs'

$resolvedDeployAccount = $DeployAccount
if ($CreateLocalDeployAccount.IsPresent) {
    if ([string]::IsNullOrWhiteSpace($DeployAccount)) {
        throw 'DeployAccount is required when CreateLocalDeployAccount is set.'
    }

    if ($null -eq $DeployAccountPassword) {
        throw 'DeployAccountPassword is required when CreateLocalDeployAccount is set.'
    }

    if ($PSCmdlet.ShouldProcess($DeployAccount, 'Create local deploy account')) {
        $resolvedDeployAccount = Ensure-LocalAccount `
            -Identity $DeployAccount `
            -Password $DeployAccountPassword `
            -FullName 'SyncFactors deployment account' `
            -Description 'Deploys SyncFactors through Azure DevOps WinRM tasks.'
    }
}

$resolvedRuntimeAccount = $RuntimeAccount
if ($CreateLocalRuntimeAccount.IsPresent) {
    if ([string]::IsNullOrWhiteSpace($RuntimeAccount)) {
        throw 'RuntimeAccount is required when CreateLocalRuntimeAccount is set.'
    }

    if ($null -eq $RuntimeAccountPassword) {
        throw 'RuntimeAccountPassword is required when CreateLocalRuntimeAccount is set.'
    }

    if ($PSCmdlet.ShouldProcess($RuntimeAccount, 'Create local runtime account')) {
        $resolvedRuntimeAccount = Ensure-LocalAccount `
            -Identity $RuntimeAccount `
            -Password $RuntimeAccountPassword `
            -FullName 'SyncFactors runtime account' `
            -Description 'Runs SyncFactors.Api and SyncFactors.Worker Windows services.'
    }
}

New-Item -Path $InstallRoot -ItemType Directory -Force | Out-Null
New-Item -Path $RuntimeRoot -ItemType Directory -Force | Out-Null
New-Item -Path $runtimeDbRoot -ItemType Directory -Force | Out-Null
New-Item -Path $runtimeLogRoot -ItemType Directory -Force | Out-Null

$deployGroups = @()
if (-not [string]::IsNullOrWhiteSpace($resolvedDeployAccount)) {
    ConvertTo-AccountSid -Identity $resolvedDeployAccount | Out-Null

    if ($PSCmdlet.ShouldProcess($resolvedDeployAccount, 'Add to local Administrators for deployment')) {
        if (Add-AccountToLocalGroup -Identity $resolvedDeployAccount -GroupName 'Administrators') {
            $deployGroups += 'Administrators'
        }
    }

    if ($PSCmdlet.ShouldProcess($resolvedDeployAccount, 'Add to local Remote Management Users')) {
        if (Add-AccountToLocalGroup -Identity $resolvedDeployAccount -GroupName 'Remote Management Users') {
            $deployGroups += 'Remote Management Users'
        }
    }
}

$runtimeRights = @()
if (-not [string]::IsNullOrWhiteSpace($resolvedRuntimeAccount)) {
    ConvertTo-AccountSid -Identity $resolvedRuntimeAccount | Out-Null

    if ($PSCmdlet.ShouldProcess($resolvedRuntimeAccount, 'Grant Log on as a service')) {
        Grant-LogOnAsServiceRight -Identity $resolvedRuntimeAccount
        $runtimeRights += 'Log on as a service'
    }

    if ($PSCmdlet.ShouldProcess($InstallRoot, "Grant Modify to $resolvedRuntimeAccount")) {
        Grant-PathAccess -Path $InstallRoot -Identity $resolvedRuntimeAccount -Rights Modify
        $runtimeRights += "Modify $InstallRoot"
    }

    if ($PSCmdlet.ShouldProcess($RuntimeRoot, "Grant Modify to $resolvedRuntimeAccount")) {
        Grant-PathAccess -Path $RuntimeRoot -Identity $resolvedRuntimeAccount -Rights Modify
        $runtimeRights += "Modify $RuntimeRoot"
    }

    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        $resolvedPfxPath = [System.IO.Path]::GetFullPath($PfxPath)
        if ($PSCmdlet.ShouldProcess($resolvedPfxPath, "Grant Read to $resolvedRuntimeAccount")) {
            Grant-PathAccess -Path $resolvedPfxPath -Identity $resolvedRuntimeAccount -Rights Read
            $runtimeRights += "Read $resolvedPfxPath"
        }
    }
}

[pscustomobject]@{
    installRoot = $InstallRoot
    runtimeRoot = $RuntimeRoot
    deployAccount = if ([string]::IsNullOrWhiteSpace($resolvedDeployAccount)) { $null } else { $resolvedDeployAccount }
    deployGroups = $deployGroups
    runtimeAccount = if ([string]::IsNullOrWhiteSpace($resolvedRuntimeAccount)) { $null } else { $resolvedRuntimeAccount }
    runtimeRights = $runtimeRights
    createdLocalDeployAccount = $CreateLocalDeployAccount.IsPresent
    createdLocalRuntimeAccount = $CreateLocalRuntimeAccount.IsPresent
}
