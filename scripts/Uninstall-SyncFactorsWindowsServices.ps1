[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('All', 'Api', 'Worker')]
    [string]$Service = 'All',
    [string]$ApiServiceName = 'SyncFactors.Api',
    [string]$WorkerServiceName = 'SyncFactors.Worker',
    [switch]$RemoveEventLogSources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsWindowsAdministrator {
    if (-not [System.OperatingSystem]::IsWindows()) {
        return $false
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Remove-SyncFactorsService {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $existing = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        return
    }

    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $Name -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    if (Get-Command Remove-Service -ErrorAction SilentlyContinue) {
        Remove-Service -Name $Name
    }
    else {
        & sc.exe delete $Name | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "sc.exe delete failed for service '$Name'."
        }
    }
}

function Remove-EventLogSource {
    param(
        [Parameter(Mandatory)]
        [string]$SourceName
    )

    if (-not [System.Diagnostics.EventLog]::SourceExists($SourceName)) {
        return
    }

    [System.Diagnostics.EventLog]::DeleteEventSource($SourceName)
}

if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'SyncFactors Windows services can only be uninstalled on Windows.'
}

if (-not (Test-IsWindowsAdministrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

$serviceNames = switch ($Service) {
    'All' { @($ApiServiceName, $WorkerServiceName) }
    'Api' { @($ApiServiceName) }
    'Worker' { @($WorkerServiceName) }
}

foreach ($name in $serviceNames) {
    if ($PSCmdlet.ShouldProcess($name, 'Uninstall Windows service')) {
        Remove-SyncFactorsService -Name $name
        if ($RemoveEventLogSources.IsPresent) {
            Remove-EventLogSource -SourceName $name
        }
    }
}

[pscustomobject]@{
    removedServices = $serviceNames
    removedEventLogSources = $RemoveEventLogSources.IsPresent
}
