[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$BundleRoot,
    [ValidateSet('All', 'Api', 'Worker')]
    [string]$Service = 'All',
    [ValidateSet('mock', 'real')]
    [string]$RunProfile = 'real',
    [ValidateSet('Automatic', 'Manual', 'Disabled')]
    [string]$StartupType = 'Automatic',
    [switch]$DelayedAutoStart = $true,
    [switch]$Force,
    [string]$ApiServiceName = 'SyncFactors.Api',
    [string]$WorkerServiceName = 'SyncFactors.Worker',
    [string]$ApiUrls = 'https://127.0.0.1:5087',
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [string]$SqlitePath,
    [string]$LogDirectory,
    [string]$TlsCertificatePath,
    [string]$TlsCertificatePassword,
    [pscredential]$Credential
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

function Resolve-BundleRoot {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $Path = Join-Path $PSScriptRoot '..'
    }

    return (Resolve-Path -Path $Path).ProviderPath
}

function Initialize-LocalConfig {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $configRoot = Join-Path $Root 'config'
    if (-not (Test-Path -Path $configRoot -PathType Container)) {
        throw "Config directory '$configRoot' was not found."
    }

    $copies = @(
        @{ Source = 'sample.mock-successfactors.real-ad.sync-config.json'; Target = 'local.mock-successfactors.real-ad.sync-config.json' },
        @{ Source = 'sample.real-successfactors.real-ad.sync-config.json'; Target = 'local.real-successfactors.real-ad.sync-config.json' },
        @{ Source = 'sample.empjob-confirmed.mapping-config.json'; Target = 'local.syncfactors.mapping-config.json' }
    )

    foreach ($copy in $copies) {
        $source = Join-Path $configRoot $copy.Source
        $target = Join-Path $configRoot $copy.Target
        if (-not (Test-Path -Path $source -PathType Leaf)) {
            throw "Sample config '$source' was not found."
        }

        if (-not (Test-Path -Path $target -PathType Leaf)) {
            Copy-Item -Path $source -Destination $target
        }
    }
}

function Resolve-DefaultConfigPath {
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$Profile
    )

    $fileName = if ($Profile -eq 'real') {
        'local.real-successfactors.real-ad.sync-config.json'
    }
    else {
        'local.mock-successfactors.real-ad.sync-config.json'
    }

    return Join-Path (Join-Path $Root 'config') $fileName
}

function Register-EventLogSource {
    param(
        [Parameter(Mandatory)]
        [string]$SourceName
    )

    if ([System.Diagnostics.EventLog]::SourceExists($SourceName)) {
        return
    }

    $sourceData = [System.Diagnostics.EventSourceCreationData]::new($SourceName, 'Application')
    [System.Diagnostics.EventLog]::CreateEventSource($sourceData)
}

function Remove-ExistingService {
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

    for ($i = 0; $i -lt 30; $i++) {
        if ($null -eq (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
            return
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for service '$Name' to be deleted."
}

function Set-ServiceEnvironment {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string[]]$Environment
    )

    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    if (-not (Test-Path -Path $serviceKey)) {
        throw "Service registry key '$serviceKey' was not found."
    }

    New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value $Environment -Force | Out-Null
}

function Set-ServiceRecoveryPolicy {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    & sc.exe failure $Name reset= 86400 actions= restart/60000/restart/60000/""/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe failure failed for service '$Name'."
    }

    & sc.exe failureflag $Name 1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe failureflag failed for service '$Name'."
    }
}

function Install-SyncFactorsService {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$DisplayName,
        [Parameter(Mandatory)]
        [string]$Description,
        [Parameter(Mandatory)]
        [string]$ExecutablePath,
        [Parameter(Mandatory)]
        [string[]]$Environment
    )

    if (-not (Test-Path -Path $ExecutablePath -PathType Leaf)) {
        throw "Executable '$ExecutablePath' was not found."
    }

    if ((Get-Service -Name $Name -ErrorAction SilentlyContinue) -and -not $Force.IsPresent) {
        throw "Service '$Name' already exists. Re-run with -Force to replace it."
    }

    if ($Force.IsPresent) {
        Remove-ExistingService -Name $Name
    }

    Register-EventLogSource -SourceName $Name

    $newServiceParameters = @{
        Name = $Name
        DisplayName = $DisplayName
        BinaryPathName = "`"$ExecutablePath`""
        StartupType = $StartupType
    }
    if ($Credential) {
        $newServiceParameters.Credential = $Credential
    }

    New-Service @newServiceParameters | Out-Null
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$Name" -Name Description -Value $Description
    if ($StartupType -eq 'Automatic' -and $DelayedAutoStart.IsPresent) {
        New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$Name" -Name DelayedAutoStart -PropertyType DWord -Value 1 -Force | Out-Null
    }

    Set-ServiceEnvironment -Name $Name -Environment $Environment
    Set-ServiceRecoveryPolicy -Name $Name
}

if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'SyncFactors Windows services can only be installed on Windows.'
}

if (-not (Test-IsWindowsAdministrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

$resolvedBundleRoot = Resolve-BundleRoot -Path $BundleRoot
Initialize-LocalConfig -Root $resolvedBundleRoot

$runtimeRoot = Join-Path $resolvedBundleRoot 'state'
if ([string]::IsNullOrWhiteSpace($SqlitePath)) {
    $SqlitePath = Join-Path $runtimeRoot 'runtime\syncfactors.db'
}
if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $LogDirectory = Join-Path $runtimeRoot 'logs'
}
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Resolve-DefaultConfigPath -Root $resolvedBundleRoot -Profile $RunProfile
}
if ([string]::IsNullOrWhiteSpace($MappingConfigPath)) {
    $MappingConfigPath = Join-Path (Join-Path $resolvedBundleRoot 'config') 'local.syncfactors.mapping-config.json'
}

$ConfigPath = [System.IO.Path]::GetFullPath($ConfigPath)
$MappingConfigPath = [System.IO.Path]::GetFullPath($MappingConfigPath)
$SqlitePath = [System.IO.Path]::GetFullPath($SqlitePath)
$LogDirectory = [System.IO.Path]::GetFullPath($LogDirectory)

New-Item -Path (Split-Path -Path $SqlitePath -Parent) -ItemType Directory -Force | Out-Null
New-Item -Path $LogDirectory -ItemType Directory -Force | Out-Null

$commonEnvironment = @(
    "DOTNET_ENVIRONMENT=Production",
    "SYNCFACTORS_RUN_PROFILE=$RunProfile",
    "SyncFactors__ConfigPath=$ConfigPath",
    "SyncFactors__MappingConfigPath=$MappingConfigPath",
    "SyncFactors__SqlitePath=$SqlitePath",
    "SYNCFACTORS_LOCAL_FILE_LOGGING_ENABLED=true",
    "SYNCFACTORS_LOCAL_LOG_DIRECTORY=$LogDirectory",
    "Logging__LogLevel__Default=Information",
    "Logging__LogLevel__Microsoft=Warning",
    "Logging__LogLevel__Microsoft.Hosting.Lifetime=Information",
    "Logging__LogLevel__SyncFactors=Information",
    "Logging__EventLog__LogLevel__Default=Information",
    "Logging__EventLog__LogLevel__Microsoft=Warning",
    "Logging__EventLog__LogLevel__Microsoft.Hosting.Lifetime=Information",
    "Logging__EventLog__LogLevel__SyncFactors=Information"
)

$apiEnvironment = @($commonEnvironment + @(
    "ASPNETCORE_ENVIRONMENT=Production",
    "ASPNETCORE_URLS=$ApiUrls"
))
if (-not [string]::IsNullOrWhiteSpace($TlsCertificatePath)) {
    $apiEnvironment += "ASPNETCORE_Kestrel__Certificates__Default__Path=$([System.IO.Path]::GetFullPath($TlsCertificatePath))"
}
if (-not [string]::IsNullOrWhiteSpace($TlsCertificatePassword)) {
    $apiEnvironment += "ASPNETCORE_Kestrel__Certificates__Default__Password=$TlsCertificatePassword"
}

$workerEnvironment = @($commonEnvironment)

if ($Service -in @('All', 'Api')) {
    $apiExe = Join-Path $resolvedBundleRoot 'app\api\SyncFactors.Api.exe'
    if ($PSCmdlet.ShouldProcess($ApiServiceName, 'Install Windows service')) {
        Install-SyncFactorsService `
            -Name $ApiServiceName `
            -DisplayName 'SyncFactors API' `
            -Description 'SyncFactors operator portal and API host.' `
            -ExecutablePath $apiExe `
            -Environment $apiEnvironment
    }
}

if ($Service -in @('All', 'Worker')) {
    $workerExe = Join-Path $resolvedBundleRoot 'app\worker\SyncFactors.Worker.exe'
    if ($PSCmdlet.ShouldProcess($WorkerServiceName, 'Install Windows service')) {
        Install-SyncFactorsService `
            -Name $WorkerServiceName `
            -DisplayName 'SyncFactors Worker' `
            -Description 'SyncFactors background sync worker.' `
            -ExecutablePath $workerExe `
            -Environment $workerEnvironment
    }
}

[pscustomobject]@{
    bundleRoot = $resolvedBundleRoot
    runProfile = $RunProfile
    apiServiceName = $ApiServiceName
    workerServiceName = $WorkerServiceName
    configPath = $ConfigPath
    mappingConfigPath = $MappingConfigPath
    sqlitePath = $SqlitePath
    logDirectory = $LogDirectory
    eventLog = 'Application'
}
