[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]$BundleZip,
    [string]$InstallRoot = 'C:\SyncFactors',
    [string]$ApiServiceName = 'SyncFactors.Api',
    [string]$WorkerServiceName = 'SyncFactors.Worker',
    [ValidateSet('mock', 'real')]
    [string]$RunProfile = 'real',
    [switch]$DryRunOnly,
    [switch]$EnableLiveWrites,
    [string]$ApiUrls = 'https://0.0.0.0:5087',
    [string]$TlsCertificatePath,
    [string]$TlsCertificatePassword,
    [string]$TlsCertificateThumbprint,
    [string]$WindowsCredentialPrefix = 'SyncFactors',
    [string]$SqlitePassword,
    [switch]$DisableSqliteEncryption,
    [string]$SecurityAuditLogPath,
    [pscredential]$Credential,
    [switch]$InstallOrUpdateServices,
    [string]$HealthUrl = 'https://localhost:5087/readyz',
    [switch]$SkipHealthCheck,
    [switch]$NoRollbackOnFailure
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

function Resolve-RequiredPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if (-not (Test-Path -Path $Path)) {
        throw "$Label '$Path' was not found."
    }

    return (Resolve-Path -Path $Path).ProviderPath
}

function Stop-SyncFactorsService {
    param([string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service) {
        return $false
    }

    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $Name -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }

    return $true
}

function Start-SyncFactorsService {
    param([string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service) {
        return $false
    }

    Start-Service -Name $Name
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    return $true
}

function Copy-PathReplacingDestination {
    param(
        [Parameter(Mandatory)]
        [string]$Source,
        [Parameter(Mandatory)]
        [string]$Destination
    )

    if (Test-Path -Path $Destination) {
        Remove-Item -Path $Destination -Recurse -Force
    }

    $parent = Split-Path -Path $Destination -Parent
    if ($parent -and -not (Test-Path -Path $parent -PathType Container)) {
        New-Item -Path $parent -ItemType Directory -Force | Out-Null
    }

    Copy-Item -Path $Source -Destination $Destination -Recurse -Force
}

function Copy-PathMergingDestination {
    param(
        [Parameter(Mandatory)]
        [string]$Source,
        [Parameter(Mandatory)]
        [string]$Destination
    )

    if (-not (Test-Path -Path $Destination -PathType Container)) {
        New-Item -Path $Destination -ItemType Directory -Force | Out-Null
    }

    Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
}

function Backup-Path {
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$RelativePath,
        [Parameter(Mandatory)]
        [string]$BackupRoot
    )

    $source = Join-Path $Root $RelativePath
    if (-not (Test-Path -Path $source)) {
        return
    }

    $destination = Join-Path $BackupRoot $RelativePath
    $parent = Split-Path -Path $destination -Parent
    if ($parent -and -not (Test-Path -Path $parent -PathType Container)) {
        New-Item -Path $parent -ItemType Directory -Force | Out-Null
    }

    Copy-Item -Path $source -Destination $destination -Recurse -Force
}

function Restore-Backup {
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$BackupRoot
    )

    foreach ($item in Get-ChildItem -Path $BackupRoot -Force) {
        $destination = Join-Path $Root $item.Name
        if (Test-Path -Path $destination) {
            Remove-Item -Path $destination -Recurse -Force
        }

        Copy-Item -Path $item.FullName -Destination $destination -Recurse -Force
    }
}

function Copy-ConfigSamples {
    param(
        [Parameter(Mandatory)]
        [string]$SourceConfigRoot,
        [Parameter(Mandatory)]
        [string]$DestinationConfigRoot
    )

    if (-not (Test-Path -Path $SourceConfigRoot -PathType Container)) {
        return
    }

    if (-not (Test-Path -Path $DestinationConfigRoot -PathType Container)) {
        New-Item -Path $DestinationConfigRoot -ItemType Directory -Force | Out-Null
    }

    foreach ($item in Get-ChildItem -Path $SourceConfigRoot -Force) {
        if ($item.Name.StartsWith('local.', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $destination = Join-Path $DestinationConfigRoot $item.Name
        Copy-PathReplacingDestination -Source $item.FullName -Destination $destination
    }
}

function Get-ServiceEnvironment {
    param([Parameter(Mandatory)][string]$ServiceName)

    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (-not (Test-Path -Path $serviceKey)) {
        return $null
    }

    $environment = Get-ItemPropertyValue -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue
    return [pscustomobject]@{
        Exists = $null -ne $environment
        Values = @($environment)
    }
}

function Get-ServiceEnvironmentValue {
    param(
        [Parameter(Mandatory)][string]$ServiceName,
        [Parameter(Mandatory)][string]$Name
    )

    $environment = Get-ServiceEnvironment -ServiceName $ServiceName
    if ($null -eq $environment) {
        return $null
    }

    foreach ($entry in @($environment.Values)) {
        if ($null -ne $entry -and $entry.StartsWith("$Name=", [System.StringComparison]::OrdinalIgnoreCase)) {
            return $entry.Substring($Name.Length + 1)
        }
    }

    return $null
}

function Set-ServiceEnvironmentValue {
    param(
        [Parameter(Mandatory)][string]$ServiceName,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (-not (Test-Path -Path $serviceKey)) {
        throw "Service registry key '$serviceKey' was not found."
    }

    $environment = Get-ServiceEnvironment -ServiceName $ServiceName
    $updated = @(
        @($environment.Values) |
            Where-Object { $null -ne $_ -and -not $_.StartsWith("$Name=", [System.StringComparison]::OrdinalIgnoreCase) }
    )
    $updated += "$Name=$Value"
    New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value $updated -Force | Out-Null
}

function Restore-ServiceEnvironment {
    param(
        [Parameter(Mandatory)][string]$ServiceName,
        [Parameter(Mandatory)]$Snapshot
    )

    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (-not (Test-Path -Path $serviceKey)) {
        return
    }

    if ($Snapshot.Exists) {
        New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value @($Snapshot.Values) -Force | Out-Null
    }
    else {
        Remove-ItemProperty -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue
    }
}

function Resolve-DryRunOnlyMode {
    param(
        [Parameter(Mandatory)][string[]]$ServiceNames,
        [Parameter(Mandatory)][bool]$DryRunOnlyRequested,
        [Parameter(Mandatory)][bool]$LiveWritesRequested
    )

    if ($DryRunOnlyRequested -and $LiveWritesRequested) {
        throw 'DryRunOnly and EnableLiveWrites cannot be specified together.'
    }

    if ($DryRunOnlyRequested) {
        return [pscustomobject]@{ Value = $true; Source = 'parameter' }
    }

    if ($LiveWritesRequested) {
        return [pscustomobject]@{ Value = $false; Source = 'explicit-live-write-opt-in' }
    }

    $existingValues = @()
    foreach ($serviceName in $ServiceNames) {
        $existingValue = Get-ServiceEnvironmentValue -ServiceName $serviceName -Name 'SyncFactors__Runtime__DryRunOnly'
        if ([string]::IsNullOrWhiteSpace($existingValue)) {
            continue
        }

        $parsedValue = $false
        if (-not [bool]::TryParse($existingValue, [ref]$parsedValue)) {
            throw "Service '$serviceName' has invalid SyncFactors__Runtime__DryRunOnly value '$existingValue'. Re-run with -DryRunOnly or -EnableLiveWrites."
        }

        $existingValues += $parsedValue
    }

    $distinctValues = @($existingValues | Select-Object -Unique)
    if ($distinctValues.Count -gt 1) {
        throw 'The installed API and worker disagree on SyncFactors__Runtime__DryRunOnly. Re-run with -DryRunOnly or -EnableLiveWrites to select one mode for both services.'
    }

    if ($distinctValues.Count -eq 1) {
        return [pscustomobject]@{ Value = [bool]$distinctValues[0]; Source = 'existing-service-environment' }
    }

    return [pscustomobject]@{ Value = $true; Source = 'production-safe-default' }
}

if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'SyncFactors Windows patch deployment can only run on Windows.'
}

if (-not (Test-IsWindowsAdministrator)) {
    throw 'Run this script from an elevated PowerShell session on the target SyncFactors server.'
}

$resolvedBundleZip = Resolve-RequiredPath -Path $BundleZip -Label 'Bundle zip'
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$stagingRoot = Join-Path $InstallRoot '_staging'
$backupRoot = Join-Path $InstallRoot '_backups'
$stamp = Get-Date -Format 'yyyyMMddHHmmss'
$expandedRoot = Join-Path $stagingRoot $stamp
$currentBackupRoot = Join-Path $backupRoot $stamp
$services = @($ApiServiceName, $WorkerServiceName)
$writeSafetyMode = Resolve-DryRunOnlyMode `
    -ServiceNames $services `
    -DryRunOnlyRequested $DryRunOnly.IsPresent `
    -LiveWritesRequested $EnableLiveWrites.IsPresent
$originalServiceEnvironments = @{}
foreach ($service in $services) {
    $environment = Get-ServiceEnvironment -ServiceName $service
    if ($null -ne $environment) {
        $originalServiceEnvironments[$service] = $environment
    }
}

New-Item -Path $expandedRoot -ItemType Directory -Force | Out-Null
New-Item -Path $currentBackupRoot -ItemType Directory -Force | Out-Null

try {
    Expand-Archive -Path $resolvedBundleZip -DestinationPath $expandedRoot -Force

    foreach ($requiredPath in @('app\api', 'app\worker', 'scripts')) {
        $candidate = Join-Path $expandedRoot $requiredPath
        if (-not (Test-Path -Path $candidate)) {
            throw "Bundle '$resolvedBundleZip' is missing required path '$requiredPath'."
        }
    }

    foreach ($relativePath in @('app', 'scripts', 'docs', 'README.md', 'LICENSE', 'SECURITY.md', 'CONTRIBUTING.md', 'VERSION', 'release-manifest.json')) {
        Backup-Path -Root $InstallRoot -RelativePath $relativePath -BackupRoot $currentBackupRoot
    }

    foreach ($service in $services) {
        if ($PSCmdlet.ShouldProcess($service, 'Stop Windows service')) {
            Stop-SyncFactorsService -Name $service | Out-Null
        }
    }

    if ($PSCmdlet.ShouldProcess($InstallRoot, "Deploy bundle $resolvedBundleZip")) {
        Copy-PathReplacingDestination -Source (Join-Path $expandedRoot 'app') -Destination (Join-Path $InstallRoot 'app')
        Copy-PathMergingDestination -Source (Join-Path $expandedRoot 'scripts') -Destination (Join-Path $InstallRoot 'scripts')

        foreach ($relativePath in @('docs', 'README.md', 'LICENSE', 'SECURITY.md', 'CONTRIBUTING.md', 'VERSION', 'release-manifest.json')) {
            $source = Join-Path $expandedRoot $relativePath
            if (Test-Path -Path $source) {
                Copy-PathReplacingDestination -Source $source -Destination (Join-Path $InstallRoot $relativePath)
            }
        }

        Copy-ConfigSamples -SourceConfigRoot (Join-Path $expandedRoot 'config') -DestinationConfigRoot (Join-Path $InstallRoot 'config')
    }

    if ($InstallOrUpdateServices.IsPresent) {
        if ($null -eq $Credential) {
            throw 'Credential is required when InstallOrUpdateServices is set because Windows service credentials cannot be recovered from existing services.'
        }

        $installScript = Join-Path $InstallRoot 'scripts\Install-SyncFactorsWindowsServices.ps1'
        $installArgs = @(
            '-BundleRoot', $InstallRoot,
            '-RunProfile', $RunProfile,
            '-ApiUrls', $ApiUrls,
            '-ApiServiceName', $ApiServiceName,
            '-WorkerServiceName', $WorkerServiceName,
            '-WindowsCredentialPrefix', $WindowsCredentialPrefix,
            '-Credential', $Credential,
            '-Force'
        )
        if (-not [string]::IsNullOrWhiteSpace($TlsCertificatePath)) {
            $installArgs += @('-TlsCertificatePath', $TlsCertificatePath)
        }
        if (-not [string]::IsNullOrWhiteSpace($TlsCertificatePassword)) {
            $installArgs += @('-TlsCertificatePassword', $TlsCertificatePassword)
        }
        if (-not [string]::IsNullOrWhiteSpace($TlsCertificateThumbprint)) {
            $installArgs += @('-TlsCertificateThumbprint', $TlsCertificateThumbprint)
        }
        if (-not [string]::IsNullOrWhiteSpace($SqlitePassword)) {
            $installArgs += @('-SqlitePassword', $SqlitePassword)
        }
        if ($DisableSqliteEncryption.IsPresent) {
            $installArgs += '-DisableSqliteEncryption'
        }
        if (-not [string]::IsNullOrWhiteSpace($SecurityAuditLogPath)) {
            $installArgs += @('-SecurityAuditLogPath', $SecurityAuditLogPath)
        }
        if ($writeSafetyMode.Value) {
            $installArgs += '-DryRunOnly'
        }
        else {
            $installArgs += '-EnableLiveWrites'
        }

        if ($PSCmdlet.ShouldProcess("$ApiServiceName,$WorkerServiceName", 'Install or update Windows services')) {
            & $installScript @installArgs | Out-Host
        }
    }
    else {
        foreach ($service in $services) {
            if (-not (Get-Service -Name $service -ErrorAction SilentlyContinue)) {
                throw "Service '$service' is not installed. Re-run with -InstallOrUpdateServices and -Credential, or install services once with Install-SyncFactorsWindowsServices.ps1."
            }
        }
    }

    $dryRunOnlyValue = $writeSafetyMode.Value.ToString().ToLowerInvariant()
    foreach ($service in $services) {
        if ($PSCmdlet.ShouldProcess($service, "Set SyncFactors__Runtime__DryRunOnly=$dryRunOnlyValue")) {
            Set-ServiceEnvironmentValue `
                -ServiceName $service `
                -Name 'SyncFactors__Runtime__DryRunOnly' `
                -Value $dryRunOnlyValue
        }
    }

    foreach ($service in $services) {
        if ($PSCmdlet.ShouldProcess($service, 'Start Windows service')) {
            Start-SyncFactorsService -Name $service | Out-Null
        }
    }

    $verificationScript = Join-Path $InstallRoot 'scripts\Test-SyncFactorsWindowsDeployment.ps1'
    $verificationArgs = @(
        '-ApiServiceName', $ApiServiceName,
        '-WorkerServiceName', $WorkerServiceName,
        '-HealthUrl', $HealthUrl
    )
    if ($writeSafetyMode.Value) {
        $verificationArgs += '-DryRunOnly'
    }
    else {
        $verificationArgs += '-EnableLiveWrites'
    }
    if ($SkipHealthCheck.IsPresent) {
        $verificationArgs += '-SkipHttpHealthCheck'
    }

    $verification = & $verificationScript @verificationArgs

    [pscustomobject]@{
        bundleZip = $resolvedBundleZip
        installRoot = $InstallRoot
        stagingRoot = $expandedRoot
        backupRoot = $currentBackupRoot
        apiServiceName = $ApiServiceName
        workerServiceName = $WorkerServiceName
        dryRunOnly = $writeSafetyMode.Value
        writeSafetySource = $writeSafetyMode.Source
        healthUrl = if ($SkipHealthCheck.IsPresent) { $null } else { $HealthUrl }
        verification = $verification
        rollbackAvailable = $true
    }
}
catch {
    $failure = $_
    Write-Warning "SyncFactors patch deployment failed: $($failure.Exception.Message)"

    if (-not $NoRollbackOnFailure.IsPresent) {
        Write-Warning "Rolling back deployable files from '$currentBackupRoot'. State and local config were not modified."
        foreach ($service in $services) {
            Stop-SyncFactorsService -Name $service | Out-Null
        }

        Restore-Backup -Root $InstallRoot -BackupRoot $currentBackupRoot

        foreach ($service in $services) {
            if ($originalServiceEnvironments.ContainsKey($service)) {
                Restore-ServiceEnvironment -ServiceName $service -Snapshot $originalServiceEnvironments[$service]
            }
        }

        foreach ($service in $services) {
            Start-SyncFactorsService -Name $service | Out-Null
        }
    }

    throw
}
