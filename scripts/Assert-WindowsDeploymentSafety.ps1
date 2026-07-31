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

$root = (Resolve-Path -Path $RepositoryRoot).ProviderPath
$installerPath = Join-Path $root 'scripts/Install-SyncFactorsWindowsServices.ps1'
$patchPath = Join-Path $root 'scripts/Deploy-SyncFactorsWindowsPatch.ps1'
$verificationPath = Join-Path $root 'scripts/Test-SyncFactorsWindowsDeployment.ps1'
$pipelinePath = Join-Path $root 'azure-pipelines.deploy.yml'

foreach ($path in @($installerPath, $patchPath, $verificationPath)) {
    Assert-PowerShellSyntax -Path $path
}

$installer = Get-Content -Path $installerPath -Raw
Assert-FileMatch -Content $installer -Pattern '\[switch\]\$DryRunOnly' -Message 'The service installer must expose the DryRunOnly switch.'
Assert-FileMatch -Content $installer -Pattern '\[switch\]\$EnableLiveWrites' -Message 'The service installer must require an explicit live-write opt-in.'
Assert-FileMatch -Content $installer -Pattern 'production-safe-default' -Message 'Fresh service installs must have a production-safe write-mode default.'
Assert-FileMatch -Content $installer -Pattern 'SyncFactors__Runtime__DryRunOnly=\$\(' -Message 'The service installer must write DryRunOnly into the shared API and worker environment.'

$patchScript = Get-Content -Path $patchPath -Raw
Assert-FileMatch -Content $patchScript -Pattern 'Resolve-DryRunOnlyMode' -Message 'Patch deployment must resolve and preserve write-safety mode.'
Assert-FileMatch -Content $patchScript -Pattern 'Restore-ServiceEnvironment' -Message 'Patch rollback must restore the original service environments.'
Assert-FileMatch -Content $patchScript -Pattern 'Test-SyncFactorsWindowsDeployment\.ps1' -Message 'Patch deployment must invoke Windows deployment verification.'
Assert-FileMatch -Content $patchScript -Pattern 'https://localhost:5087/readyz' -Message 'Patch deployment must default to the runtime readiness endpoint.'

$verification = Get-Content -Path $verificationPath -Raw
Assert-FileMatch -Content $verification -Pattern '@\(\$ApiServiceName, \$WorkerServiceName\)' -Message 'Deployment verification must check both the API and worker services.'
Assert-FileMatch -Content $verification -Pattern 'must have an explicit boolean SyncFactors__Runtime__DryRunOnly' -Message 'Deployment verification must reject missing or invalid write-safety configuration.'
Assert-FileMatch -Content $verification -Pattern 'Invoke-HttpHealthCheck' -Message 'Deployment verification must require runtime readiness.'

$pipeline = Get-Content -Path $pipelinePath -Raw
Assert-FileMatch -Content $pipeline -Pattern '(?m)^\s+dryRunOnly:\s+true\s*$' -Message 'The production deployment pipeline must default dryRunOnly to true.'
Assert-FileMatch -Content $pipeline -Pattern 'Set-ServiceDryRunOnlyEnvironment' -Message 'The pipeline must harden pre-existing service environments during upgrades.'
Assert-FileMatch -Content $pipeline -Pattern 'Test-SyncFactorsWindowsDeployment\.ps1' -Message 'The pipeline must run deployment verification for patch and fresh installs.'
Assert-FileMatch -Content $pipeline -Pattern '/readyz' -Message 'The pipeline must verify API and worker runtime readiness.'

Write-Output 'Windows deployment safety checks passed.'
