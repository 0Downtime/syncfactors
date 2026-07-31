[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-FileMatch {
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Content -notmatch $Pattern) {
        throw $Message
    }
}

function Assert-FileNotMatch {
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Content -match $Pattern) {
        throw $Message
    }
}

function Assert-PowerShellSyntax {
    param([Parameter(Mandatory)][string]$Path)

    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        $details = ($errors | ForEach-Object { $_.Message }) -join '; '
        throw "PowerShell syntax validation failed for '$Path': $details"
    }
}

function Assert-AzurePipelineInlinePowerShellSyntax {
    param([Parameter(Mandatory)][string]$Path)

    $lines = @(Get-Content -Path $Path)
    $blocksFound = 0
    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        if ($lines[$lineIndex] -notmatch '^(\s*)InlineScript:\s*\|\s*$') {
            continue
        }

        $blocksFound++
        $parentIndent = $Matches[1].Length
        $blockIndent = $parentIndent + 2
        $scriptLines = @()
        for ($blockLineIndex = $lineIndex + 1; $blockLineIndex -lt $lines.Count; $blockLineIndex++) {
            $line = $lines[$blockLineIndex]
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                $indent = $line.Length - $line.TrimStart().Length
                if ($indent -le $parentIndent) {
                    break
                }
            }

            if ($line.Length -ge $blockIndent) {
                $scriptLines += $line.Substring($blockIndent)
            }
            else {
                $scriptLines += ''
            }
        }

        $tokens = $null
        $errors = $null
        $scriptText = $scriptLines -join [Environment]::NewLine
        [void][System.Management.Automation.Language.Parser]::ParseInput(
            $scriptText,
            "$Path InlineScript block $blocksFound",
            [ref]$tokens,
            [ref]$errors)
        if ($errors.Count -gt 0) {
            $details = ($errors | ForEach-Object { $_.Message }) -join '; '
            throw "PowerShell syntax validation failed for InlineScript block $blocksFound in '$Path': $details"
        }
    }

    if ($blocksFound -eq 0) {
        throw "No InlineScript PowerShell block was found in '$Path'."
    }
}

$root = (Resolve-Path -Path $RepositoryRoot).ProviderPath
$installerPath = Join-Path $root 'scripts/Install-SyncFactorsWindowsServices.ps1'
$patchPath = Join-Path $root 'scripts/Deploy-SyncFactorsWindowsPatch.ps1'
$verificationPath = Join-Path $root 'scripts/Test-SyncFactorsWindowsDeployment.ps1'
$prerequisitesPath = Join-Path $root 'scripts/Install-SyncFactorsWindowsPrerequisites.ps1'
$accountsPath = Join-Path $root 'scripts/New-SyncFactorsWindowsServiceAccounts.ps1'
$behaviorTestsPath = Join-Path $root 'tests/WindowsDeploymentBehavior.Tests.ps1'
$pipelinePath = Join-Path $root 'azure-pipelines.deploy.yml'
$githubTestWorkflowPath = Join-Path $root '.github/workflows/test.yml'

foreach ($path in @($installerPath, $patchPath, $verificationPath, $prerequisitesPath, $accountsPath, $behaviorTestsPath)) {
    Assert-PowerShellSyntax -Path $path
}
Assert-AzurePipelineInlinePowerShellSyntax -Path $pipelinePath

$installer = Get-Content -Path $installerPath -Raw
Assert-FileMatch -Content $installer -Pattern '\[switch\]\$DryRunOnly' -Message 'The service installer must expose the DryRunOnly switch.'
Assert-FileMatch -Content $installer -Pattern '\[switch\]\$EnableLiveWrites' -Message 'The service installer must require an explicit live-write opt-in.'
Assert-FileMatch -Content $installer -Pattern 'production-safe-default' -Message 'Fresh service installs must have a production-safe write-mode default.'
Assert-FileMatch -Content $installer -Pattern 'SyncFactors__Runtime__DryRunOnly=\$\(' -Message 'The service installer must write DryRunOnly into the shared API and worker environment.'
Assert-FileMatch -Content $installer -Pattern 'Resolve-SecurityAuditIntegrityKey' -Message 'The service installer must resolve a stable security audit integrity key.'
Assert-FileMatch -Content $installer -Pattern 'SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY=\$\(' -Message 'The service installer must write the audit integrity key into both service environments.'
Assert-FileMatch -Content $installer -Pattern 'SyncFactors__Deployment__Nonce=\$DeploymentNonce' -Message 'The service installer must put the deployment nonce in both service environments.'
Assert-FileMatch -Content $installer -Pattern 'Merge-ServiceEnvironmentEntries' -Message 'Force reinstall must preserve non-managed service environment values.'
Assert-FileMatch -Content $installer -Pattern 'SYNCFACTORS__AUTH__MODE=\$resolvedAuthMode' -Message 'Fresh service installation must write an explicit API auth mode.'
Assert-FileMatch -Content $installer -Pattern 'SYNCFACTORS__AUTH__LOCALBREAKGLASS__ENABLED=' -Message 'Fresh service installation must write the local break-glass enablement decision.'
Assert-FileMatch -Content $installer -Pattern '\[switch\]\$AllowLocalSystem' -Message 'Any LocalSystem installation must require a conspicuous opt-in.'
Assert-FileMatch -Content $installer -Pattern 'Credential is required for the restricted SyncFactors runtime identity' -Message 'The service installer must fail closed without a restricted runtime credential.'
Assert-FileMatch -Content $installer -Pattern 'DeploymentSecretsFile' -Message 'Fresh installation must accept an ACL-restricted DPAPI secret handoff instead of native secret arguments.'
Assert-FileMatch -Content $installer -Pattern 'Protect-ServiceRegistryKey' -Message 'Service installation must protect registry environment secrets.'
Assert-FileMatch -Content $installer -Pattern 'SyncFactors__Deployment__CommitMarkerPath=' -Message 'Service installation must configure the worker deployment commit marker.'

$patchScript = Get-Content -Path $patchPath -Raw
Assert-FileMatch -Content $patchScript -Pattern 'Resolve-DryRunOnlyMode' -Message 'Patch deployment must resolve and preserve write-safety mode.'
Assert-FileMatch -Content $patchScript -Pattern 'Restore-ServiceEnvironment' -Message 'Patch rollback must restore the original service environments.'
Assert-FileMatch -Content $patchScript -Pattern 'Test-SyncFactorsWindowsDeployment\.ps1' -Message 'Patch deployment must invoke Windows deployment verification.'
Assert-FileMatch -Content $patchScript -Pattern 'https://localhost:5087/readyz' -Message 'Patch deployment must default to the runtime readiness endpoint.'
Assert-FileMatch -Content $patchScript -Pattern 'Resolve-SecurityAuditIntegrityKey' -Message 'Patch deployment must preserve and validate the audit integrity key.'
Assert-FileMatch -Content $patchScript -Pattern "-Name 'SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY'" -Message 'Patch deployment must apply the audit integrity key to both services.'
Assert-FileMatch -Content $patchScript -Pattern 'Assert-ExpectedServiceIdentity' -Message 'Normal patch deployment must validate and preserve the installed service identity.'
Assert-FileMatch -Content $patchScript -Pattern 'Resolve-PatchAuthEnvironment' -Message 'Patch deployment must preflight and apply rollback-protected auth environment updates.'
Assert-FileMatch -Content $patchScript -Pattern 'Protect runtime state and rollback backups before snapshot creation' -Message 'Patch deployment must harden legacy filesystem ACLs before creating backups.'
Assert-FileMatch -Content $patchScript -Pattern 'Protect-ServiceRegistryKey' -Message 'Patch deployment must protect service registry environment secrets.'
Assert-FileMatch -Content $patchScript -Pattern 'Backup-SqliteState' -Message 'Patch deployment must snapshot SQLite before starting migration-capable binaries.'
Assert-FileMatch -Content $patchScript -Pattern "@\('', '-wal', '-shm', '-journal'\)" -Message 'SQLite rollback snapshots must include the database, WAL, SHM, and rollback journal.'
Assert-FileMatch -Content $patchScript -Pattern 'Restore-SqliteState -Snapshot \$sqliteSnapshot' -Message 'Patch rollback must restore the pre-deployment SQLite snapshot.'
Assert-FileMatch -Content $patchScript -Pattern 'full 40- or 64-character hexadecimal commitSha' -Message 'Patch deployment must reject a missing or partial release commit.'
Assert-FileMatch -Content $patchScript -Pattern 'Backup-DeploymentCommitMarker' -Message 'Patch deployment must snapshot the prior worker commit marker.'
Assert-FileMatch -Content $patchScript -Pattern 'Restore-DeploymentCommitMarker' -Message 'Patch rollback must restore the prior worker commit marker.'
Assert-FileMatch -Content $patchScript -Pattern 'Publish-DeploymentCommitMarker' -Message 'Patch deployment must publish the worker commit gate only after attestation.'
Assert-FileMatch -Content $patchScript -Pattern 'requires an HTTPS HealthUrl' -Message 'Patch deployment must reject plaintext readiness URLs before mutation.'
Assert-FileMatch -Content $patchScript -Pattern 'SkipHealthCheck is not permitted' -Message 'Production patch deployment must not bypass attested readiness.'
$verificationCallIndex = $patchScript.IndexOf('$verification = & $verificationScript')
$markerPublishIndex = $patchScript.IndexOf('Publish-DeploymentCommitMarker -MarkerPath')
if ($verificationCallIndex -lt 0 -or $markerPublishIndex -lt 0 -or $markerPublishIndex -lt $verificationCallIndex) {
    throw 'The deployment commit marker must be published only after attested readiness succeeds.'
}
$patchManifestPreflightIndex = $patchScript.IndexOf('$expectedReleaseCommit = Get-BundleReleaseCommit')
$patchMutationIndex = $patchScript.IndexOf('New-Item -Path $expandedRoot')
if ($patchManifestPreflightIndex -lt 0 -or $patchMutationIndex -lt 0 -or $patchManifestPreflightIndex -gt $patchMutationIndex) {
    throw 'Patch deployment must validate the bundle manifest and full commit before staging mutation.'
}

$verification = Get-Content -Path $verificationPath -Raw
Assert-FileMatch -Content $verification -Pattern '@\(\$ApiServiceName, \$WorkerServiceName\)' -Message 'Deployment verification must check both the API and worker services.'
Assert-FileMatch -Content $verification -Pattern 'must have an explicit boolean SyncFactors__Runtime__DryRunOnly' -Message 'Deployment verification must reject missing or invalid write-safety configuration.'
Assert-FileMatch -Content $verification -Pattern 'Invoke-HttpHealthCheck' -Message 'Deployment verification must require runtime readiness.'
Assert-FileMatch -Content $verification -Pattern 'The API and worker must use the same security audit integrity key' -Message 'Deployment verification must reject audit-key mismatch.'
Assert-FileMatch -Content $verification -Pattern 'X-SyncFactors-Deployment-Nonce' -Message 'Readiness verification must bind the response to a deployment nonce.'
Assert-FileMatch -Content $verification -Pattern 'X-SyncFactors-Worker-Started-After' -Message 'Readiness verification must reject a stale worker heartbeat.'
Assert-FileMatch -Content $verification -Pattern 'X-SyncFactors-Expected-Worker-Commit' -Message 'Readiness verification must attest the worker commit.'
Assert-FileMatch -Content $verification -Pattern 'X-SyncFactors-Expected-Api-Commit' -Message 'Readiness verification must attest the API commit.'
Assert-FileMatch -Content $verification -Pattern 'attested=true' -Message 'Deployment verification must reject legacy un-attested HTTP 200 responses.'
Assert-FileNotMatch -Content $verification -Pattern 'SkipCertificateCheck' -Message 'Readiness must never send the nonce with certificate validation disabled.'
Assert-FileMatch -Content $verification -Pattern 'ServerCertificateCustomValidationCallback' -Message 'HTTPS readiness must pin the expected server certificate.'
Assert-FileMatch -Content $verification -Pattern 'ready = -not \$SkipHttpHealthCheck' -Message 'Skipped HTTP attestation must never report ready=true.'
Assert-FileMatch -Content $verification -Pattern 'requires an HTTPS HealthUrl' -Message 'Deployment verification must reject plaintext readiness URLs.'

$prerequisites = Get-Content -Path $prerequisitesPath -Raw
Assert-FileMatch -Content $prerequisites -Pattern "-Rights \(\[System\.Security\.AccessControl\.FileSystemRights\]'ReadAndExecute, Synchronize'\)" -Message 'Prerequisite setup must limit the runtime identity to read/execute on the install root.'
Assert-FileMatch -Content $prerequisites -Pattern 'Set-RestrictedDirectoryAccess' -Message 'Runtime and backup data must use protected ACLs.'
Assert-FileMatch -Content $prerequisites -Pattern 'S-1-5-18' -Message 'Protected data ACLs must preserve SYSTEM.'
Assert-FileMatch -Content $prerequisites -Pattern 'S-1-5-32-544' -Message 'Protected data ACLs must preserve Administrators.'
Assert-FileMatch -Content $prerequisites -Pattern 'Protect rollback backups for SYSTEM and Administrators' -Message 'Rollback snapshots must not inherit broad Users access.'

$accounts = Get-Content -Path $accountsPath -Raw
Assert-FileMatch -Content $accounts -Pattern 'ReadAndExecute \$InstallRoot' -Message 'Account setup must report read/execute access on the install root.'
Assert-FileMatch -Content $accounts -Pattern 'Modify \$RuntimeRoot' -Message 'Account setup must report Modify access on runtime state.'

$pipeline = Get-Content -Path $pipelinePath -Raw
Assert-FileMatch -Content $pipeline -Pattern '(?m)^\s+dryRunOnly:\s+true\s*$' -Message 'The production deployment pipeline must default dryRunOnly to true.'
Assert-FileNotMatch -Content $pipeline -Pattern 'Set-ServiceDryRunOnlyEnvironment' -Message 'The pipeline must not mutate write mode before patch snapshot and rollback protection.'
Assert-FileMatch -Content $pipeline -Pattern '_current-patch-' -Message 'Legacy installations must execute the current rollback-safe patch script from the bundle.'
Assert-FileMatch -Content $pipeline -Pattern 'if \(\$apiServiceExists -and \$workerServiceExists\)' -Message 'Any complete existing service installation must use patch mode even when the installed patch script is missing.'
Assert-FileMatch -Content $pipeline -Pattern '\$null -eq \$patchCommand -or' -Message 'A missing installed patch command must fall back to the current bundle patch, never fresh reinstall.'
Assert-FileNotMatch -Content $pipeline -Pattern "'-InstallOrUpdateServices'" -Message 'Normal pipeline patches must preserve service definitions rather than reinstalling services.'
Assert-FileMatch -Content $pipeline -Pattern "'-ExpectedServiceIdentity'" -Message 'Normal pipeline patches must validate the configured restricted identity.'
$patchArgsBlock = [regex]::Match($pipeline, '(?s)\$patchArgs\s*=\s*@\(.*?Patch deployment failed').Value
if ([string]::IsNullOrWhiteSpace($patchArgsBlock)) {
    throw 'Could not locate the normal pipeline patch argument block.'
}
Assert-FileNotMatch -Content $patchArgsBlock -Pattern "'-Credential'|serviceUserPassword" -Message 'Normal pipeline patches must not pass or construct with the service password.'
Assert-FileNotMatch -Content $patchArgsBlock -Pattern '\$patchArgs \+= @\(''-SqlitePassword''|\$patchArgs \+= @\(''-SecurityAuditIntegrityKey''' -Message 'Normal pipeline patches must not place secrets in native arguments.'
Assert-FileMatch -Content $pipeline -Pattern '(?m)^\s+securityAuditIntegrityKey:\s+''{2}\s*$' -Message 'The pipeline must expose an overridable secret audit-integrity-key input.'
Assert-FileMatch -Content $pipeline -Pattern '(?m)^\s+authMode:\s+''{2}\s*$' -Message 'The pipeline must expose an explicit production auth-mode input.'
Assert-FileMatch -Content $pipeline -Pattern 'authMode must be explicitly set' -Message 'Fresh pipeline deployment must reject an omitted auth mode.'
Assert-FileMatch -Content $pipeline -Pattern 'deploymentNonceBytes' -Message 'Every pipeline deployment must generate a fresh readiness nonce.'
Assert-FileMatch -Content $pipeline -Pattern 'RandomNumberGenerator\]::Create\(\)' -Message 'Nonce generation must use a Windows PowerShell 5.1-compatible RNG API.'
Assert-FileNotMatch -Content $pipeline -Pattern 'RandomNumberGenerator\]::GetBytes\(48\)' -Message 'The remote deployment must not use a .NET API unavailable in stock Windows PowerShell 5.1.'
Assert-FileNotMatch -Content $pipeline -Pattern '\$releaseManifest\.version' -Message 'Deployment attestation must not compare the release manifest package version to assembly informational version.'
Assert-FileMatch -Content $pipeline -Pattern '\$bundleCommitSha = Get-BundleReleaseCommit' -Message 'The pipeline must validate the bundle manifest before fresh install or patch deployment.'
Assert-FileMatch -Content $pipeline -Pattern 'ExpectedWorkerCommit'', \$bundleCommitSha' -Message 'Fresh deployment readiness must always attest the bundle worker commit.'
Assert-FileMatch -Content $pipeline -Pattern 'ExpectedApiCommit'', \$bundleCommitSha' -Message 'Fresh deployment readiness must always attest the bundle API commit.'
$pipelineManifestPreflightIndex = $pipeline.IndexOf('$bundleCommitSha = Get-BundleReleaseCommit')
$pipelineMutationIndex = $pipeline.IndexOf('New-Item -Path $installRoot')
if ($pipelineManifestPreflightIndex -lt 0 -or $pipelineMutationIndex -lt 0 -or $pipelineManifestPreflightIndex -gt $pipelineMutationIndex) {
    throw 'The pipeline must reject a missing/invalid bundle manifest before install-root mutation.'
}
Assert-FileMatch -Content $pipeline -Pattern '\$isRoleGroup -and -not \[string\]::IsNullOrWhiteSpace\(\$authMode\)' -Message 'Explicit auth updates must pass empty role-group lists so stale privileged groups are removed.'
Assert-FileMatch -Content $pipeline -Pattern '(?m)^\s+allowLocalSystemServiceAccount:\s+false\s*$' -Message 'The pipeline must block LocalSystem by default.'
Assert-FileMatch -Content $pipeline -Pattern 'serviceUserName is required to validate the restricted runtime identity' -Message 'The pipeline must fail closed without a restricted runtime identity.'
Assert-FileMatch -Content $pipeline -Pattern 'serviceUserPassword is required for a fresh service install' -Message 'Fresh service installation must require the runtime credential password.'
Assert-FileMatch -Content $pipeline -Pattern 'service installation state disagrees' -Message 'The pipeline must reject a half-installed API/worker state before filesystem mutation.'
Assert-FileMatch -Content $pipeline -Pattern 'Test-SyncFactorsWindowsDeployment\.ps1' -Message 'The pipeline must run deployment verification for patch and fresh installs.'
Assert-FileMatch -Content $pipeline -Pattern '/readyz' -Message 'The pipeline must verify API and worker runtime readiness.'
Assert-FileMatch -Content $pipeline -Pattern 'Export-Clixml' -Message 'Cross-process credentials and secrets must use a DPAPI-protected handoff.'
Assert-FileMatch -Content $pipeline -Pattern "'-DeploymentSecretsFile'" -Message 'Child PowerShell must consume the protected handoff file.'
Assert-FileMatch -Content $pipeline -Pattern 'Remove-Item -Path \$deploymentSecretsFile' -Message 'The protected secret handoff must be deleted in finally.'
Assert-FileMatch -Content $pipeline -Pattern 'issecret=true' -Message 'Encoded cross-WinRM values must remain masked Azure task variables.'
$remoteInline = [regex]::Match($pipeline, '(?s)displayName:\s+Install prerequisites and services.*?InlineScript:\s*\|(?<script>.*)$').Groups['script'].Value
if ([string]::IsNullOrWhiteSpace($remoteInline)) {
    throw 'Could not locate the remote deployment InlineScript.'
}
$unsafeRemoteMacros = [regex]::Replace($remoteInline, '\$\(syncFactors[A-Za-z0-9]+B64\)', '')
Assert-FileNotMatch -Content $unsafeRemoteMacros -Pattern '\$\([A-Za-z][A-Za-z0-9_.]*\)' -Message 'Remote PowerShell source may contain only masked Base64 transport macros, never raw deployment-variable substitution.'

$githubTestWorkflow = Get-Content -Path $githubTestWorkflowPath -Raw
Assert-FileMatch -Content $githubTestWorkflow -Pattern 'runs-on: windows-latest' -Message 'Behavioral deployment tests must run on a hosted Windows runner.'
Assert-FileMatch -Content $githubTestWorkflow -Pattern 'WindowsDeploymentBehavior\.Tests\.ps1' -Message 'The Windows runner must execute deployment behavioral tests.'

Write-Output 'Windows deployment safety checks passed.'
