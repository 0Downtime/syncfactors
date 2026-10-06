[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Import-ScriptFunction {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )

    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        throw "Could not parse '$Path': $($errors[0].Message)"
    }

    $definition = $ast.Find(
        { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name },
        $true)
    if ($null -eq $definition) {
        throw "Function '$Name' was not found in '$Path'."
    }

    $escapedName = [regex]::Escape($Name)
    $scriptDefinition = $definition.Extent.Text -replace "^function\s+$escapedName", "function script:$Name"
    Invoke-Expression $scriptDefinition
}

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal {
    param($Expected, $Actual, [Parameter(Mandatory)][string]$Message)
    if ($Expected -cne $Actual) {
        throw "$Message Expected '$Expected'; actual '$Actual'."
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$MessagePattern,
        [Parameter(Mandatory)][string]$Message
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike $MessagePattern) {
            throw "$Message Unexpected error: $($_.Exception.Message)"
        }
        return
    }

    throw "$Message The operation did not throw."
}

$root = (Resolve-Path -Path $RepositoryRoot).ProviderPath
$installerPath = Join-Path $root 'scripts/Install-SyncFactorsWindowsServices.ps1'
$patchPath = Join-Path $root 'scripts/Deploy-SyncFactorsWindowsPatch.ps1'

Import-ScriptFunction -Path $installerPath -Name 'Resolve-DryRunOnlyMode'
Import-ScriptFunction -Path $installerPath -Name 'New-RandomDeploymentSecret'
Import-ScriptFunction -Path $installerPath -Name 'Resolve-SecurityAuditIntegrityKey'
Import-ScriptFunction -Path $installerPath -Name 'Assert-ServiceIdentityConfiguration'
Import-ScriptFunction -Path $installerPath -Name 'Resolve-AuthMode'
Import-ScriptFunction -Path $installerPath -Name 'Merge-ServiceEnvironmentEntries'
Import-ScriptFunction -Path $installerPath -Name 'Get-IndexedEnvironmentEntryValues'
Import-ScriptFunction -Path $installerPath -Name 'ConvertFrom-ProtectedSecureString'
Import-ScriptFunction -Path $installerPath -Name 'Import-DeploymentSecrets'

$script:mockServiceValues = @{}
function Get-ServiceEnvironmentValue {
    param([string[]]$ServiceNames, [string]$Name)
    $key = "$($ServiceNames[0])|$Name"
    return $script:mockServiceValues[$key]
}

$safeMode = Resolve-DryRunOnlyMode `
    -ServiceNames @('Api', 'Worker') `
    -DryRunOnlyRequested $false `
    -LiveWritesRequested $false
Assert-True -Condition $safeMode.Value -Message 'Fresh deployments must default to dry-run-only.'
Assert-Equal -Expected 'production-safe-default' -Actual $safeMode.Source -Message 'Fresh mode source was wrong.'

$liveMode = Resolve-DryRunOnlyMode `
    -ServiceNames @('Api', 'Worker') `
    -DryRunOnlyRequested $false `
    -LiveWritesRequested $true
Assert-True -Condition (-not $liveMode.Value) -Message 'Explicit live-write opt-in was not honored.'

$script:mockServiceValues['Api|SyncFactors__Runtime__DryRunOnly'] = 'true'
$script:mockServiceValues['Worker|SyncFactors__Runtime__DryRunOnly'] = 'false'
Assert-Throws `
    -Action { Resolve-DryRunOnlyMode -ServiceNames @('Api', 'Worker') -DryRunOnlyRequested $false -LiveWritesRequested $false } `
    -MessagePattern '*disagree*' `
    -Message 'Mismatched installed write modes must be rejected.'
$script:mockServiceValues.Clear()

Assert-Throws `
    -Action { Resolve-AuthMode -RequestedMode $null -ExistingMode $null } `
    -MessagePattern '*requires an explicit AuthMode*' `
    -Message 'Fresh production auth must not fall back implicitly.'
Assert-Equal `
    -Expected 'hybrid' `
    -Actual (Resolve-AuthMode -RequestedMode $null -ExistingMode 'hybrid') `
    -Message 'Upgrade did not preserve the installed auth mode.'

$mergedEnvironment = @(Merge-ServiceEnvironmentEntries `
    -ExistingEntries @(
        'SyncFactors__Deployment__Nonce=old-nonce-value-that-is-long-enough',
        'SYNCFACTORS__AUTH__MODE=hybrid',
        'SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME=existing-admin',
        'SYNCFACTORS__AUTH__OIDC__CLIENTSECRET=credential-manager-override',
        'SYNCFACTORS__AUTH__IDLETIMEOUTMINUTES=240') `
    -ManagedEntries @(
        'SyncFactors__Deployment__Nonce=new-nonce-value-that-is-long-enough',
        'SYNCFACTORS__AUTH__MODE=hybrid',
        'SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME=existing-admin') `
    -ManagedNames @(
        'SyncFactors__Deployment__Nonce',
        'SYNCFACTORS__AUTH__MODE',
        'SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME'))
Assert-True -Condition ($mergedEnvironment -contains 'SyncFactors__Deployment__Nonce=new-nonce-value-that-is-long-enough') -Message 'Managed nonce was not rotated.'
Assert-True -Condition ($mergedEnvironment -notcontains 'SyncFactors__Deployment__Nonce=old-nonce-value-that-is-long-enough') -Message 'Old managed nonce survived merge.'
Assert-True -Condition ($mergedEnvironment -contains 'SYNCFACTORS__AUTH__OIDC__CLIENTSECRET=credential-manager-override') -Message 'Non-managed OIDC environment was not preserved.'
Assert-True -Condition ($mergedEnvironment -contains 'SYNCFACTORS__AUTH__IDLETIMEOUTMINUTES=240') -Message 'Non-managed auth tuning was not preserved.'
$indexedGroups = @(Get-IndexedEnvironmentEntryValues `
    -Entries @(
        'SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__1=second',
        'SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__0=first') `
    -Prefix 'SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__')
Assert-Equal -Expected 'first' -Actual $indexedGroups[0] -Message 'Indexed auth groups were not read in order.'
Assert-Equal -Expected 'second' -Actual $indexedGroups[1] -Message 'Indexed auth groups were not read in order.'

$handoffTestPath = Join-Path ([System.IO.Path]::GetTempPath()) ("syncfactors-handoff-{0}.clixml" -f [Guid]::NewGuid().ToString('N'))
$handoffPassword = "pa'ssword`nwith-unicode-雪"
$handoffCredential = [pscredential]::new(
    "CONTOSO\svc-o'connor",
    (ConvertTo-SecureString $handoffPassword -AsPlainText -Force))
[pscustomobject]@{
    Credential = $handoffCredential
    SqlitePassword = ConvertTo-SecureString "sqlite-'quoted'" -AsPlainText -Force
    SecurityAuditIntegrityKey = ConvertTo-SecureString 'audit-integrity-value' -AsPlainText -Force
} | Export-Clixml -Path $handoffTestPath
try {
    if ([System.OperatingSystem]::IsWindows()) {
        $handoffAcl = Get-Acl -Path $handoffTestPath
        $handoffAcl.SetAccessRuleProtection($true, $false)
        foreach ($existingRule in @($handoffAcl.Access)) {
            [void]$handoffAcl.RemoveAccessRuleSpecific($existingRule)
        }
        foreach ($sid in @(
            [System.Security.Principal.WindowsIdentity]::GetCurrent().User,
            [System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
            [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
            $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [System.Security.AccessControl.FileSystemRights]::FullControl,
                [System.Security.AccessControl.AccessControlType]::Allow)
            [void]$handoffAcl.AddAccessRule($rule)
        }
        Set-Acl -Path $handoffTestPath -AclObject $handoffAcl
    }

    $importedHandoff = Import-DeploymentSecrets -Path $handoffTestPath
    Assert-Equal -Expected "CONTOSO\svc-o'connor" -Actual $importedHandoff.Credential.UserName -Message 'Credential handoff corrupted an apostrophe in the username.'
    Assert-Equal -Expected $handoffPassword -Actual (ConvertFrom-ProtectedSecureString $importedHandoff.Credential.Password) -Message 'Credential handoff corrupted the password.'
    Assert-Equal -Expected "sqlite-'quoted'" -Actual $importedHandoff.SqlitePassword -Message 'SQLite secret handoff corrupted quotes.'
    Assert-True -Condition ((Get-Content -Path $handoffTestPath -Raw) -notlike "*$handoffPassword*") -Message 'Credential handoff serialized the raw password.'
}
finally {
    Remove-Item -Path $handoffTestPath -Force -ErrorAction SilentlyContinue
}

$originalIntegrityKey = [Environment]::GetEnvironmentVariable('SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY')
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("syncfactors-deployment-test-{0}" -f [Guid]::NewGuid().ToString('N'))
$auditPath = Join-Path $tempRoot 'security-audit.jsonl'
New-Item -Path $tempRoot -ItemType Directory -Force | Out-Null
try {
    [Environment]::SetEnvironmentVariable('SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY', $null)

    $generated = Resolve-SecurityAuditIntegrityKey `
        -ServiceNames @('Api', 'Worker') `
        -RequestedKey $null `
        -AuditLogPath $auditPath
    Assert-Equal -Expected 'generated' -Actual $generated.Source -Message 'Fresh audit key source was wrong.'
    Assert-True -Condition ($generated.Value.Length -ge 40) -Message 'Generated audit key was unexpectedly short.'

    $script:mockServiceValues['Api|SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY'] = 'stable-key'
    $script:mockServiceValues['Worker|SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY'] = 'stable-key'
    $preserved = Resolve-SecurityAuditIntegrityKey `
        -ServiceNames @('Api', 'Worker') `
        -RequestedKey $null `
        -AuditLogPath $auditPath
    Assert-Equal -Expected 'stable-key' -Actual $preserved.Value -Message 'Installed audit key was not preserved.'
    Assert-Equal -Expected 'existing-service-environment' -Actual $preserved.Source -Message 'Preserved audit key source was wrong.'

    Assert-Throws `
        -Action { Resolve-SecurityAuditIntegrityKey -ServiceNames @('Api', 'Worker') -RequestedKey 'rotated-key' -AuditLogPath $auditPath } `
        -MessagePattern '*does not match*' `
        -Message 'Silent audit key rotation must be rejected.'

    $script:mockServiceValues.Clear()
    Set-Content -Path $auditPath -Value '{"existing":true}'
    Assert-Throws `
        -Action { Resolve-SecurityAuditIntegrityKey -ServiceNames @('Api', 'Worker') -RequestedKey $null -AuditLogPath $auditPath } `
        -MessagePattern '*no recoverable*' `
        -Message 'Existing audit state without a recoverable key must be rejected.'

    $recovered = Resolve-SecurityAuditIntegrityKey `
        -ServiceNames @('Api', 'Worker') `
        -RequestedKey 'recovered-key' `
        -AuditLogPath $auditPath
    Assert-Equal -Expected 'recovered-key' -Actual $recovered.Value -Message 'Explicit audit key recovery failed.'
}
finally {
    [Environment]::SetEnvironmentVariable('SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY', $originalIntegrityKey)
    Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$dummyPassword = ConvertTo-SecureString 'not-a-real-secret' -AsPlainText -Force
$dummyCredential = [pscredential]::new('CONTOSO\svc-syncfactors', $dummyPassword)
Assert-Throws `
    -Action { Assert-ServiceIdentityConfiguration -RuntimeCredential $null -LocalSystemAllowed $false } `
    -MessagePattern '*LocalSystem is blocked by default*' `
    -Message 'Missing runtime credentials must fail closed.'
Assert-Equal `
    -Expected 'CONTOSO\svc-syncfactors' `
    -Actual (Assert-ServiceIdentityConfiguration -RuntimeCredential $dummyCredential -LocalSystemAllowed $false) `
    -Message 'Restricted runtime credential was not accepted.'
Assert-Equal `
    -Expected 'LocalSystem (explicit opt-in)' `
    -Actual (Assert-ServiceIdentityConfiguration -RuntimeCredential $null -LocalSystemAllowed $true) `
    -Message 'Explicit LocalSystem opt-in was not accepted.'
Assert-Throws `
    -Action { Assert-ServiceIdentityConfiguration -RuntimeCredential $dummyCredential -LocalSystemAllowed $true } `
    -MessagePattern '*cannot be specified together*' `
    -Message 'Ambiguous runtime identity configuration must be rejected.'

Import-ScriptFunction -Path $patchPath -Name 'Set-ServiceEnvironmentValue'
Import-ScriptFunction -Path $patchPath -Name 'Set-ServiceEnvironmentValues'
Import-ScriptFunction -Path $patchPath -Name 'Restore-ServiceEnvironment'
Import-ScriptFunction -Path $patchPath -Name 'ConvertTo-GroupList'
Import-ScriptFunction -Path $patchPath -Name 'Get-IndexedServiceEnvironmentValues'
Import-ScriptFunction -Path $patchPath -Name 'Resolve-PatchAuthEnvironment'
Import-ScriptFunction -Path $patchPath -Name 'Backup-SqliteState'
Import-ScriptFunction -Path $patchPath -Name 'Restore-SqliteState'
Import-ScriptFunction -Path $patchPath -Name 'Get-BundleReleaseCommit'
Import-ScriptFunction -Path $patchPath -Name 'Backup-DeploymentCommitMarker'
Import-ScriptFunction -Path $patchPath -Name 'Restore-DeploymentCommitMarker'
Import-ScriptFunction -Path $patchPath -Name 'Publish-DeploymentCommitMarker'
Import-ScriptFunction -Path $patchPath -Name 'Assert-HttpsHealthUrl'
Assert-Throws `
    -Action { Assert-HttpsHealthUrl -Url 'http://localhost:5087/readyz' } `
    -MessagePattern '*requires an HTTPS HealthUrl*' `
    -Message 'Patch deployment must reject plaintext readiness URLs before mutation.'
Assert-HttpsHealthUrl -Url 'https://localhost:5087/readyz'

$legacyEnvironment = [pscustomobject]@{ Exists = $true; Values = @('DOTNET_ENVIRONMENT=Production') }
Assert-Throws `
    -Action { Resolve-PatchAuthEnvironment -ExistingEnvironment $legacyEnvironment -RequestedParameters @{} } `
    -MessagePattern '*requires an existing explicit AuthMode*' `
    -Message 'A legacy patch with no explicit production auth mode must fail during preflight.'

$configuredEnvironment = [pscustomobject]@{ Exists = $true; Values = @(
    'SYNCFACTORS__AUTH__MODE=hybrid',
    'SYNCFACTORS__AUTH__OIDC__AUTHORITY=https://login.example.test',
    'SYNCFACTORS__AUTH__OIDC__CLIENTID=configured-client',
    'SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__0=configured-viewers') }
$preservedAuth = Resolve-PatchAuthEnvironment -ExistingEnvironment $configuredEnvironment -RequestedParameters @{}
Assert-True -Condition (-not $preservedAuth.UpdateRequested) -Message 'A valid existing auth mode should be preserved when no auth update is requested.'

$manifestTestRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("syncfactors-manifest-test-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -Path $manifestTestRoot -ItemType Directory -Force | Out-Null
try {
    $validRoot = Join-Path $manifestTestRoot 'valid'
    $missingRoot = Join-Path $manifestTestRoot 'missing'
    $invalidRoot = Join-Path $manifestTestRoot 'invalid'
    New-Item -Path $validRoot, $missingRoot, $invalidRoot -ItemType Directory -Force | Out-Null
    $validCommit = '0123456789abcdef0123456789abcdef01234567'
    Set-Content -Path (Join-Path $validRoot 'release-manifest.json') -Value "{`"commitSha`":`"$validCommit`"}"
    Set-Content -Path (Join-Path $missingRoot 'placeholder.txt') -Value 'no manifest'
    Set-Content -Path (Join-Path $invalidRoot 'release-manifest.json') -Value '{"commitSha":"short"}'
    $validBundle = Join-Path $manifestTestRoot 'valid.zip'
    $missingBundle = Join-Path $manifestTestRoot 'missing.zip'
    $invalidBundle = Join-Path $manifestTestRoot 'invalid.zip'
    Compress-Archive -Path (Join-Path $validRoot '*') -DestinationPath $validBundle
    Compress-Archive -Path (Join-Path $missingRoot '*') -DestinationPath $missingBundle
    Compress-Archive -Path (Join-Path $invalidRoot '*') -DestinationPath $invalidBundle

    Assert-Equal -Expected $validCommit -Actual (Get-BundleReleaseCommit -BundlePath $validBundle) -Message 'Valid bundle commit attestation was not resolved.'
    Assert-Throws `
        -Action { Get-BundleReleaseCommit -BundlePath $missingBundle } `
        -MessagePattern '*exactly one root release-manifest.json*' `
        -Message 'A bundle without a release manifest must fail before deployment mutation.'
    Assert-Throws `
        -Action { Get-BundleReleaseCommit -BundlePath $invalidBundle } `
        -MessagePattern '*full 40- or 64-character hexadecimal commitSha*' `
        -Message 'A bundle with a partial commit must fail before deployment mutation.'
}
finally {
    Remove-Item -Path $manifestTestRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$sqliteTestRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("syncfactors-sqlite-rollback-test-{0}" -f [Guid]::NewGuid().ToString('N'))
$sqliteDatabasePath = Join-Path $sqliteTestRoot 'state\runtime\syncfactors.db'
New-Item -Path (Split-Path -Path $sqliteDatabasePath -Parent) -ItemType Directory -Force | Out-Null
try {
    Set-Content -Path $sqliteDatabasePath -Value 'schema-v15'
    Set-Content -Path "$sqliteDatabasePath-wal" -Value 'wal-v15'
    Set-Content -Path "$sqliteDatabasePath-shm" -Value 'shm-v15'
    Set-Content -Path "$sqliteDatabasePath-journal" -Value 'journal-v15'
    $sqliteSnapshot = Backup-SqliteState `
        -DatabasePath $sqliteDatabasePath `
        -SnapshotRoot (Join-Path $sqliteTestRoot 'snapshot-existing')

    Set-Content -Path $sqliteDatabasePath -Value 'schema-v16'
    Set-Content -Path "$sqliteDatabasePath-wal" -Value 'wal-v16'
    Set-Content -Path "$sqliteDatabasePath-shm" -Value 'shm-v16'
    Set-Content -Path "$sqliteDatabasePath-journal" -Value 'journal-v16'
    Restore-SqliteState -Snapshot $sqliteSnapshot
    Assert-Equal -Expected 'schema-v15' -Actual ((Get-Content -Path $sqliteDatabasePath -Raw).Trim()) -Message 'SQLite rollback did not restore the old database.'
    Assert-Equal -Expected 'wal-v15' -Actual ((Get-Content -Path "$sqliteDatabasePath-wal" -Raw).Trim()) -Message 'SQLite rollback did not restore the old WAL.'
    Assert-Equal -Expected 'shm-v15' -Actual ((Get-Content -Path "$sqliteDatabasePath-shm" -Raw).Trim()) -Message 'SQLite rollback did not restore the old SHM sidecar.'
    Assert-Equal -Expected 'journal-v15' -Actual ((Get-Content -Path "$sqliteDatabasePath-journal" -Raw).Trim()) -Message 'SQLite rollback did not restore the old rollback journal.'

    Remove-Item -Path $sqliteDatabasePath, "$sqliteDatabasePath-wal", "$sqliteDatabasePath-shm", "$sqliteDatabasePath-journal" -Force
    $missingSnapshot = Backup-SqliteState `
        -DatabasePath $sqliteDatabasePath `
        -SnapshotRoot (Join-Path $sqliteTestRoot 'snapshot-missing')
    Set-Content -Path $sqliteDatabasePath -Value 'created-by-new-version'
    Set-Content -Path "$sqliteDatabasePath-wal" -Value 'new-wal'
    Set-Content -Path "$sqliteDatabasePath-journal" -Value 'new-journal'
    Restore-SqliteState -Snapshot $missingSnapshot
    Assert-True -Condition (-not (Test-Path -Path $sqliteDatabasePath)) -Message 'Rollback did not remove a database created by the failed deployment.'
    Assert-True -Condition (-not (Test-Path -Path "$sqliteDatabasePath-wal")) -Message 'Rollback did not remove a WAL created by the failed deployment.'
    Assert-True -Condition (-not (Test-Path -Path "$sqliteDatabasePath-journal")) -Message 'Rollback did not remove a rollback journal created by the failed deployment.'

    $markerPath = "$sqliteDatabasePath.deployment-commit"
    Set-Content -Path $markerPath -Value 'prior-deployment-marker'
    $markerSnapshot = Backup-DeploymentCommitMarker `
        -MarkerPath $markerPath `
        -SnapshotRoot (Join-Path $sqliteTestRoot 'snapshot-existing')
    Remove-Item -Path $markerPath -Force
    $markerNonce = 'deployment-nonce-that-is-at-least-thirty-two-characters'
    Publish-DeploymentCommitMarker -MarkerPath $markerPath -Nonce $markerNonce
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $expectedMarker = ([BitConverter]::ToString($sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($markerNonce)))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
    Assert-Equal -Expected $expectedMarker -Actual ((Get-Content -Path $markerPath -Raw).Trim()) -Message 'Published deployment marker did not contain SHA-256(nonce).'
    Restore-DeploymentCommitMarker -Snapshot $markerSnapshot
    Assert-Equal -Expected 'prior-deployment-marker' -Actual ((Get-Content -Path $markerPath -Raw).Trim()) -Message 'Rollback did not restore the prior deployment marker.'
}
finally {
    Remove-Item -Path $sqliteTestRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$prerequisitesPath = Join-Path $root 'scripts/Install-SyncFactorsWindowsPrerequisites.ps1'
if ([System.OperatingSystem]::IsWindows()) {
    Import-ScriptFunction -Path $prerequisitesPath -Name 'ConvertTo-AccountSid'
    Import-ScriptFunction -Path $prerequisitesPath -Name 'Grant-ServiceAccountAccess'
    Import-ScriptFunction -Path $prerequisitesPath -Name 'Set-RestrictedDirectoryAccess'
    Import-ScriptFunction -Path $installerPath -Name 'Protect-ServiceRegistryKey'
    $aclTestRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("syncfactors-acl-test-{0}" -f [Guid]::NewGuid().ToString('N'))
    $aclStateRoot = Join-Path $aclTestRoot 'state'
    $aclBackupRoot = Join-Path $aclTestRoot '_backups'
    New-Item -Path $aclStateRoot, $aclBackupRoot -ItemType Directory -Force | Out-Null
    try {
        $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        Grant-ServiceAccountAccess `
            -Path $aclTestRoot `
            -Identity $currentIdentity `
            -Rights ([System.Security.AccessControl.FileSystemRights]'ReadAndExecute, Synchronize')
        Set-RestrictedDirectoryAccess `
            -Path $aclStateRoot `
            -RuntimeIdentity $currentIdentity `
            -RuntimeRights ([System.Security.AccessControl.FileSystemRights]'Modify, Synchronize')
        Set-RestrictedDirectoryAccess -Path $aclBackupRoot

        $currentSid = (ConvertTo-AccountSid -Identity $currentIdentity).Value
        $rootRule = Get-Acl -Path $aclTestRoot | Select-Object -ExpandProperty Access |
            Where-Object { -not $_.IsInherited -and $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -eq $currentSid } |
            Select-Object -First 1
        $stateRule = Get-Acl -Path $aclStateRoot | Select-Object -ExpandProperty Access |
            Where-Object { -not $_.IsInherited -and $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -eq $currentSid } |
            Select-Object -First 1
        $stateAcl = Get-Acl -Path $aclStateRoot
        $backupAcl = Get-Acl -Path $aclBackupRoot
        $broadDataSids = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545')
        $stateBroadAllow = @($stateAcl.Access | Where-Object {
            $sidValue = $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and $sidValue -in $broadDataSids
        })
        $backupBroadAllow = @($backupAcl.Access | Where-Object {
            $sidValue = $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and $sidValue -in $broadDataSids
        })
        Assert-True -Condition ($null -ne $rootRule) -Message 'Install-root runtime ACL was not created.'
        Assert-True -Condition (($rootRule.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Modify) -ne [System.Security.AccessControl.FileSystemRights]::Modify) -Message 'Install-root runtime ACL still grants Modify.'
        Assert-True -Condition (($stateRule.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Modify) -eq [System.Security.AccessControl.FileSystemRights]::Modify) -Message 'State runtime ACL does not grant Modify.'
        Assert-True -Condition $stateAcl.AreAccessRulesProtected -Message 'Runtime state still inherits broad parent ACLs.'
        Assert-True -Condition $backupAcl.AreAccessRulesProtected -Message 'Rollback backups still inherit broad parent ACLs.'
        Assert-Equal -Expected 0 -Actual $stateBroadAllow.Count -Message 'Runtime state grants read access to Users, Everyone, or Authenticated Users.'
        Assert-Equal -Expected 0 -Actual $backupBroadAllow.Count -Message 'Rollback backups grant read access to Users, Everyone, or Authenticated Users.'
    }
    finally {
        Remove-Item -Path $aclTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    $registryAclTestPath = "HKLM:\SOFTWARE\SyncFactorsAclTest-$([Guid]::NewGuid().ToString('N'))"
    New-Item -Path $registryAclTestPath -Force | Out-Null
    try {
        $registryAcl = Get-Acl -Path $registryAclTestPath
        $usersRule = [System.Security.AccessControl.RegistryAccessRule]::new(
            [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),
            [System.Security.AccessControl.RegistryRights]::ReadKey,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void]$registryAcl.AddAccessRule($usersRule)
        Set-Acl -Path $registryAclTestPath -AclObject $registryAcl
        Protect-ServiceRegistryKey -Name 'test' -RegistryPath $registryAclTestPath

        $protectedRegistryAcl = Get-Acl -Path $registryAclTestPath
        $broadRegistryAllows = @($protectedRegistryAcl.Access | Where-Object {
            $sidValue = $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and
                $sidValue -in @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545')
        })
        Assert-True -Condition $protectedRegistryAcl.AreAccessRulesProtected -Message 'Service registry key still inherits broad ACLs.'
        Assert-Equal -Expected 0 -Actual $broadRegistryAllows.Count -Message 'Service registry key still grants broad read access.'
    }
    finally {
        Remove-Item -Path $registryAclTestPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$script:registryEnvironment = @{
    'SyncFactors.Api' = @(
        'SyncFactors__Runtime__DryRunOnly=true',
        'SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY=stable-key',
        'SyncFactors__Deployment__Nonce=old-deployment-nonce-that-is-long-enough',
        'SYNCFACTORS__AUTH__MODE=hybrid',
        'SYNCFACTORS__AUTH__LOCALBREAKGLASS__ENABLED=true',
        'SYNCFACTORS__AUTH__OIDC__AUTHORITY=https://old.example.test',
        'SYNCFACTORS__AUTH__OIDC__CLIENTID=old-client',
        'SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__0=old-viewers',
        'SYNCFACTORS__AUTH__OIDC__ADMINGROUPS__0=old-admins',
        'SYNCFACTORS__AUTH__OIDC__CLIENTSECRET=credential-manager-override',
        'SYNCFACTORS__AUTH__IDLETIMEOUTMINUTES=240'
    )
}

function Get-ServiceEnvironment {
    param([string]$ServiceName)
    return [pscustomobject]@{ Exists = $script:registryEnvironment.ContainsKey($ServiceName); Values = @($script:registryEnvironment[$ServiceName]) }
}

function Test-Path {
    param([string]$Path, [string]$PathType)
    return $Path.StartsWith('HKLM:\SYSTEM\CurrentControlSet\Services\', [System.StringComparison]::OrdinalIgnoreCase)
}

function New-ItemProperty {
    param([string]$Path, [string]$Name, $PropertyType, [object[]]$Value, [switch]$Force)
    $serviceName = Split-Path -Path $Path -Leaf
    $script:registryEnvironment[$serviceName] = @($Value)
}

function Remove-ItemProperty {
    param([string]$Path, [string]$Name, $ErrorAction)
    $serviceName = Split-Path -Path $Path -Leaf
    $script:registryEnvironment.Remove($serviceName)
}

$snapshot = [pscustomobject]@{ Exists = $true; Values = @($script:registryEnvironment['SyncFactors.Api']) }
Set-ServiceEnvironmentValue `
    -ServiceName 'SyncFactors.Api' `
    -Name 'SyncFactors__Runtime__DryRunOnly' `
    -Value 'false'
Set-ServiceEnvironmentValue `
    -ServiceName 'SyncFactors.Api' `
    -Name 'SyncFactors__Deployment__Nonce' `
    -Value 'new-deployment-nonce-that-is-long-enough'
Assert-True `
    -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SyncFactors__Runtime__DryRunOnly=false') `
    -Message 'Mode transition did not update the service environment.'
Assert-True `
    -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY=stable-key') `
    -Message 'Mode transition dropped unrelated service environment values.'
Assert-True `
    -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SyncFactors__Deployment__Nonce=new-deployment-nonce-that-is-long-enough') `
    -Message 'Deployment nonce did not rotate.'

Restore-ServiceEnvironment -ServiceName 'SyncFactors.Api' -Snapshot $snapshot
Assert-True `
    -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SyncFactors__Runtime__DryRunOnly=true') `
    -Message 'Rollback did not restore the prior mode.'
Assert-True `
    -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY=stable-key') `
    -Message 'Rollback did not restore the audit integrity key.'
Assert-True `
    -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SyncFactors__Deployment__Nonce=old-deployment-nonce-that-is-long-enough') `
    -Message 'Rollback did not restore the prior deployment nonce.'

$authUpdate = Resolve-PatchAuthEnvironment `
    -ExistingEnvironment (Get-ServiceEnvironment -ServiceName 'SyncFactors.Api') `
    -RequestedParameters @{
        AuthMode = 'oidc'
        OidcAuthority = 'https://new.example.test'
        OidcClientId = 'new-client'
        OidcViewerGroups = 'new-viewers'
        OidcOperatorGroups = ''
        OidcAdminGroups = ''
    }
Set-ServiceEnvironmentValues `
    -ServiceName 'SyncFactors.Api' `
    -ManagedNames $authUpdate.ManagedNames `
    -ManagedEntries $authUpdate.ManagedEntries
Assert-True -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SYNCFACTORS__AUTH__MODE=oidc') -Message 'Auth mode transition was not applied.'
Assert-True -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SYNCFACTORS__AUTH__LOCALBREAKGLASS__ENABLED=false') -Message 'OIDC transition did not disable local break-glass.'
Assert-True -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SYNCFACTORS__AUTH__OIDC__VIEWERGROUPS__0=new-viewers') -Message 'Updated viewer group was not applied.'
Assert-True -Condition ($script:registryEnvironment['SyncFactors.Api'] -notcontains 'SYNCFACTORS__AUTH__OIDC__ADMINGROUPS__0=old-admins') -Message 'Explicitly cleared admin groups retained stale privileged entries.'
Assert-True -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SYNCFACTORS__AUTH__OIDC__CLIENTSECRET=credential-manager-override') -Message 'Auth update removed a non-managed secret override.'
Assert-True -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SYNCFACTORS__AUTH__IDLETIMEOUTMINUTES=240') -Message 'Auth update removed non-managed auth tuning.'

Restore-ServiceEnvironment -ServiceName 'SyncFactors.Api' -Snapshot $snapshot
Assert-True -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SYNCFACTORS__AUTH__MODE=hybrid') -Message 'Auth rollback did not restore the prior mode.'
Assert-True -Condition ($script:registryEnvironment['SyncFactors.Api'] -contains 'SYNCFACTORS__AUTH__OIDC__ADMINGROUPS__0=old-admins') -Message 'Auth rollback did not restore prior admin groups.'

$verificationPath = Join-Path $root 'scripts/Test-SyncFactorsWindowsDeployment.ps1'
Import-ScriptFunction -Path $verificationPath -Name 'New-ReadinessAttestationHeaders'
Import-ScriptFunction -Path $verificationPath -Name 'Invoke-HttpHealthCheck'
Import-ScriptFunction -Path $verificationPath -Name 'Get-CertificateHashAlgorithm'
$sha1Algorithm = Get-CertificateHashAlgorithm -ExpectedHexLength 40
$sha256Algorithm = Get-CertificateHashAlgorithm -ExpectedHexLength 64
Assert-Equal -Expected 'SHA1' -Actual $sha1Algorithm.Name -Message 'A 40-character certificate thumbprint must use SHA-1.'
Assert-Equal -Expected 'SHA256' -Actual $sha256Algorithm.Name -Message 'A 64-character certificate thumbprint must use SHA-256.'
Assert-Throws `
    -Action { Get-CertificateHashAlgorithm -ExpectedHexLength 48 } `
    -MessagePattern '*Unsupported certificate thumbprint length*' `
    -Message 'Unsupported certificate hash lengths must fail closed.'
$attestationTime = [DateTimeOffset]::Parse('2026-07-31T12:00:00.0000000Z')
$attestationHeaders = New-ReadinessAttestationHeaders `
    -Nonce 'deployment-nonce-that-is-at-least-thirty-two-characters' `
    -StartedAfter $attestationTime `
    -WorkerCommit 'abcdef' `
    -ApiCommit 'abcdef'
Assert-Equal -Expected 'deployment-nonce-that-is-at-least-thirty-two-characters' -Actual $attestationHeaders['X-SyncFactors-Deployment-Nonce'] -Message 'Readiness nonce header was missing.'
Assert-Equal -Expected $attestationTime.ToString('O') -Actual $attestationHeaders['X-SyncFactors-Worker-Started-After'] -Message 'Worker start boundary header was wrong.'
Assert-Equal -Expected 'abcdef' -Actual $attestationHeaders['X-SyncFactors-Expected-Worker-Commit'] -Message 'Expected worker commit header was missing.'
Assert-Equal -Expected 'abcdef' -Actual $attestationHeaders['X-SyncFactors-Expected-Api-Commit'] -Message 'Expected API commit header was missing.'

$script:readinessContent = '{"status":"ready","attested":true}'
$script:capturedReadinessHeaders = $null
function Invoke-PinnedHttpRequest {
    param([string]$Url, [hashtable]$Headers, [string]$CertificateThumbprint)
    $script:capturedReadinessHeaders = $Headers
    return [pscustomobject]@{ StatusCode = 200; Content = $script:readinessContent }
}
function Start-Sleep { param([int]$Seconds) }

$statusCode = Invoke-HttpHealthCheck `
    -Url 'https://localhost:5087/readyz' `
    -Deadline ([DateTimeOffset]::UtcNow) `
    -Headers $attestationHeaders `
    -CertificateThumbprint ('A' * 40)
Assert-Equal -Expected 200 -Actual $statusCode -Message 'Attested readiness response was not accepted.'
Assert-Equal -Expected $attestationHeaders['X-SyncFactors-Deployment-Nonce'] -Actual $script:capturedReadinessHeaders['X-SyncFactors-Deployment-Nonce'] -Message 'Readiness request omitted attestation headers.'

$script:readinessContent = '{"status":"ready"}'
Assert-Throws `
    -Action { Invoke-HttpHealthCheck -Url 'https://localhost:5087/readyz' -Deadline ([DateTimeOffset]::UtcNow) -Headers $attestationHeaders -CertificateThumbprint ('A' * 40) } `
    -MessagePattern '*did not contain status=ready and attested=true*' `
    -Message 'Legacy un-attested readiness response must be rejected.'

Write-Output 'Windows deployment behavioral tests passed.'
