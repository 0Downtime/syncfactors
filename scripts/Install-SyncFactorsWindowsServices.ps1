[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$BundleRoot,
    [ValidateSet('All', 'Api', 'Worker')]
    [string]$Service = 'All',
    [ValidateSet('mock', 'real')]
    [string]$RunProfile = 'real',
    [switch]$DryRunOnly,
    [switch]$EnableLiveWrites,
    [string]$DeploymentNonce,
    [string]$DeploymentCommitMarkerPath,
    [ValidateSet('local-break-glass', 'oidc', 'hybrid')]
    [string]$AuthMode,
    [string]$OidcAuthority,
    [string]$OidcClientId,
    [string]$OidcViewerGroups,
    [string]$OidcOperatorGroups,
    [string]$OidcAdminGroups,
    [string]$BootstrapAdminUsername,
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
    [string]$SqlitePassword,
    [switch]$DisableSqliteEncryption,
    [string]$SecurityAuditLogPath,
    [string]$SecurityAuditIntegrityKey,
    [string]$LogDirectory,
    [string]$TlsCertificatePath,
    [string]$TlsCertificatePassword,
    [string]$TlsCertificateThumbprint,
    [string]$WindowsCredentialPrefix,
    [string]$DeploymentSecretsFile,
    [pscredential]$Credential,
    [switch]$AllowLocalSystem
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

function Test-IsWindowsAdministrator {
    if (-not [System.OperatingSystem]::IsWindows()) {
        return $false
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-ServiceIdentityConfiguration {
    param(
        [pscredential]$RuntimeCredential,
        [Parameter(Mandatory)]
        [bool]$LocalSystemAllowed
    )

    if ($null -eq $RuntimeCredential -and -not $LocalSystemAllowed) {
        throw 'Credential is required for the restricted SyncFactors runtime identity. LocalSystem is blocked by default; pass -AllowLocalSystem only for an explicitly approved exceptional deployment.'
    }

    if ($null -ne $RuntimeCredential -and $LocalSystemAllowed) {
        throw 'Credential and AllowLocalSystem cannot be specified together.'
    }

    if ($null -eq $RuntimeCredential) {
        return 'LocalSystem (explicit opt-in)'
    }

    return $RuntimeCredential.UserName
}

function ConvertFrom-ProtectedSecureString {
    param([Parameter(Mandatory)][securestring]$Value)

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Import-DeploymentSecrets {
    param([Parameter(Mandatory)][string]$Path)

    $resolvedPath = (Resolve-Path -Path $Path -ErrorAction Stop).ProviderPath
    if ([System.OperatingSystem]::IsWindows()) {
        $acl = Get-Acl -Path $resolvedPath
        if (-not $acl.AreAccessRulesProtected) {
            throw "Deployment secrets file '$resolvedPath' must have inheritance disabled."
        }
        $broadSids = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545')
        foreach ($rule in @($acl.Access)) {
            $ruleSid = $rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            if ($rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and
                $ruleSid -in $broadSids) {
                throw "Deployment secrets file '$resolvedPath' grants access to a broad Windows principal."
            }
        }
    }

    $handoff = Import-Clixml -Path $resolvedPath
    $result = [ordered]@{ Credential = $null; SqlitePassword = $null; SecurityAuditIntegrityKey = $null }
    $credentialProperty = $handoff.PSObject.Properties['Credential']
    if ($null -ne $credentialProperty -and $null -ne $credentialProperty.Value) {
        if ($credentialProperty.Value -isnot [pscredential]) {
            throw "Deployment secrets file '$resolvedPath' contains an invalid Credential value."
        }
        $result.Credential = $credentialProperty.Value
    }
    foreach ($mapping in @(
        @{ Property = 'SqlitePassword'; Result = 'SqlitePassword' },
        @{ Property = 'SecurityAuditIntegrityKey'; Result = 'SecurityAuditIntegrityKey' })) {
        $property = $handoff.PSObject.Properties[$mapping.Property]
        if ($null -ne $property -and $null -ne $property.Value) {
            if ($property.Value -isnot [securestring]) {
                throw "Deployment secrets file '$resolvedPath' contains an invalid $($mapping.Property) value."
            }
            $result[$mapping.Result] = ConvertFrom-ProtectedSecureString -Value $property.Value
        }
    }
    return [pscustomobject]$result
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

function Protect-ServiceRegistryKey {
    param([Parameter(Mandatory)][string]$Name, [string]$RegistryPath)

    $serviceKey = if ([string]::IsNullOrWhiteSpace($RegistryPath)) {
        "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    }
    else {
        $RegistryPath
    }
    $acl = Get-Acl -Path $serviceKey
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($existingRule in @($acl.Access)) {
        [void]$acl.RemoveAccessRuleSpecific($existingRule)
    }
    foreach ($sid in @(
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $rule = [System.Security.AccessControl.RegistryAccessRule]::new(
            $sid,
            [System.Security.AccessControl.RegistryRights]::FullControl,
            [System.Security.AccessControl.InheritanceFlags]::ContainerInherit,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void]$acl.AddAccessRule($rule)
    }
    Set-Acl -Path $serviceKey -AclObject $acl
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

function Get-ServiceEnvironmentValue {
    param(
        [Parameter(Mandatory)]
        [string[]]$ServiceNames,
        [Parameter(Mandatory)]
        [string]$Name
    )

    foreach ($serviceName in $ServiceNames) {
        $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
        if (-not (Test-Path -Path $serviceKey)) {
            continue
        }

        $environment = Get-ItemPropertyValue -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue
        if ($null -eq $environment) {
            continue
        }

        foreach ($entry in @($environment)) {
            if ($entry.StartsWith("$Name=", [System.StringComparison]::OrdinalIgnoreCase)) {
                return $entry.Substring($Name.Length + 1)
            }
        }
    }

    return $null
}

function Get-ServiceEnvironmentEntries {
    param([Parameter(Mandatory)][string]$ServiceName)

    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (-not (Test-Path -Path $serviceKey)) {
        return @()
    }

    return @(Get-ItemPropertyValue -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue)
}

function Merge-ServiceEnvironmentEntries {
    param(
        [object[]]$ExistingEntries,
        [Parameter(Mandatory)][string[]]$ManagedEntries,
        [Parameter(Mandatory)][string[]]$ManagedNames
    )

    $managedNameSet = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $ManagedNames) {
        [void]$managedNameSet.Add($name)
    }

    $preserved = @()
    foreach ($entry in @($ExistingEntries)) {
        if ($null -eq $entry) {
            continue
        }

        $separatorIndex = $entry.IndexOf('=')
        if ($separatorIndex -le 0) {
            $preserved += $entry
            continue
        }

        $name = $entry.Substring(0, $separatorIndex)
        if (-not $managedNameSet.Contains($name)) {
            $preserved += $entry
        }
    }

    return @($preserved + $ManagedEntries)
}

function Get-EnvironmentEntryValue {
    param([object[]]$Entries, [Parameter(Mandatory)][string]$Name)

    foreach ($entry in @($Entries)) {
        if ($null -ne $entry -and $entry.StartsWith("$Name=", [System.StringComparison]::OrdinalIgnoreCase)) {
            return $entry.Substring($Name.Length + 1)
        }
    }
    return $null
}

function Get-IndexedEnvironmentEntryValues {
    param([object[]]$Entries, [Parameter(Mandatory)][string]$Prefix)

    $indexedValues = @()
    foreach ($entry in @($Entries)) {
        if ($null -eq $entry -or $entry -notmatch "^$([regex]::Escape($Prefix))(\d+)=(.*)$") {
            continue
        }
        $indexedValues += [pscustomobject]@{ Index = [int]$Matches[1]; Value = $Matches[2] }
    }
    return @($indexedValues | Sort-Object Index | Select-Object -ExpandProperty Value)
}

function ConvertTo-GroupList {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return @()
    }
    return @($Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Resolve-AuthMode {
    param([string]$RequestedMode, [string]$ExistingMode)

    $resolvedMode = if (-not [string]::IsNullOrWhiteSpace($RequestedMode)) {
        $RequestedMode.Trim()
    }
    elseif (-not [string]::IsNullOrWhiteSpace($ExistingMode)) {
        $ExistingMode.Trim()
    }
    else {
        throw 'Fresh production service installation requires an explicit AuthMode. Choose local-break-glass, oidc, or hybrid.'
    }

    if ($resolvedMode -notin @('local-break-glass', 'oidc', 'hybrid')) {
        throw "Resolved auth mode '$resolvedMode' is invalid."
    }
    return $resolvedMode
}

function Resolve-DryRunOnlyMode {
    param(
        [Parameter(Mandatory)]
        [string[]]$ServiceNames,
        [Parameter(Mandatory)]
        [bool]$DryRunOnlyRequested,
        [Parameter(Mandatory)]
        [bool]$LiveWritesRequested
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
        $existingValue = Get-ServiceEnvironmentValue -ServiceNames @($serviceName) -Name 'SyncFactors__Runtime__DryRunOnly'
        if ([string]::IsNullOrWhiteSpace($existingValue)) {
            continue
        }

        $parsedValue = $false
        if (-not [bool]::TryParse($existingValue, [ref]$parsedValue)) {
            throw "Service '$serviceName' has invalid SyncFactors__Runtime__DryRunOnly value '$existingValue'. Re-run with -DryRunOnly or -EnableLiveWrites."
        }

        $existingValues += [pscustomobject]@{ ServiceName = $serviceName; Value = $parsedValue }
    }

    $distinctValues = @($existingValues | Select-Object -ExpandProperty Value -Unique)
    if ($distinctValues.Count -gt 1) {
        throw 'The installed API and worker disagree on SyncFactors__Runtime__DryRunOnly. Re-run with -DryRunOnly or -EnableLiveWrites to select one mode for both services.'
    }

    if ($distinctValues.Count -eq 1) {
        return [pscustomobject]@{ Value = [bool]$distinctValues[0]; Source = 'existing-service-environment' }
    }

    return [pscustomobject]@{ Value = $true; Source = 'production-safe-default' }
}

function New-RandomDeploymentSecret {
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48)
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Resolve-SecurityAuditIntegrityKey {
    param(
        [Parameter(Mandatory)]
        [string[]]$ServiceNames,
        [string]$RequestedKey,
        [Parameter(Mandatory)]
        [string]$AuditLogPath
    )

    $existingKeys = @()
    foreach ($serviceName in $ServiceNames) {
        $existingKey = Get-ServiceEnvironmentValue `
            -ServiceNames @($serviceName) `
            -Name 'SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY'
        if (-not [string]::IsNullOrWhiteSpace($existingKey)) {
            $existingKeys += $existingKey
        }
    }

    $distinctExistingKeys = @($existingKeys | Select-Object -Unique)
    if ($distinctExistingKeys.Count -gt 1) {
        throw 'The installed API and worker have different security audit integrity keys. Restore the correct shared key before reinstalling.'
    }

    $existingKey = if ($distinctExistingKeys.Count -eq 1) { [string]$distinctExistingKeys[0] } else { $null }
    $environmentKey = $env:SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY

    if (-not [string]::IsNullOrWhiteSpace($RequestedKey) -and
        -not [string]::IsNullOrWhiteSpace($environmentKey) -and
        $RequestedKey -cne $environmentKey) {
        throw 'SecurityAuditIntegrityKey differs from SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY in the current process.'
    }

    foreach ($candidate in @($RequestedKey, $environmentKey)) {
        if (-not [string]::IsNullOrWhiteSpace($existingKey) -and
            -not [string]::IsNullOrWhiteSpace($candidate) -and
            $existingKey -cne $candidate) {
            throw 'The supplied security audit integrity key does not match the installed service key. Key rotation requires an explicit audit migration and is not performed by the service installer.'
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($existingKey)) {
        return [pscustomobject]@{ Value = $existingKey; Source = 'existing-service-environment' }
    }

    if (-not [string]::IsNullOrWhiteSpace($RequestedKey)) {
        return [pscustomobject]@{ Value = $RequestedKey; Source = 'parameter' }
    }

    if (-not [string]::IsNullOrWhiteSpace($environmentKey)) {
        return [pscustomobject]@{ Value = $environmentKey; Source = 'environment' }
    }

    if ((Test-Path -Path $AuditLogPath -PathType Leaf) -and (Get-Item -Path $AuditLogPath).Length -gt 0) {
        throw "Existing security audit state was found at '$AuditLogPath', but no recoverable SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY was supplied or found in either service environment. Restore the original key before reinstalling."
    }

    return [pscustomobject]@{ Value = (New-RandomDeploymentSecret); Source = 'generated' }
}

function Test-SqliteDatabaseIsPlaintext {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        return $false
    }

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        if ($stream.Length -lt 16) {
            return $false
        }

        $bytes = [byte[]]::new(16)
        [void]$stream.Read($bytes, 0, $bytes.Length)
        $header = [System.Text.Encoding]::ASCII.GetString($bytes)
        return $header.StartsWith('SQLite format 3', [System.StringComparison]::Ordinal)
    }
    finally {
        $stream.Dispose()
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
    Protect-ServiceRegistryKey -Name $Name
    Set-ServiceRecoveryPolicy -Name $Name
}

if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'SyncFactors Windows services can only be installed on Windows.'
}

if (-not (Test-IsWindowsAdministrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

if (-not [string]::IsNullOrWhiteSpace($DeploymentSecretsFile)) {
    if ($PSBoundParameters.ContainsKey('Credential') -or
        $PSBoundParameters.ContainsKey('SqlitePassword') -or
        $PSBoundParameters.ContainsKey('SecurityAuditIntegrityKey')) {
        throw 'DeploymentSecretsFile cannot be combined with Credential, SqlitePassword, or SecurityAuditIntegrityKey.'
    }
    $deploymentSecrets = Import-DeploymentSecrets -Path $DeploymentSecretsFile
    $Credential = $deploymentSecrets.Credential
    $SqlitePassword = $deploymentSecrets.SqlitePassword
    $SecurityAuditIntegrityKey = $deploymentSecrets.SecurityAuditIntegrityKey
}

$serviceIdentity = Assert-ServiceIdentityConfiguration `
    -RuntimeCredential $Credential `
    -LocalSystemAllowed $AllowLocalSystem.IsPresent
if ($AllowLocalSystem.IsPresent) {
    Write-Warning 'Installing SyncFactors services as LocalSystem by explicit override. Use a restricted runtime service account for production.'
}

$existingServiceEnvironments = @{}
$existingServiceEnvironments[$ApiServiceName] = @(Get-ServiceEnvironmentEntries -ServiceName $ApiServiceName)
$existingServiceEnvironments[$WorkerServiceName] = @(Get-ServiceEnvironmentEntries -ServiceName $WorkerServiceName)

$resolvedBundleRoot = Resolve-BundleRoot -Path $BundleRoot

$writeSafetyMode = Resolve-DryRunOnlyMode `
    -ServiceNames @($ApiServiceName, $WorkerServiceName) `
    -DryRunOnlyRequested $DryRunOnly.IsPresent `
    -LiveWritesRequested $EnableLiveWrites.IsPresent

if ([string]::IsNullOrWhiteSpace($DeploymentNonce)) {
    if ($Service -eq 'All') {
        $DeploymentNonce = New-RandomDeploymentSecret
    }
    else {
        $existingNonces = @(
            @($ApiServiceName, $WorkerServiceName) |
                ForEach-Object {
                    Get-ServiceEnvironmentValue -ServiceNames @($_) -Name 'SyncFactors__Deployment__Nonce'
                } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Select-Object -Unique
        )
        if ($existingNonces.Count -gt 1) {
            throw 'The installed API and worker have different deployment nonces. Reinstall both services together to restore deployment attestation.'
        }

        $DeploymentNonce = if ($existingNonces.Count -eq 1) {
            [string]$existingNonces[0]
        }
        else {
            New-RandomDeploymentSecret
        }
    }
}
$DeploymentNonce = $DeploymentNonce.Trim()
if ($DeploymentNonce.Length -lt 32) {
    throw 'DeploymentNonce must contain at least 32 characters.'
}

$runtimeRoot = Join-Path $resolvedBundleRoot 'state'
if ([string]::IsNullOrWhiteSpace($SqlitePath)) {
    $SqlitePath = Join-Path $runtimeRoot 'runtime\syncfactors.db'
}
if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $LogDirectory = Join-Path $runtimeRoot 'logs'
}
if ([string]::IsNullOrWhiteSpace($SecurityAuditLogPath)) {
    $existingAuditLogPath = Get-ServiceEnvironmentValue `
        -ServiceNames @($ApiServiceName, $WorkerServiceName) `
        -Name 'SYNCFACTORS_SECURITY_AUDIT_LOG_PATH'
    $SecurityAuditLogPath = if ([string]::IsNullOrWhiteSpace($existingAuditLogPath)) {
        Join-Path $runtimeRoot 'runtime\security-audit.jsonl'
    }
    else {
        $existingAuditLogPath
    }
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
$existingCommitMarkerPaths = @(
    @($ApiServiceName, $WorkerServiceName) |
        ForEach-Object { Get-ServiceEnvironmentValue -ServiceNames @($_) -Name 'SyncFactors__Deployment__CommitMarkerPath' } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [System.IO.Path]::GetFullPath($_) } |
        Select-Object -Unique)
if ($existingCommitMarkerPaths.Count -gt 1) {
    throw 'The installed API and worker have different deployment commit-marker paths.'
}
if ([string]::IsNullOrWhiteSpace($DeploymentCommitMarkerPath)) {
    $DeploymentCommitMarkerPath = if ($existingCommitMarkerPaths.Count -eq 1) {
        [string]$existingCommitMarkerPaths[0]
    }
    else {
        "$SqlitePath.deployment-commit"
    }
}
$DeploymentCommitMarkerPath = [System.IO.Path]::GetFullPath($DeploymentCommitMarkerPath)
$SecurityAuditLogPath = [System.IO.Path]::GetFullPath($SecurityAuditLogPath)
$LogDirectory = [System.IO.Path]::GetFullPath($LogDirectory)

$existingApiEnvironment = @($existingServiceEnvironments[$ApiServiceName])
$existingMode = Get-EnvironmentEntryValue -Entries $existingApiEnvironment -Name 'SYNCFACTORS__AUTH__MODE'
$resolvedAuthMode = Resolve-AuthMode -RequestedMode $AuthMode -ExistingMode $existingMode

$resolvedOidcAuthority = if (-not [string]::IsNullOrWhiteSpace($OidcAuthority)) {
    $OidcAuthority.Trim()
}
else {
    Get-EnvironmentEntryValue -Entries $existingApiEnvironment -Name 'SYNCFACTORS__AUTH__OIDC__AUTHORITY'
}
$resolvedOidcClientId = if (-not [string]::IsNullOrWhiteSpace($OidcClientId)) {
    $OidcClientId.Trim()
}
else {
    Get-EnvironmentEntryValue -Entries $existingApiEnvironment -Name 'SYNCFACTORS__AUTH__OIDC__CLIENTID'
}
$resolvedViewerGroups = @(if ($PSBoundParameters.ContainsKey('OidcViewerGroups')) {
    ConvertTo-GroupList -Value $OidcViewerGroups
}
else {
    Get-IndexedEnvironmentEntryValues -Entries $existingApiEnvironment -Prefix 'SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__'
})
$resolvedOperatorGroups = @(if ($PSBoundParameters.ContainsKey('OidcOperatorGroups')) {
    ConvertTo-GroupList -Value $OidcOperatorGroups
}
else {
    Get-IndexedEnvironmentEntryValues -Entries $existingApiEnvironment -Prefix 'SYNCFACTORS__AUTH__OIDC__OPERATORGROUPS__'
})
$resolvedAdminGroups = @(if ($PSBoundParameters.ContainsKey('OidcAdminGroups')) {
    ConvertTo-GroupList -Value $OidcAdminGroups
}
else {
    Get-IndexedEnvironmentEntryValues -Entries $existingApiEnvironment -Prefix 'SYNCFACTORS__AUTH__OIDC__ADMINGROUPS__'
})
$resolvedBootstrapAdminUsername = if (-not [string]::IsNullOrWhiteSpace($BootstrapAdminUsername)) {
    $BootstrapAdminUsername.Trim()
}
else {
    Get-EnvironmentEntryValue -Entries $existingApiEnvironment -Name 'SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME'
}

if ($resolvedAuthMode -in @('oidc', 'hybrid')) {
    if ([string]::IsNullOrWhiteSpace($resolvedOidcAuthority) -or
        [string]::IsNullOrWhiteSpace($resolvedOidcClientId) -or
        ($resolvedViewerGroups.Count + $resolvedOperatorGroups.Count + $resolvedAdminGroups.Count) -lt 1) {
        throw 'OIDC and hybrid auth require OidcAuthority, OidcClientId, and at least one viewer/operator/admin role group.'
    }
}

$databaseIsFresh = -not (Test-Path -Path $SqlitePath -PathType Leaf) -or (Get-Item -Path $SqlitePath).Length -eq 0
if ($databaseIsFresh -and
    $resolvedAuthMode -in @('local-break-glass', 'hybrid') -and
    [string]::IsNullOrWhiteSpace($resolvedBootstrapAdminUsername)) {
    throw 'A fresh local-break-glass or hybrid deployment requires BootstrapAdminUsername. Store the matching bootstrap password under the runtime identity in Windows Credential Manager before starting services.'
}

$auditIntegrity = Resolve-SecurityAuditIntegrityKey `
    -ServiceNames @($ApiServiceName, $WorkerServiceName) `
    -RequestedKey $SecurityAuditIntegrityKey `
    -AuditLogPath $SecurityAuditLogPath

Initialize-LocalConfig -Root $resolvedBundleRoot

New-Item -Path (Split-Path -Path $SqlitePath -Parent) -ItemType Directory -Force | Out-Null
New-Item -Path (Split-Path -Path $SecurityAuditLogPath -Parent) -ItemType Directory -Force | Out-Null
New-Item -Path $LogDirectory -ItemType Directory -Force | Out-Null

$sqlitePasswordSource = 'disabled'
if ($DisableSqliteEncryption.IsPresent) {
    if (-not [string]::IsNullOrWhiteSpace($SqlitePassword)) {
        throw 'Do not pass -SqlitePassword when -DisableSqliteEncryption is set.'
    }

    if ((Test-Path -Path $SqlitePath -PathType Leaf) -and -not (Test-SqliteDatabaseIsPlaintext -Path $SqlitePath)) {
        throw "SQLite encryption cannot be disabled because '$SqlitePath' does not look like a plaintext SQLite database."
    }
}
else {
    if (-not [string]::IsNullOrWhiteSpace($SqlitePassword)) {
        $sqlitePasswordSource = 'parameter'
    }

    if ([string]::IsNullOrWhiteSpace($SqlitePassword)) {
        $existingSqlitePassword = Get-ServiceEnvironmentValue -ServiceNames @($ApiServiceName, $WorkerServiceName) -Name 'SYNCFACTORS_SQLITE_PASSWORD'
        if (-not [string]::IsNullOrWhiteSpace($existingSqlitePassword)) {
            $SqlitePassword = $existingSqlitePassword
            $sqlitePasswordSource = 'existing-service-environment'
        }
    }

    if ([string]::IsNullOrWhiteSpace($SqlitePassword)) {
        $SqlitePassword = $env:SYNCFACTORS_SQLITE_PASSWORD
        if (-not [string]::IsNullOrWhiteSpace($SqlitePassword)) {
            $sqlitePasswordSource = 'environment'
        }
    }

    if ([string]::IsNullOrWhiteSpace($SqlitePassword)) {
        if ((Test-Path -Path $SqlitePath -PathType Leaf) -and -not (Test-SqliteDatabaseIsPlaintext -Path $SqlitePath)) {
            throw "Existing SQLite database '$SqlitePath' does not look plaintext and no SQLCipher password was supplied or found in the existing service environment. Re-run with -SqlitePassword or set SYNCFACTORS_SQLITE_PASSWORD to the original database password."
        }

        $SqlitePassword = New-RandomDeploymentSecret
        $sqlitePasswordSource = 'generated'
    }
}

$commonEnvironment = @(
    "DOTNET_ENVIRONMENT=Production",
    "SYNCFACTORS_RUN_PROFILE=$RunProfile",
    "SyncFactors__Runtime__DryRunOnly=$($writeSafetyMode.Value.ToString().ToLowerInvariant())",
    "SyncFactors__Deployment__Nonce=$DeploymentNonce",
    "SyncFactors__Deployment__CommitMarkerPath=$DeploymentCommitMarkerPath",
    "SyncFactors__ConfigPath=$ConfigPath",
    "SyncFactors__MappingConfigPath=$MappingConfigPath",
    "SyncFactors__SqlitePath=$SqlitePath",
    "SYNCFACTORS_SECURITY_AUDIT_LOG_PATH=$SecurityAuditLogPath",
    "SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY=$($auditIntegrity.Value)",
    "SYNCFACTORS_LOCAL_FILE_LOGGING_ENABLED=true",
    "SYNCFACTORS_LOCAL_LOG_DIRECTORY=$LogDirectory",
    "SYNCFACTORS_LOCAL_LOG_RETENTION_DAYS=7",
    "SYNCFACTORS_RUN_FILE_LOGGING_ENABLED=false",
    "Logging__LogLevel__Default=Information",
    "Logging__LogLevel__Microsoft=Warning",
    "Logging__LogLevel__Microsoft.Hosting.Lifetime=Information",
    "Logging__LogLevel__SyncFactors=Information",
    "Logging__EventLog__LogLevel__Default=Information",
    "Logging__EventLog__LogLevel__Microsoft=Warning",
    "Logging__EventLog__LogLevel__Microsoft.Hosting.Lifetime=Information",
    "Logging__EventLog__LogLevel__SyncFactors=Information"
)
if (-not [string]::IsNullOrWhiteSpace($WindowsCredentialPrefix)) {
    $commonEnvironment += "SYNCFACTORS_WINDOWS_CREDENTIAL_PREFIX=$WindowsCredentialPrefix"
}
if (-not [string]::IsNullOrWhiteSpace($SqlitePassword)) {
    $commonEnvironment += "SYNCFACTORS_SQLITE_PASSWORD=$SqlitePassword"
}

$apiEnvironment = @($commonEnvironment + @(
    "ASPNETCORE_ENVIRONMENT=Production",
    "ASPNETCORE_URLS=$ApiUrls",
    "SYNCFACTORS__AUTH__MODE=$resolvedAuthMode",
    "SYNCFACTORS__AUTH__LOCALBREAKGLASS__ENABLED=$(($resolvedAuthMode -in @('local-break-glass', 'hybrid')).ToString().ToLowerInvariant())"
))
if (-not [string]::IsNullOrWhiteSpace($resolvedBootstrapAdminUsername)) {
    $apiEnvironment += "SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME=$resolvedBootstrapAdminUsername"
}
if ($resolvedAuthMode -in @('oidc', 'hybrid')) {
    $apiEnvironment += "SYNCFACTORS__AUTH__OIDC__AUTHORITY=$resolvedOidcAuthority"
    $apiEnvironment += "SYNCFACTORS__AUTH__OIDC__CLIENTID=$resolvedOidcClientId"
    for ($i = 0; $i -lt $resolvedViewerGroups.Count; $i++) {
        $apiEnvironment += "SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__$i=$($resolvedViewerGroups[$i])"
    }
    for ($i = 0; $i -lt $resolvedOperatorGroups.Count; $i++) {
        $apiEnvironment += "SYNCFACTORS__AUTH__OIDC__OPERATORGROUPS__$i=$($resolvedOperatorGroups[$i])"
    }
    for ($i = 0; $i -lt $resolvedAdminGroups.Count; $i++) {
        $apiEnvironment += "SYNCFACTORS__AUTH__OIDC__ADMINGROUPS__$i=$($resolvedAdminGroups[$i])"
    }
}
if (-not [string]::IsNullOrWhiteSpace($TlsCertificatePath)) {
    $apiEnvironment += "ASPNETCORE_Kestrel__Certificates__Default__Path=$([System.IO.Path]::GetFullPath($TlsCertificatePath))"
}
if (-not [string]::IsNullOrWhiteSpace($TlsCertificatePassword)) {
    $apiEnvironment += "ASPNETCORE_Kestrel__Certificates__Default__Password=$TlsCertificatePassword"
}
if ([string]::IsNullOrWhiteSpace($TlsCertificatePath) -and
    [string]::IsNullOrWhiteSpace($TlsCertificatePassword)) {
    $machineCertificate = Find-SyncFactorsMachineCertificate -Urls $ApiUrls -Thumbprint $TlsCertificateThumbprint
    if ($null -ne $machineCertificate) {
        $apiEnvironment += "SyncFactors__Tls__MachineCertificateThumbprint=$($machineCertificate.Thumbprint)"
        $apiEnvironment += "SYNCFACTORS_TLS_CERT_THUMBPRINT=$($machineCertificate.Thumbprint)"
    }
}

$workerEnvironment = @($commonEnvironment)

$commonManagedEnvironmentNames = @(
    'DOTNET_ENVIRONMENT',
    'SYNCFACTORS_RUN_PROFILE',
    'SyncFactors__Runtime__DryRunOnly',
    'SyncFactors__Deployment__Nonce',
    'SyncFactors__Deployment__CommitMarkerPath',
    'SyncFactors__ConfigPath',
    'SyncFactors__MappingConfigPath',
    'SyncFactors__SqlitePath',
    'SYNCFACTORS_SECURITY_AUDIT_LOG_PATH',
    'SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY',
    'SYNCFACTORS_LOCAL_FILE_LOGGING_ENABLED',
    'SYNCFACTORS_LOCAL_LOG_DIRECTORY',
    'SYNCFACTORS_LOCAL_LOG_RETENTION_DAYS',
    'SYNCFACTORS_RUN_FILE_LOGGING_ENABLED',
    'Logging__LogLevel__Default',
    'Logging__LogLevel__Microsoft',
    'Logging__LogLevel__Microsoft.Hosting.Lifetime',
    'Logging__LogLevel__SyncFactors',
    'Logging__EventLog__LogLevel__Default',
    'Logging__EventLog__LogLevel__Microsoft',
    'Logging__EventLog__LogLevel__Microsoft.Hosting.Lifetime',
    'Logging__EventLog__LogLevel__SyncFactors',
    'SYNCFACTORS_WINDOWS_CREDENTIAL_PREFIX',
    'SYNCFACTORS_SQLITE_PASSWORD'
)
$apiManagedEnvironmentNames = @($commonManagedEnvironmentNames + @(
    'ASPNETCORE_ENVIRONMENT',
    'ASPNETCORE_URLS',
    'SYNCFACTORS__AUTH__MODE',
    'SYNCFACTORS__AUTH__LOCALBREAKGLASS__ENABLED',
    'SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME',
    'SYNCFACTORS__AUTH__OIDC__AUTHORITY',
    'SYNCFACTORS__AUTH__OIDC__CLIENTID',
    'ASPNETCORE_Kestrel__Certificates__Default__Path',
    'ASPNETCORE_Kestrel__Certificates__Default__Password',
    'SyncFactors__Tls__MachineCertificateThumbprint',
    'SYNCFACTORS_TLS_CERT_THUMBPRINT'
))
$apiManagedEnvironmentNames += @(
    @($existingApiEnvironment) |
        ForEach-Object {
            if ($null -ne $_ -and $_ -match '^(SYNCFACTORS__AUTH__OIDC__(VIEWER|OPERATOR|ADMIN)GROUPS__\d+)=') {
                $Matches[1]
            }
        }
)
$apiManagedEnvironmentNames += @(
    @($apiEnvironment) |
        ForEach-Object {
            if ($_ -match '^(SYNCFACTORS__AUTH__OIDC__(VIEWER|OPERATOR|ADMIN)GROUPS__\d+)=') {
                $Matches[1]
            }
        }
)

$apiEnvironment = Merge-ServiceEnvironmentEntries `
    -ExistingEntries $existingServiceEnvironments[$ApiServiceName] `
    -ManagedEntries $apiEnvironment `
    -ManagedNames $apiManagedEnvironmentNames
$workerEnvironment = Merge-ServiceEnvironmentEntries `
    -ExistingEntries $existingServiceEnvironments[$WorkerServiceName] `
    -ManagedEntries $workerEnvironment `
    -ManagedNames $commonManagedEnvironmentNames

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
    authMode = $resolvedAuthMode
    dryRunOnly = $writeSafetyMode.Value
    writeSafetySource = $writeSafetyMode.Source
    apiServiceName = $ApiServiceName
    workerServiceName = $WorkerServiceName
    configPath = $ConfigPath
    mappingConfigPath = $MappingConfigPath
    sqlitePath = $SqlitePath
    sqliteEncryption = if ([string]::IsNullOrWhiteSpace($SqlitePassword)) { 'disabled' } else { 'enabled' }
    sqlitePasswordSource = $sqlitePasswordSource
    securityAuditLogPath = $SecurityAuditLogPath
    securityAuditIntegrityKeySource = $auditIntegrity.Source
    serviceIdentity = $serviceIdentity
    logDirectory = $LogDirectory
    eventLog = 'Application'
}
