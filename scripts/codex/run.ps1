[CmdletBinding()]
param(
    [ValidateSet('api', 'worker', 'mock', 'stack')]
    [string]$Service = 'stack',
    [ValidateSet('mock', 'real')]
    [string]$Profile = 'mock',
    [switch]$Restart,
    [switch]$SkipBuild,
    [Alias('h')]
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Show-Usage {
    @'
Usage:
  pwsh ./scripts/codex/run.ps1 -Service <api|worker|mock|stack> [-Profile <mock|real>] [-Restart] [-SkipBuild]
  pwsh ./scripts/codex/run.ps1 -Help
  pwsh ./scripts/codex/run.ps1

Services:
  api      Start the SyncFactors .NET API.
  worker   Start the SyncFactors worker service.
  mock     Start the mock SuccessFactors API.
  stack    Start the full local stack in separate terminals.

Options:
  -Profile <mock|real>  Select the run profile. Defaults to mock.
  -Restart              Stop existing local service processes before starting the selected service or stack.
  -SkipBuild            Skip the solution build step before starting the selected service.
  -Help                 Show this help text.

Examples:
  pwsh ./scripts/codex/run.ps1
  pwsh ./scripts/codex/run.ps1 -Service api
  pwsh ./scripts/codex/run.ps1 -Service stack -Restart
  pwsh ./scripts/codex/run.ps1 -Service worker -Profile real -SkipBuild
  pwsh ./scripts/codex/run.ps1 -Service stack -Profile mock
'@ | Write-Host
}

if ($Help) {
    Show-Usage
    exit 0
}

. (Join-Path $scriptDir 'Load-WorktreeEnv.ps1')
. (Join-Path $scriptDir '..' 'Start-SyncFactorsCommon.ps1')

function Get-ListeningProcessIds {
    param(
        [Parameter(Mandatory)]
        [string]$Port
    )

    $lines = @( & lsof "-nP" "-iTCP:$Port" "-sTCP:LISTEN" "-t" 2>$null )
    return $lines |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [int]$_.Trim() } |
        Sort-Object -Unique
}

function Get-ProcessIdsByCommandPattern {
    param(
        [Parameter(Mandatory)]
        [string[]]$Patterns
    )

    $lines = @( & ps "-ax" "-o" "pid=" "-o" "command=" 2>$null )
    $matches = foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $trimmed = $line.Trim()
        $firstSpace = $trimmed.IndexOf(' ')
        if ($firstSpace -lt 0) {
            continue
        }

        $pidText = $trimmed.Substring(0, $firstSpace).Trim()
        $command = $trimmed.Substring($firstSpace + 1).Trim()
        if ($Patterns | Where-Object { $command.Contains($_, [StringComparison]::OrdinalIgnoreCase) }) {
            [int]$pidText
        }
    }

    return $matches | Sort-Object -Unique
}

function Stop-LocalProcesses {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [int[]]$ProcessIds
    )

    $targets = $ProcessIds | Sort-Object -Unique
    if ($targets.Count -eq 0) {
        Write-Host "No running $Name processes found."
        return
    }

    Write-Host ("Stopping {0} process(es) for {1}: {2}" -f $targets.Count, $Name, ($targets -join ', ')) -ForegroundColor Yellow
    foreach ($pid in $targets) {
        Stop-Process -Id $pid -ErrorAction SilentlyContinue
    }

    Start-Sleep -Milliseconds 750

    foreach ($pid in $targets) {
        if (Get-Process -Id $pid -ErrorAction SilentlyContinue) {
            Stop-Process -Id $pid -Force -ErrorAction SilentlyContinue
        }
    }
}

function Restart-SelectedServices {
    param(
        [Parameter(Mandatory)]
        [string]$RequestedService,
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $apiProjectPath = Join-Path $RepositoryRoot 'src/SyncFactors.Api/SyncFactors.Api.csproj'
    $workerProjectPath = Join-Path $RepositoryRoot 'src/SyncFactors.Worker/SyncFactors.Worker.csproj'
    $mockProjectPath = Join-Path $RepositoryRoot 'src/SyncFactors.MockSuccessFactors/SyncFactors.MockSuccessFactors.csproj'

    $servicesToRestart = switch ($RequestedService) {
        'stack' { @('mock', 'api', 'worker') }
        default { @($RequestedService) }
    }

    foreach ($serviceName in $servicesToRestart) {
        switch ($serviceName) {
            'api' {
                $apiPids = @(
                    Get-ListeningProcessIds -Port $env:SYNCFACTORS_API_PORT
                    Get-ProcessIdsByCommandPattern -Patterns @($apiProjectPath, 'SyncFactors.Api')
                ) | Sort-Object -Unique
                Stop-LocalProcesses -Name 'SyncFactors API' -ProcessIds $apiPids
            }
            'worker' {
                $workerPids = Get-ProcessIdsByCommandPattern -Patterns @($workerProjectPath, 'SyncFactors.Worker')
                Stop-LocalProcesses -Name 'SyncFactors worker' -ProcessIds $workerPids
            }
            'mock' {
                $mockPids = @(
                    Get-ListeningProcessIds -Port $env:MOCK_SF_PORT
                    Get-ProcessIdsByCommandPattern -Patterns @($mockProjectPath, 'SyncFactors.MockSuccessFactors')
                ) | Sort-Object -Unique
                Stop-LocalProcesses -Name 'SyncFactors mock API' -ProcessIds $mockPids
            }
        }
    }
}

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

$activeProfile = $env:SYNCFACTORS_RUN_PROFILE.ToLowerInvariant()
$repoRoot = Resolve-ProjectRoot

if ($Restart) {
    Restart-SelectedServices -RequestedService $Service -RepositoryRoot $repoRoot
}

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
        $sharedArguments += @('-Profile', $Profile)
        if ($Restart) {
            $sharedArguments += '-Restart:$false'
        }

        if (-not $SkipBuild) {
            Invoke-SolutionBuild -ProjectRoot $repoRoot
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
