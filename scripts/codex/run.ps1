[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('api', 'worker', 'mock', 'stack')]
    [string]$Service,
    [ValidateSet('mock', 'real')]
    [string]$Profile,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDir 'Load-WorktreeEnv.ps1')
. (Join-Path $scriptDir '..' 'Start-SyncFactorsCommon.ps1')

if ($PSBoundParameters.ContainsKey('Profile')) {
    $env:SYNCFACTORS_RUN_PROFILE = $Profile
    if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_CONFIG_PATH)) {
        $env:SYNCFACTORS_PROFILE_CONFIG_PATH_ABS = if ($Profile -eq 'real') {
            $env:SYNCFACTORS_REAL_CONFIG_PATH_ABS
        }
        else {
            $env:SYNCFACTORS_MOCK_CONFIG_PATH_ABS
        }

        $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS = $env:SYNCFACTORS_PROFILE_CONFIG_PATH_ABS
    }
    else {
        $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS = $env:SYNCFACTORS_CONFIG_PATH_ABS
    }
}

$activeProfile = $env:SYNCFACTORS_RUN_PROFILE.ToLowerInvariant()

switch ($Service) {
    'api' {
        $arguments = @(
            './scripts/Start-SyncFactorsNextApi.ps1',
            '-ConfigPath', $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS,
            '-MappingConfigPath', $env:SYNCFACTORS_MAPPING_CONFIG_PATH_ABS,
            '-SqlitePath', $env:SYNCFACTORS_SQLITE_PATH_ABS,
            '-Urls', "http://127.0.0.1:$($env:SYNCFACTORS_API_PORT)"
        )

        if ($SkipBuild) {
            $arguments += '-SkipBuild'
        }

        & pwsh @arguments
        exit $LASTEXITCODE
    }
    'worker' {
        $arguments = @(
            './scripts/Start-SyncFactorsWorker.ps1',
            '-ConfigPath', $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS,
            '-MappingConfigPath', $env:SYNCFACTORS_MAPPING_CONFIG_PATH_ABS,
            '-SqlitePath', $env:SYNCFACTORS_SQLITE_PATH_ABS
        )

        if ($SkipBuild) {
            $arguments += '-SkipBuild'
        }

        & pwsh @arguments
        exit $LASTEXITCODE
    }
    'mock' {
        $arguments = @(
            './scripts/Start-SyncFactorsMockSuccessFactors.ps1',
            '-Urls', "http://127.0.0.1:$($env:MOCK_SF_PORT)"
        )

        if ($SkipBuild) {
            $arguments += '-SkipBuild'
        }

        & pwsh @arguments
        exit $LASTEXITCODE
    }
    'stack' {
        $sharedArguments = @()
        if ($PSBoundParameters.ContainsKey('Profile')) {
            $sharedArguments += @('-Profile', $Profile)
        }

        if (-not $SkipBuild) {
            Invoke-SolutionBuild -ProjectRoot (Resolve-ProjectRoot)
            $sharedArguments += '-SkipBuild'
        }

        if ($SkipBuild) {
            $sharedArguments += '-SkipBuild'
        }

        $terminalScriptPath = Join-Path $scriptDir 'Open-TerminalCommand.ps1'
        $workerArguments = @('-Service', 'worker') + $sharedArguments
        $startMockApi = $activeProfile -eq 'mock'
        $startSyncFactorsApi = $true

        if ($startMockApi) {
            $mockArguments = @('-Service', 'mock') + $sharedArguments
            & $terminalScriptPath 'SyncFactors mock API' './scripts/codex/run.ps1' $mockArguments
        }

        if ($startSyncFactorsApi) {
            $apiArguments = @('-Service', 'api') + $sharedArguments
            & $terminalScriptPath 'SyncFactors .NET API' './scripts/codex/run.ps1' $apiArguments
        }

        & $terminalScriptPath 'SyncFactors worker' './scripts/codex/run.ps1' $workerArguments
    }
}
