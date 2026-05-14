[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = 'C:\SyncFactors',
    [string]$RuntimeRoot,
    [string]$PowerShellInstallScriptUrl = 'https://aka.ms/install-powershell.ps1',
    [version]$MinimumPowerShellVersion = '7.4.0',
    [switch]$InstallPowerShell,
    [switch]$ConfigureFirewall,
    [int]$ApiPort = 5087,
    [string]$FirewallRuleName = 'SyncFactors API',
    [string]$ServiceAccount,
    [securestring]$ServiceAccountPassword,
    [switch]$CreateLocalServiceAccount
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsWindowsAdministrator {
    if (-not (Test-IsWindowsHost)) {
        return $false
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-IsWindowsHost {
    return [string]::Equals($env:OS, 'Windows_NT', [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-PowerShellCoreCommand {
    $command = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $powerShellRoot = Join-Path $env:ProgramFiles 'PowerShell'
    if (Test-Path -Path $powerShellRoot -PathType Container) {
        $candidate = Get-ChildItem -Path $powerShellRoot -Filter pwsh.exe -Recurse -File -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    return $null
}

function Get-PowerShellCoreVersion {
    $commandPath = Get-PowerShellCoreCommand
    if (-not $commandPath) {
        return $null
    }

    $versionText = & $commandPath -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return [version]($versionText.Trim())
}

function ConvertTo-AccountSid {
    param(
        [Parameter(Mandatory)]
        [string]$Identity
    )

    $account = [System.Security.Principal.NTAccount]::new($Identity)
    return $account.Translate([System.Security.Principal.SecurityIdentifier])
}

function Install-PowerShellCore {
    param(
        [Parameter(Mandatory)]
        [string]$SourceUrl
    )

    $tempScript = Join-Path ([System.IO.Path]::GetTempPath()) 'install-powershell.ps1'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $SourceUrl -OutFile $tempScript -UseBasicParsing
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tempScript -UseMSI -Quiet
    if ($LASTEXITCODE -ne 0) {
        throw "PowerShell installer exited with code $LASTEXITCODE."
    }
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
        throw "Only local accounts can be created by this script. Create domain account '$Identity' in AD first, then run without -CreateLocalServiceAccount."
    }

    return $normalized
}

function Ensure-LocalServiceAccount {
    param(
        [Parameter(Mandatory)]
        [string]$Identity,
        [Parameter(Mandatory)]
        [securestring]$Password
    )

    $localName = Resolve-LocalAccountName -Identity $Identity
    $existing = Get-LocalUser -Name $localName -ErrorAction SilentlyContinue
    if ($existing) {
        return ".\$localName"
    }

    New-LocalUser `
        -Name $localName `
        -Password $Password `
        -FullName 'SyncFactors Windows service account' `
        -Description 'Runs SyncFactors.Api and SyncFactors.Worker Windows services.' `
        -PasswordNeverExpires `
        -UserMayNotChangePassword | Out-Null

    return ".\$localName"
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

function Initialize-Directory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ($PSCmdlet.ShouldProcess($Path, 'Create deployment directory')) {
        New-Item -Path $Path -ItemType Directory -Force | Out-Null
    }
}

function Grant-ServiceAccountAccess {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Identity
    )

    $sid = ConvertTo-AccountSid -Identity $Identity
    $acl = Get-Acl -Path $Path
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $sid,
        [System.Security.AccessControl.FileSystemRights]'Modify, Synchronize',
        [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.SetAccessRule($rule)
    Set-Acl -Path $Path -AclObject $acl
}

function Enable-ApiFirewallRule {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [int]$Port
    )

    $existingRule = Get-NetFirewallRule -DisplayName $Name -ErrorAction SilentlyContinue
    if ($existingRule) {
        Set-NetFirewallRule -DisplayName $Name -Enabled True -Direction Inbound -Action Allow | Out-Null
        Set-NetFirewallPortFilter -AssociatedNetFirewallRule $existingRule -Protocol TCP -LocalPort $Port
        return
    }

    New-NetFirewallRule `
        -DisplayName $Name `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort $Port `
        -Profile Domain,Private | Out-Null
}

if (-not (Test-IsWindowsHost)) {
    throw 'SyncFactors Windows prerequisites can only be installed on Windows.'
}

if (-not (Test-IsWindowsAdministrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) {
    $RuntimeRoot = Join-Path $InstallRoot 'state'
}

$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$RuntimeRoot = [System.IO.Path]::GetFullPath($RuntimeRoot)

$effectiveServiceAccount = $ServiceAccount
if ($CreateLocalServiceAccount.IsPresent) {
    if ([string]::IsNullOrWhiteSpace($ServiceAccount)) {
        throw 'ServiceAccount is required when CreateLocalServiceAccount is set.'
    }

    if ($null -eq $ServiceAccountPassword) {
        throw 'ServiceAccountPassword is required when CreateLocalServiceAccount is set.'
    }

    if ($PSCmdlet.ShouldProcess($ServiceAccount, 'Create local service account')) {
        $effectiveServiceAccount = Ensure-LocalServiceAccount -Identity $ServiceAccount -Password $ServiceAccountPassword
    }
}

Initialize-Directory -Path $InstallRoot
Initialize-Directory -Path $RuntimeRoot
Initialize-Directory -Path (Join-Path $RuntimeRoot 'runtime')
Initialize-Directory -Path (Join-Path $RuntimeRoot 'logs')

if (-not [string]::IsNullOrWhiteSpace($effectiveServiceAccount)) {
    if ($PSCmdlet.ShouldProcess($effectiveServiceAccount, 'Grant Log on as a service')) {
        Grant-LogOnAsServiceRight -Identity $effectiveServiceAccount
    }

    if ($PSCmdlet.ShouldProcess($InstallRoot, "Grant Modify access to $effectiveServiceAccount")) {
        Grant-ServiceAccountAccess -Path $InstallRoot -Identity $effectiveServiceAccount
    }

    if ($PSCmdlet.ShouldProcess($RuntimeRoot, "Grant Modify access to $effectiveServiceAccount")) {
        Grant-ServiceAccountAccess -Path $RuntimeRoot -Identity $effectiveServiceAccount
    }
}

$powerShellVersion = Get-PowerShellCoreVersion
if ($null -eq $powerShellVersion -or $powerShellVersion -lt $MinimumPowerShellVersion) {
    if (-not $InstallPowerShell.IsPresent) {
        throw "PowerShell $MinimumPowerShellVersion or newer is required. Re-run with -InstallPowerShell to install it."
    }

    if ($PSCmdlet.ShouldProcess('PowerShell 7', 'Install or upgrade')) {
        Install-PowerShellCore -SourceUrl $PowerShellInstallScriptUrl
        $powerShellVersion = Get-PowerShellCoreVersion
    }
}

if ($null -eq $powerShellVersion -or $powerShellVersion -lt $MinimumPowerShellVersion) {
    throw "PowerShell $MinimumPowerShellVersion or newer is still not available on PATH after prerequisite installation."
}

if ($ConfigureFirewall.IsPresent) {
    if ($PSCmdlet.ShouldProcess($FirewallRuleName, "Allow inbound TCP $ApiPort")) {
        Enable-ApiFirewallRule -Name $FirewallRuleName -Port $ApiPort
    }
}

$configuredFirewallRule = $null
if ($ConfigureFirewall.IsPresent) {
    $configuredFirewallRule = $FirewallRuleName
}

$configuredServiceAccount = $null
if (-not [string]::IsNullOrWhiteSpace($effectiveServiceAccount)) {
    $configuredServiceAccount = $effectiveServiceAccount
}

[pscustomobject]@{
    installRoot = $InstallRoot
    runtimeRoot = $RuntimeRoot
    powerShellVersion = $powerShellVersion.ToString()
    firewallRule = $configuredFirewallRule
    apiPort = $ApiPort
    serviceAccount = $configuredServiceAccount
    createdLocalServiceAccount = $CreateLocalServiceAccount.IsPresent
}
