[CmdletBinding()]
param(
    [string[]]$Scenario = @(),
    [string]$ReportPath,
    [string]$ApiUrl,
    [string]$MockUrl,
    [string]$UsernameEnv = 'SYNCFACTORS_AUTOMATION_USERNAME',
    [string]$PasswordEnv = 'SYNCFACTORS_AUTOMATION_PASSWORD',
    [string[]]$Tags = @(),
    [switch]$StartStack,
    [switch]$AllowAdReset,
    [switch]$SkipBuild,
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [int]$TimeoutMinutes = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

$projectRoot = Resolve-ProjectRoot
$automationProjectPath = Join-Path $projectRoot 'src/SyncFactors.Automation/SyncFactors.Automation.csproj'
$worktreeEnvScript = Join-Path $projectRoot 'scripts/codex/Load-WorktreeEnv.ps1'

if (Test-Path $worktreeEnvScript) {
    . $worktreeEnvScript
}

if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_RUN_PROFILE) -or
    ([string]::IsNullOrWhiteSpace($ConfigPath) -and -not [string]::IsNullOrWhiteSpace($env:SYNCFACTORS_RUN_PROFILE) -and $env:SYNCFACTORS_RUN_PROFILE -ne 'mock')) {
    $env:SYNCFACTORS_RUN_PROFILE = 'mock'
}

if ($Scenario.Count -eq 0) {
    $Scenario = @(Join-Path $projectRoot 'config/automation/*.json')
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $projectRoot ("state/runtime/automation-reports/e2e-{0}.md" -f (Get-Date -Format 'yyyyMMddHHmmss'))
}

if ([string]::IsNullOrWhiteSpace($ApiUrl)) {
    $port = if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_API_PORT)) { '5087' } else { $env:SYNCFACTORS_API_PORT }
    $hostName = if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_API_PUBLIC_HOST)) { '127.0.0.1' } else { $env:SYNCFACTORS_API_PUBLIC_HOST }
    $ApiUrl = "https://$hostName`:$port"
}

if ([string]::IsNullOrWhiteSpace($MockUrl)) {
    $mockPort = if ([string]::IsNullOrWhiteSpace($env:MOCK_SF_PORT)) { '18080' } else { $env:MOCK_SF_PORT }
    $MockUrl = "http://127.0.0.1:$mockPort"
}

$username = [Environment]::GetEnvironmentVariable($UsernameEnv)
$password = [Environment]::GetEnvironmentVariable($PasswordEnv)
if ([string]::IsNullOrWhiteSpace($username) -or [string]::IsNullOrWhiteSpace($password)) {
    throw "Set $UsernameEnv and $PasswordEnv for a local operator/admin account before running automation."
}

if ($StartStack) {
    $runScript = Join-Path $projectRoot 'scripts/codex/run.ps1'
    $stackArgs = @('-Service', 'stack', '-Profile', 'mock')
    if ($SkipBuild) {
        $stackArgs += '-SkipBuild'
    }

    & pwsh $runScript @stackArgs
}

Push-Location $projectRoot
try {
    Initialize-DotnetEnvironment -ProjectRoot $projectRoot

    if (-not $SkipBuild) {
        Invoke-SolutionBuild -ProjectRoot $projectRoot
    }

    $runnerArgs = @()
    foreach ($item in $Scenario) {
        $runnerArgs += @('--scenario', $item)
    }

    $runnerArgs += @(
        '--report', $ReportPath,
        '--api-url', $ApiUrl,
        '--mock-url', $MockUrl,
        '--timeout-minutes', $TimeoutMinutes.ToString([Globalization.CultureInfo]::InvariantCulture)
    )

    if ($AllowAdReset) {
        $runnerArgs += '--allow-ad-reset'
    }

    foreach ($tag in $Tags) {
        $runnerArgs += @('--tags', $tag)
    }

    if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
        $runnerArgs += @('--config', (Resolve-Path $ConfigPath).Path)
    }

    if (-not [string]::IsNullOrWhiteSpace($MappingConfigPath)) {
        $runnerArgs += @('--mapping', (Resolve-Path $MappingConfigPath).Path)
    }

    $dotnetArgs = @('run')
    if ($SkipBuild) {
        $dotnetArgs += '--no-build'
    }
    else {
        $dotnetArgs += '--no-restore'
    }

    $dotnetArgs += @('--project', $automationProjectPath, '--')
    $dotnetArgs += $runnerArgs
    dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "SyncFactors E2E automation failed."
    }
}
finally {
    Pop-Location
}
