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
    [string]$ApiUrls = 'https://0.0.0.0:5087',
    [string]$TlsCertificatePath,
    [string]$TlsCertificatePassword,
    [string]$TlsCertificateThumbprint,
    [string]$WindowsCredentialPrefix = 'SyncFactors',
    [string]$SqlitePath,
    [string]$SqlitePassword,
    [switch]$DisableSqliteEncryption,
    [string]$SecurityAuditLogPath,
    [string]$SecurityAuditIntegrityKey,
    [string]$DeploymentSecretsFile,
    [string]$ExpectedServiceIdentity,
    [pscredential]$Credential,
    [switch]$AllowLocalSystem,
    [switch]$InstallOrUpdateServices,
    [string]$HealthUrl = 'https://localhost:5087/readyz',
    [switch]$SkipHealthCheck,
    [switch]$NoRollbackOnFailure,
    [Parameter(DontShow)]
    [switch]$DeploymentLockAlreadyHeld
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

function Enter-SyncFactorsDeploymentLock {
    param([TimeSpan]$Timeout = [TimeSpan]::FromMinutes(30))

    $mutex = [Threading.Mutex]::new($false, 'Global\SyncFactors.WindowsDeployment')
    try {
        $acquired = $false
        try {
            $acquired = $mutex.WaitOne($Timeout)
        }
        catch [Threading.AbandonedMutexException] {
            $acquired = $true
        }

        if (-not $acquired) {
            throw "Timed out waiting $([int]$Timeout.TotalMinutes) minutes for another SyncFactors deployment to finish."
        }

        return $mutex
    }
    catch {
        $mutex.Dispose()
        throw
    }
}

function Exit-SyncFactorsDeploymentLock {
    param([Parameter(Mandatory)][Threading.Mutex]$Mutex)

    try {
        $Mutex.ReleaseMutex()
    }
    finally {
        $Mutex.Dispose()
    }
}

function Assert-HttpsHealthUrl {
    param([Parameter(Mandatory)][string]$Url)
    $uri = [Uri]$Url
    if ($uri.Scheme -ne 'https') {
        throw 'Production patch deployment requires an HTTPS HealthUrl so the deployment nonce is never sent over plaintext.'
    }
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

function Get-BundleReleaseCommit {
    param([Parameter(Mandatory)][string]$BundlePath)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($BundlePath)
    try {
        $manifestEntries = @(
            $archive.Entries |
                Where-Object { $_.FullName.Replace('\', '/') -ceq 'release-manifest.json' })
        if ($manifestEntries.Count -ne 1) {
            throw "Bundle must contain exactly one root release-manifest.json; found $($manifestEntries.Count)."
        }

        $reader = [System.IO.StreamReader]::new($manifestEntries[0].Open())
        try {
            $manifest = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }

        $commitProperty = $manifest.PSObject.Properties['commitSha']
        $commitSha = if ($null -eq $commitProperty) { $null } else { [string]$commitProperty.Value }
        if ([string]::IsNullOrWhiteSpace($commitSha) -or
            $commitSha -notmatch '^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$') {
            throw 'Bundle release-manifest.json must contain a full 40- or 64-character hexadecimal commitSha.'
        }
        return $commitSha
    }
    finally {
        $archive.Dispose()
    }
}

function Get-InstalledServiceIdentity {
    param([Parameter(Mandatory)][string[]]$ServiceNames)

    $identities = @()
    foreach ($serviceName in $ServiceNames) {
        $escapedName = $serviceName.Replace("'", "''")
        $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$escapedName'"
        if ($null -eq $service -or [string]::IsNullOrWhiteSpace($service.StartName)) {
            throw "Could not resolve the Windows service identity for '$serviceName'."
        }
        $identities += $service.StartName
    }

    $distinctIdentities = @($identities | Select-Object -Unique)
    if ($distinctIdentities.Count -ne 1) {
        throw 'The API and worker must run as the same restricted Windows service identity.'
    }

    return [string]$distinctIdentities[0]
}

function Test-IsLocalSystemIdentity {
    param([string]$Identity)
    return $Identity -in @('LocalSystem', 'NT AUTHORITY\SYSTEM', '.\LocalSystem')
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

function Assert-ExpectedServiceIdentity {
    param(
        [Parameter(Mandatory)][string]$InstalledIdentity,
        [Parameter(Mandatory)][string]$ExpectedIdentity
    )

    $normalizedInstalled = $InstalledIdentity.Trim()
    $normalizedExpected = $ExpectedIdentity.Trim()
    if ($normalizedInstalled.StartsWith('.\')) {
        $normalizedInstalled = "$env:COMPUTERNAME\$($normalizedInstalled.Substring(2))"
    }
    elseif (-not $normalizedInstalled.Contains('\') -and -not $normalizedInstalled.Contains('@')) {
        $normalizedInstalled = "$env:COMPUTERNAME\$normalizedInstalled"
    }
    if ($normalizedExpected.StartsWith('.\')) {
        $normalizedExpected = "$env:COMPUTERNAME\$($normalizedExpected.Substring(2))"
    }
    elseif (-not $normalizedExpected.Contains('\') -and -not $normalizedExpected.Contains('@')) {
        $normalizedExpected = "$env:COMPUTERNAME\$normalizedExpected"
    }
    if ([string]::Equals($normalizedInstalled, $normalizedExpected, [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    try {
        $installedSid = ([System.Security.Principal.NTAccount]::new($normalizedInstalled)).Translate(
            [System.Security.Principal.SecurityIdentifier]).Value
        $expectedSid = ([System.Security.Principal.NTAccount]::new($normalizedExpected)).Translate(
            [System.Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        throw "Could not validate installed service identity '$InstalledIdentity' against expected identity '$ExpectedIdentity': $($_.Exception.Message)"
    }

    if ($installedSid -cne $expectedSid) {
        throw "The installed service identity '$InstalledIdentity' does not match the expected restricted identity '$ExpectedIdentity'. Normal patch deployment does not replace service definitions."
    }
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

function Backup-SqliteState {
    param(
        [Parameter(Mandatory)][string]$DatabasePath,
        [Parameter(Mandatory)][string]$SnapshotRoot
    )

    New-Item -Path $SnapshotRoot -ItemType Directory -Force | Out-Null
    $files = @()
    foreach ($suffix in @('', '-wal', '-shm', '-journal')) {
        $sourcePath = "$DatabasePath$suffix"
        $snapshotName = if ([string]::IsNullOrEmpty($suffix)) { 'database' } else { "database$suffix" }
        $snapshotPath = Join-Path $SnapshotRoot $snapshotName
        $exists = Test-Path -Path $sourcePath -PathType Leaf
        if ($exists) {
            Copy-Item -Path $sourcePath -Destination $snapshotPath -Force
        }
        $files += [pscustomobject]@{
            path = $sourcePath
            snapshotPath = $snapshotPath
            existed = $exists
        }
    }

    return [pscustomobject]@{
        databasePath = $DatabasePath
        snapshotRoot = $SnapshotRoot
        files = $files
    }
}

function Restore-SqliteState {
    param([Parameter(Mandatory)]$Snapshot)

    $databaseParent = Split-Path -Path $Snapshot.databasePath -Parent
    if (-not (Test-Path -Path $databaseParent -PathType Container)) {
        New-Item -Path $databaseParent -ItemType Directory -Force | Out-Null
    }

    foreach ($file in @($Snapshot.files)) {
        if (Test-Path -Path $file.path -PathType Leaf) {
            Remove-Item -Path $file.path -Force
        }
        if ($file.existed) {
            if (-not (Test-Path -Path $file.snapshotPath -PathType Leaf)) {
                throw "SQLite rollback snapshot '$($file.snapshotPath)' is missing."
            }
            Copy-Item -Path $file.snapshotPath -Destination $file.path -Force
        }
    }
}

function Backup-DeploymentCommitMarker {
    param(
        [Parameter(Mandatory)][string]$MarkerPath,
        [Parameter(Mandatory)][string]$SnapshotRoot
    )

    $snapshotPath = Join-Path $SnapshotRoot 'deployment-commit-marker'
    $existed = Test-Path -Path $MarkerPath -PathType Leaf
    if ($existed) {
        Copy-Item -Path $MarkerPath -Destination $snapshotPath -Force
    }
    return [pscustomobject]@{ path = $MarkerPath; snapshotPath = $snapshotPath; existed = $existed }
}

function Restore-DeploymentCommitMarker {
    param([Parameter(Mandatory)]$Snapshot)

    if (Test-Path -Path $Snapshot.path -PathType Leaf) {
        Remove-Item -Path $Snapshot.path -Force
    }
    if ($Snapshot.existed) {
        Copy-Item -Path $Snapshot.snapshotPath -Destination $Snapshot.path -Force
    }
}

function Publish-DeploymentCommitMarker {
    param(
        [Parameter(Mandatory)][string]$MarkerPath,
        [Parameter(Mandatory)][string]$Nonce
    )

    $markerParent = Split-Path -Path $MarkerPath -Parent
    New-Item -Path $markerParent -ItemType Directory -Force | Out-Null
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digestBytes = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($Nonce.Trim()))
    }
    finally {
        $sha256.Dispose()
    }
    $digest = ([BitConverter]::ToString($digestBytes)).Replace('-', '').ToLowerInvariant()
    $temporaryPath = Join-Path $markerParent (".{0}.{1}.tmp" -f (Split-Path -Path $MarkerPath -Leaf), [Guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($temporaryPath, $digest, [Text.UTF8Encoding]::new($false))
        if (Test-Path -Path $MarkerPath -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $MarkerPath, $null)
        }
        else {
            [IO.File]::Move($temporaryPath, $MarkerPath)
        }
    }
    finally {
        Remove-Item -Path $temporaryPath -Force -ErrorAction SilentlyContinue
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

function Protect-ServiceRegistryKey {
    param([Parameter(Mandatory)][string]$ServiceName)

    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
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

function Set-ServiceEnvironmentValues {
    param(
        [Parameter(Mandatory)][string]$ServiceName,
        [Parameter(Mandatory)][string[]]$ManagedNames,
        [Parameter(Mandatory)][string[]]$ManagedEntries
    )

    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (-not (Test-Path -Path $serviceKey)) {
        throw "Service registry key '$serviceKey' was not found."
    }

    $managedNameSet = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $ManagedNames) {
        [void]$managedNameSet.Add($name)
    }

    $environment = Get-ServiceEnvironment -ServiceName $ServiceName
    $updated = @()
    foreach ($entry in @($environment.Values)) {
        if ($null -eq $entry) {
            continue
        }

        $separatorIndex = $entry.IndexOf('=')
        if ($separatorIndex -le 0 -or -not $managedNameSet.Contains($entry.Substring(0, $separatorIndex))) {
            $updated += $entry
        }
    }
    $updated += $ManagedEntries
    New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value $updated -Force | Out-Null
}

function ConvertTo-GroupList {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return @()
    }
    return @($Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Get-IndexedServiceEnvironmentValues {
    param(
        [Parameter(Mandatory)]$Environment,
        [Parameter(Mandatory)][string]$Prefix
    )

    $indexedValues = @()
    foreach ($entry in @($Environment.Values)) {
        if ($null -ne $entry -and $entry -match "^$([regex]::Escape($Prefix))(\d+)=(.*)$") {
            $indexedValues += [pscustomobject]@{ Index = [int]$Matches[1]; Value = $Matches[2] }
        }
    }
    return @($indexedValues | Sort-Object Index | Select-Object -ExpandProperty Value)
}

function Resolve-PatchAuthEnvironment {
    param(
        [Parameter(Mandatory)]$ExistingEnvironment,
        [System.Collections.IDictionary]$RequestedParameters
    )

    $authParameterNames = @(
        'AuthMode',
        'OidcAuthority',
        'OidcClientId',
        'OidcViewerGroups',
        'OidcOperatorGroups',
        'OidcAdminGroups',
        'BootstrapAdminUsername')
    $updateRequested = @($authParameterNames | Where-Object { $RequestedParameters.ContainsKey($_) }).Count -gt 0

    function Get-ExistingValue([string]$Name) {
        foreach ($entry in @($ExistingEnvironment.Values)) {
            if ($null -ne $entry -and $entry.StartsWith("$Name=", [System.StringComparison]::OrdinalIgnoreCase)) {
                return $entry.Substring($Name.Length + 1)
            }
        }
        return $null
    }

    $mode = if ($RequestedParameters.ContainsKey('AuthMode')) {
        [string]$RequestedParameters.AuthMode
    }
    else {
        Get-ExistingValue -Name 'SYNCFACTORS__AUTH__MODE'
    }
    if ([string]::IsNullOrWhiteSpace($mode)) {
        throw 'Patch preflight requires an existing explicit AuthMode or an AuthMode parameter before staging or services are modified.'
    }
    $mode = $mode.Trim().ToLowerInvariant()
    if ($mode -notin @('local-break-glass', 'oidc', 'hybrid')) {
        throw "Patch preflight resolved invalid AuthMode '$mode'."
    }
    $authority = if ($RequestedParameters.ContainsKey('OidcAuthority')) { [string]$RequestedParameters.OidcAuthority } else { Get-ExistingValue 'SYNCFACTORS__AUTH__OIDC__AUTHORITY' }
    $clientId = if ($RequestedParameters.ContainsKey('OidcClientId')) { [string]$RequestedParameters.OidcClientId } else { Get-ExistingValue 'SYNCFACTORS__AUTH__OIDC__CLIENTID' }
    $bootstrapUsername = if ($RequestedParameters.ContainsKey('BootstrapAdminUsername')) { [string]$RequestedParameters.BootstrapAdminUsername } else { Get-ExistingValue 'SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME' }
    $viewerGroups = @(if ($RequestedParameters.ContainsKey('OidcViewerGroups')) { ConvertTo-GroupList $RequestedParameters.OidcViewerGroups } else { Get-IndexedServiceEnvironmentValues -Environment $ExistingEnvironment -Prefix 'SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__' })
    $operatorGroups = @(if ($RequestedParameters.ContainsKey('OidcOperatorGroups')) { ConvertTo-GroupList $RequestedParameters.OidcOperatorGroups } else { Get-IndexedServiceEnvironmentValues -Environment $ExistingEnvironment -Prefix 'SYNCFACTORS__AUTH__OIDC__OPERATORGROUPS__' })
    $adminGroups = @(if ($RequestedParameters.ContainsKey('OidcAdminGroups')) { ConvertTo-GroupList $RequestedParameters.OidcAdminGroups } else { Get-IndexedServiceEnvironmentValues -Environment $ExistingEnvironment -Prefix 'SYNCFACTORS__AUTH__OIDC__ADMINGROUPS__' })

    if ($mode -in @('oidc', 'hybrid') -and
        ([string]::IsNullOrWhiteSpace($authority) -or
        [string]::IsNullOrWhiteSpace($clientId) -or
        ($viewerGroups.Count + $operatorGroups.Count + $adminGroups.Count) -lt 1)) {
        throw 'OIDC and hybrid patch preflight requires Authority, ClientId, and at least one viewer/operator/admin role group.'
    }
    if (-not $updateRequested) {
        return [pscustomobject]@{ UpdateRequested = $false; ManagedNames = @(); ManagedEntries = @() }
    }

    $managedNames = @(
        'SYNCFACTORS__AUTH__MODE',
        'SYNCFACTORS__AUTH__LOCALBREAKGLASS__ENABLED',
        'SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME',
        'SYNCFACTORS__AUTH__OIDC__AUTHORITY',
        'SYNCFACTORS__AUTH__OIDC__CLIENTID')
    foreach ($entry in @($ExistingEnvironment.Values)) {
        if ($null -ne $entry -and $entry -match '^(SYNCFACTORS__AUTH__OIDC__(VIEWER|OPERATOR|ADMIN)GROUPS__\d+)=') {
            $managedNames += $Matches[1]
        }
    }

    $managedEntries = @(
        "SYNCFACTORS__AUTH__MODE=$mode",
        "SYNCFACTORS__AUTH__LOCALBREAKGLASS__ENABLED=$(($mode -in @('local-break-glass', 'hybrid')).ToString().ToLowerInvariant())")
    if (-not [string]::IsNullOrWhiteSpace($bootstrapUsername)) {
        $managedEntries += "SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME=$($bootstrapUsername.Trim())"
    }
    if ($mode -in @('oidc', 'hybrid')) {
        $managedEntries += "SYNCFACTORS__AUTH__OIDC__AUTHORITY=$($authority.Trim())"
        $managedEntries += "SYNCFACTORS__AUTH__OIDC__CLIENTID=$($clientId.Trim())"
        foreach ($groupSet in @(
            @{ Prefix = 'SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__'; Values = $viewerGroups },
            @{ Prefix = 'SYNCFACTORS__AUTH__OIDC__OPERATORGROUPS__'; Values = $operatorGroups },
            @{ Prefix = 'SYNCFACTORS__AUTH__OIDC__ADMINGROUPS__'; Values = $adminGroups })) {
            for ($index = 0; $index -lt $groupSet.Values.Count; $index++) {
                $name = "$($groupSet.Prefix)$index"
                $managedNames += $name
                $managedEntries += "$name=$($groupSet.Values[$index])"
            }
        }
    }

    return [pscustomobject]@{
        UpdateRequested = $true
        ManagedNames = @($managedNames | Select-Object -Unique)
        ManagedEntries = $managedEntries
    }
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

function New-RandomDeploymentSecret {
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48)
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Resolve-SecurityAuditIntegrityKey {
    param(
        [Parameter(Mandatory)][string[]]$ServiceNames,
        [string]$RequestedKey,
        [Parameter(Mandatory)][string]$AuditLogPath
    )

    $existingKeys = @()
    foreach ($serviceName in $ServiceNames) {
        $existingKey = Get-ServiceEnvironmentValue `
            -ServiceName $serviceName `
            -Name 'SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY'
        if (-not [string]::IsNullOrWhiteSpace($existingKey)) {
            $existingKeys += $existingKey
        }
    }

    $distinctExistingKeys = @($existingKeys | Select-Object -Unique)
    if ($distinctExistingKeys.Count -gt 1) {
        throw 'The installed API and worker have different security audit integrity keys. Restore the correct shared key before patching.'
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
            throw 'The supplied security audit integrity key does not match the installed service key. Key rotation requires an explicit audit migration and is not performed by patch deployment.'
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
        throw "Existing security audit state was found at '$AuditLogPath', but no recoverable SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY was supplied or found in either service environment. Restore the original key before patching."
    }

    return [pscustomobject]@{ Value = (New-RandomDeploymentSecret); Source = 'generated' }
}

if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'SyncFactors Windows patch deployment can only run on Windows.'
}

if (-not (Test-IsWindowsAdministrator)) {
    throw 'Run this script from an elevated PowerShell session on the target SyncFactors server.'
}
if ($SkipHealthCheck.IsPresent) {
    throw 'SkipHealthCheck is not permitted for production patch deployment because attested readiness is a required commit gate.'
}
Assert-HttpsHealthUrl -Url $HealthUrl

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

$resolvedBundleZip = Resolve-RequiredPath -Path $BundleZip -Label 'Bundle zip'
$expectedReleaseCommit = Get-BundleReleaseCommit -BundlePath $resolvedBundleZip
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$deploymentMutex = $null
if (-not $DeploymentLockAlreadyHeld.IsPresent) {
    $deploymentMutex = Enter-SyncFactorsDeploymentLock
}
try {
$installedSqlitePaths = @(
    @($ApiServiceName, $WorkerServiceName) |
        ForEach-Object { Get-ServiceEnvironmentValue -ServiceName $_ -Name 'SyncFactors__SqlitePath' } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [System.IO.Path]::GetFullPath($_) } |
        Select-Object -Unique
)
if ($installedSqlitePaths.Count -gt 1) {
    throw 'The installed API and worker have different SyncFactors__SqlitePath values. Repair the service environments before patching.'
}
$installedSqlitePath = if ($installedSqlitePaths.Count -eq 1) { [string]$installedSqlitePaths[0] } else { $null }
if ([string]::IsNullOrWhiteSpace($SqlitePath)) {
    $SqlitePath = if ([string]::IsNullOrWhiteSpace($installedSqlitePath)) {
        Join-Path $InstallRoot 'state\runtime\syncfactors.db'
    }
    else {
        $installedSqlitePath
    }
}
$SqlitePath = [System.IO.Path]::GetFullPath($SqlitePath)
if (-not [string]::IsNullOrWhiteSpace($installedSqlitePath) -and
    -not [string]::Equals($SqlitePath, $installedSqlitePath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Requested SqlitePath '$SqlitePath' does not match installed service path '$installedSqlitePath'."
}
$installedCommitMarkerPaths = @(
    @($ApiServiceName, $WorkerServiceName) |
        ForEach-Object { Get-ServiceEnvironmentValue -ServiceName $_ -Name 'SyncFactors__Deployment__CommitMarkerPath' } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [System.IO.Path]::GetFullPath($_) } |
        Select-Object -Unique)
if ($installedCommitMarkerPaths.Count -gt 1) {
    throw 'The installed API and worker have different deployment commit-marker paths.'
}
if ([string]::IsNullOrWhiteSpace($DeploymentCommitMarkerPath)) {
    $DeploymentCommitMarkerPath = if ($installedCommitMarkerPaths.Count -eq 1) {
        [string]$installedCommitMarkerPaths[0]
    }
    else {
        "$SqlitePath.deployment-commit"
    }
}
$DeploymentCommitMarkerPath = [System.IO.Path]::GetFullPath($DeploymentCommitMarkerPath)
$existingAuditLogPath = $null
if ([string]::IsNullOrWhiteSpace($SecurityAuditLogPath)) {
    foreach ($serviceName in @($ApiServiceName, $WorkerServiceName)) {
        $existingAuditLogPath = Get-ServiceEnvironmentValue `
            -ServiceName $serviceName `
            -Name 'SYNCFACTORS_SECURITY_AUDIT_LOG_PATH'
        if (-not [string]::IsNullOrWhiteSpace($existingAuditLogPath)) {
            break
        }
    }

    $SecurityAuditLogPath = if ([string]::IsNullOrWhiteSpace($existingAuditLogPath)) {
        Join-Path $InstallRoot 'state\runtime\security-audit.jsonl'
    }
    else {
        $existingAuditLogPath
    }
}
$SecurityAuditLogPath = [System.IO.Path]::GetFullPath($SecurityAuditLogPath)
$stagingRoot = Join-Path $InstallRoot '_staging'
$backupRoot = Join-Path $InstallRoot '_backups'
$deploymentId = "{0}-{1}" -f (Get-Date -Format 'yyyyMMddHHmmss'), [Guid]::NewGuid().ToString('N')
$expandedRoot = Join-Path $stagingRoot $deploymentId
$currentBackupRoot = Join-Path $backupRoot $deploymentId
$currentSqliteBackupRoot = Join-Path $backupRoot ("{0}-sqlite" -f $deploymentId)
$sqliteSnapshot = $null
$commitMarkerSnapshot = $null
$services = @($ApiServiceName, $WorkerServiceName)
$runtimeServiceIdentity = if ($InstallOrUpdateServices.IsPresent) {
    if ($null -eq $Credential -and -not $AllowLocalSystem.IsPresent) {
        throw 'Credential is required when InstallOrUpdateServices is set because Windows service credentials cannot be recovered and LocalSystem is blocked by default. Pass -AllowLocalSystem only for an explicitly approved exceptional deployment.'
    }
    if ($null -ne $Credential -and $AllowLocalSystem.IsPresent) {
        throw 'Credential and AllowLocalSystem cannot be specified together.'
    }

    if ($null -ne $Credential) { $Credential.UserName } else { 'LocalSystem' }
}
else {
    $installedIdentity = Get-InstalledServiceIdentity -ServiceNames $services
    $identityToValidate = if (-not [string]::IsNullOrWhiteSpace($ExpectedServiceIdentity)) {
        $ExpectedServiceIdentity
    }
    elseif ($null -ne $Credential) {
        $Credential.UserName
    }
    else {
        $null
    }
    if (-not [string]::IsNullOrWhiteSpace($identityToValidate)) {
        Assert-ExpectedServiceIdentity -InstalledIdentity $installedIdentity -ExpectedIdentity $identityToValidate
    }
    $installedIdentity
}
if ((Test-IsLocalSystemIdentity -Identity $runtimeServiceIdentity) -and -not $AllowLocalSystem.IsPresent) {
    throw 'The installed services run as LocalSystem. Migrate them to a restricted runtime credential, or pass -AllowLocalSystem only for an explicitly approved exception.'
}
$writeSafetyMode = Resolve-DryRunOnlyMode `
    -ServiceNames $services `
    -DryRunOnlyRequested $DryRunOnly.IsPresent `
    -LiveWritesRequested $EnableLiveWrites.IsPresent
$auditIntegrity = Resolve-SecurityAuditIntegrityKey `
    -ServiceNames $services `
    -RequestedKey $SecurityAuditIntegrityKey `
    -AuditLogPath $SecurityAuditLogPath
if ([string]::IsNullOrWhiteSpace($DeploymentNonce)) {
    $DeploymentNonce = New-RandomDeploymentSecret
}
$DeploymentNonce = $DeploymentNonce.Trim()
if ($DeploymentNonce.Length -lt 32) {
    throw 'DeploymentNonce must contain at least 32 characters.'
}
$originalServiceEnvironments = @{}
foreach ($service in $services) {
    $environment = Get-ServiceEnvironment -ServiceName $service
    if ($null -eq $environment) {
        throw "Service registry environment for '$service' could not be read."
    }
    $originalServiceEnvironments[$service] = $environment
}
$authEnvironment = Resolve-PatchAuthEnvironment `
    -ExistingEnvironment $originalServiceEnvironments[$ApiServiceName] `
    -RequestedParameters $PSBoundParameters

New-Item -Path $expandedRoot -ItemType Directory -Force | Out-Null

try {
    Expand-Archive -Path $resolvedBundleZip -DestinationPath $expandedRoot -Force

    foreach ($requiredPath in @('app\api', 'app\worker', 'scripts', 'release-manifest.json')) {
        $candidate = Join-Path $expandedRoot $requiredPath
        if (-not (Test-Path -Path $candidate)) {
            throw "Bundle '$resolvedBundleZip' is missing required path '$requiredPath'."
        }
    }

    $prerequisitesScript = Join-Path $expandedRoot 'scripts\Install-SyncFactorsWindowsPrerequisites.ps1'
    $prerequisitesArgs = @(
        '-InstallRoot', $InstallRoot,
        '-RuntimeRoot', (Join-Path $InstallRoot 'state'),
        '-BackupRoot', $backupRoot)
    if (-not (Test-IsLocalSystemIdentity -Identity $runtimeServiceIdentity)) {
        $prerequisitesArgs += @('-ServiceAccount', $runtimeServiceIdentity)
    }
    if ($PSCmdlet.ShouldProcess($InstallRoot, 'Protect runtime state and rollback backups before snapshot creation')) {
        & $prerequisitesScript @prerequisitesArgs | Out-Host
    }
    foreach ($service in $services) {
        if ($PSCmdlet.ShouldProcess($service, 'Protect service registry environment for SYSTEM and Administrators')) {
            Protect-ServiceRegistryKey -ServiceName $service
        }
    }

    New-Item -Path $currentBackupRoot -ItemType Directory -Force | Out-Null

    foreach ($relativePath in @('app', 'scripts', 'docs', 'README.md', 'LICENSE', 'SECURITY.md', 'CONTRIBUTING.md', 'VERSION', 'release-manifest.json')) {
        Backup-Path -Root $InstallRoot -RelativePath $relativePath -BackupRoot $currentBackupRoot
    }

    foreach ($service in $services) {
        if ($PSCmdlet.ShouldProcess($service, 'Stop Windows service')) {
            Stop-SyncFactorsService -Name $service | Out-Null
        }
    }

    if ($PSCmdlet.ShouldProcess($SqlitePath, 'Snapshot SQLite database, WAL, SHM, and rollback-journal sidecars')) {
        $sqliteSnapshot = Backup-SqliteState `
            -DatabasePath $SqlitePath `
            -SnapshotRoot $currentSqliteBackupRoot
    }
    $commitMarkerSnapshot = Backup-DeploymentCommitMarker `
        -MarkerPath $DeploymentCommitMarkerPath `
        -SnapshotRoot $currentSqliteBackupRoot
    if (Test-Path -Path $DeploymentCommitMarkerPath -PathType Leaf) {
        Remove-Item -Path $DeploymentCommitMarkerPath -Force
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
        $installScript = Join-Path $InstallRoot 'scripts\Install-SyncFactorsWindowsServices.ps1'
        $installArgs = @(
            '-BundleRoot', $InstallRoot,
            '-RunProfile', $RunProfile,
            '-ApiUrls', $ApiUrls,
            '-ApiServiceName', $ApiServiceName,
            '-WorkerServiceName', $WorkerServiceName,
            '-WindowsCredentialPrefix', $WindowsCredentialPrefix,
            '-SqlitePath', $SqlitePath,
            '-DeploymentCommitMarkerPath', $DeploymentCommitMarkerPath,
            '-SecurityAuditIntegrityKey', $auditIntegrity.Value,
            '-DeploymentNonce', $DeploymentNonce,
            '-DeploymentLockAlreadyHeld',
            '-Force'
        )
        if ($null -ne $Credential) {
            $installArgs += @('-Credential', $Credential)
        }
        else {
            $installArgs += '-AllowLocalSystem'
        }
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
        $installArgs += @('-SecurityAuditLogPath', $SecurityAuditLogPath)
        if ($writeSafetyMode.Value) {
            $installArgs += '-DryRunOnly'
        }
        else {
            $installArgs += '-EnableLiveWrites'
        }
        foreach ($authParameterName in @(
            'AuthMode',
            'OidcAuthority',
            'OidcClientId',
            'OidcViewerGroups',
            'OidcOperatorGroups',
            'OidcAdminGroups',
            'BootstrapAdminUsername')) {
            if ($PSBoundParameters.ContainsKey($authParameterName)) {
                $installArgs += @("-$authParameterName", $PSBoundParameters[$authParameterName])
            }
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

    if ($authEnvironment.UpdateRequested -and
        $PSCmdlet.ShouldProcess($ApiServiceName, 'Apply rollback-protected managed authentication environment')) {
        Set-ServiceEnvironmentValues `
            -ServiceName $ApiServiceName `
            -ManagedNames $authEnvironment.ManagedNames `
            -ManagedEntries $authEnvironment.ManagedEntries
    }

    $dryRunOnlyValue = $writeSafetyMode.Value.ToString().ToLowerInvariant()
    foreach ($service in $services) {
        if ($PSCmdlet.ShouldProcess($service, "Set SyncFactors__Runtime__DryRunOnly=$dryRunOnlyValue")) {
            Set-ServiceEnvironmentValue `
                -ServiceName $service `
                -Name 'SyncFactors__Runtime__DryRunOnly' `
                -Value $dryRunOnlyValue
        }
        if ($PSCmdlet.ShouldProcess($service, 'Set security audit integrity key')) {
            Set-ServiceEnvironmentValue `
                -ServiceName $service `
                -Name 'SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY' `
                -Value $auditIntegrity.Value
        }
        if ($PSCmdlet.ShouldProcess($service, 'Rotate deployment attestation nonce')) {
            Set-ServiceEnvironmentValue `
                -ServiceName $service `
                -Name 'SyncFactors__Deployment__Nonce' `
                -Value $DeploymentNonce
        }
        if ($PSCmdlet.ShouldProcess($service, 'Set deployment commit-marker path')) {
            Set-ServiceEnvironmentValue `
                -ServiceName $service `
                -Name 'SyncFactors__Deployment__CommitMarkerPath' `
                -Value $DeploymentCommitMarkerPath
        }
    }

    $workerStartedAfter = [DateTimeOffset]::UtcNow.ToString('O')
    foreach ($service in $services) {
        if ($PSCmdlet.ShouldProcess($service, 'Start Windows service')) {
            Start-SyncFactorsService -Name $service | Out-Null
        }
    }

    $verificationScript = Join-Path $InstallRoot 'scripts\Test-SyncFactorsWindowsDeployment.ps1'
    $verificationArgs = @(
        '-ApiServiceName', $ApiServiceName,
        '-WorkerServiceName', $WorkerServiceName,
        '-HealthUrl', $HealthUrl,
        '-DeploymentNonce', $DeploymentNonce,
        '-WorkerStartedAfter', $workerStartedAfter
    )
    $verificationArgs += @('-ExpectedWorkerCommit', $expectedReleaseCommit)
    $verificationArgs += @('-ExpectedApiCommit', $expectedReleaseCommit)
    if (-not [string]::IsNullOrWhiteSpace($TlsCertificateThumbprint)) {
        $verificationArgs += @('-TlsCertificateThumbprint', $TlsCertificateThumbprint)
    }
    if ($writeSafetyMode.Value) {
        $verificationArgs += '-DryRunOnly'
    }
    else {
        $verificationArgs += '-EnableLiveWrites'
    }
    $verification = & $verificationScript @verificationArgs
    Publish-DeploymentCommitMarker -MarkerPath $DeploymentCommitMarkerPath -Nonce $DeploymentNonce

    [pscustomobject]@{
        bundleZip = $resolvedBundleZip
        installRoot = $InstallRoot
        stagingRoot = $expandedRoot
        backupRoot = $currentBackupRoot
        sqliteBackupRoot = $currentSqliteBackupRoot
        apiServiceName = $ApiServiceName
        workerServiceName = $WorkerServiceName
        serviceIdentity = $runtimeServiceIdentity
        dryRunOnly = $writeSafetyMode.Value
        writeSafetySource = $writeSafetyMode.Source
        securityAuditIntegrityKeySource = $auditIntegrity.Source
        healthUrl = if ($SkipHealthCheck.IsPresent) { $null } else { $HealthUrl }
        verification = $verification
        rollbackAvailable = $true
    }
}
catch {
    $failure = $_
    Write-Warning "SyncFactors patch deployment failed: $($failure.Exception.Message)"

    if (-not $NoRollbackOnFailure.IsPresent) {
        Write-Warning "Rolling back deployable files, service environments, and the SQLite snapshot."
        foreach ($service in $services) {
            Stop-SyncFactorsService -Name $service | Out-Null
        }

        if (Test-Path -Path $currentBackupRoot -PathType Container) {
            Restore-Backup -Root $InstallRoot -BackupRoot $currentBackupRoot
        }

        if ($null -ne $sqliteSnapshot) {
            Restore-SqliteState -Snapshot $sqliteSnapshot
        }
        if ($null -ne $commitMarkerSnapshot) {
            Restore-DeploymentCommitMarker -Snapshot $commitMarkerSnapshot
        }

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
}
finally {
    if ($null -ne $deploymentMutex) {
        Exit-SyncFactorsDeploymentLock -Mutex $deploymentMutex
    }
}
