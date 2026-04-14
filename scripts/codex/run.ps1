[CmdletBinding()]
param(
    [ValidateSet('api', 'ui', 'worker', 'mock', 'stack')]
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
  pwsh ./scripts/codex/run.ps1 -Service <api|ui|worker|mock|stack> [-Profile <mock|real>] [-Restart] [-SkipBuild]
  pwsh ./scripts/codex/run.ps1 -Help
  pwsh ./scripts/codex/run.ps1

Services:
  api      Start the SyncFactors .NET API.
  ui       Start the SyncFactors API after building the frontend UI bundle.
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
  pwsh ./scripts/codex/run.ps1 -Service ui
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

function Resolve-ProfileConfigPath {
    param(
        [Parameter(Mandatory)]
        [string]$Profile,
        [Parameter(Mandatory)]
        [string]$MockConfigPath,
        [Parameter(Mandatory)]
        [string]$RealConfigPath
    )

    $normalizedProfile = $Profile.ToLowerInvariant()
    $preferredPath = switch ($normalizedProfile) {
        'real' { $RealConfigPath }
        'mock' { $MockConfigPath }
        default { throw "Unsupported profile '$Profile'. Expected 'mock' or 'real'." }
    }

    $fallbackPath = if ($preferredPath -eq $MockConfigPath) { $RealConfigPath } else { $MockConfigPath }

    if (Test-Path $preferredPath) {
        return $preferredPath
    }

    if (Test-Path $fallbackPath) {
        return $fallbackPath
    }

    return $preferredPath
}

function Get-ListeningProcessIds {
    param(
        [Parameter(Mandatory)]
        [string]$Port
    )

    if ([OperatingSystem]::IsWindows()) {
        try {
            $netstatLines = @( & netstat '-ano' '-p' 'tcp' 2>$null )
        }
        catch {
            return @()
        }

        $matches = foreach ($line in $netstatLines) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $trimmed = $line.Trim()
            if (-not $trimmed.Contains('LISTENING', [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $columns = $trimmed -split '\s+'
            if ($columns.Length -lt 5) {
                continue
            }

            $localAddress = $columns[1]
            $state = $columns[3]
            $processId = $columns[4]
            if (-not $state.Equals('LISTENING', [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $separatorIndex = $localAddress.LastIndexOf(':')
            if ($separatorIndex -lt 0) {
                continue
            }

            $localPort = $localAddress.Substring($separatorIndex + 1)
            if ($localPort -eq $Port) {
                [int]$processId
            }
        }

        return $matches | Sort-Object -Unique
    }

    try {
        $lines = @( & lsof "-nP" "-iTCP:$Port" "-sTCP:LISTEN" "-t" 2>$null )
    }
    catch {
        return @()
    }

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

    if ([OperatingSystem]::IsWindows()) {
        $getCimInstance = Get-Command 'Get-CimInstance' -ErrorAction SilentlyContinue
        if ($null -eq $getCimInstance) {
            Write-Warning 'Get-CimInstance is unavailable; command-line-based restart matching may be incomplete on Windows.'
            return @()
        }

        try {
            $processes = @( Get-CimInstance -ClassName Win32_Process -ErrorAction SilentlyContinue )
        }
        catch {
            return @()
        }

        $matches = foreach ($process in $processes) {
            $command = $process.CommandLine
            if ([string]::IsNullOrWhiteSpace($command)) {
                continue
            }

            if ($Patterns | Where-Object { $command.Contains($_, [StringComparison]::OrdinalIgnoreCase) }) {
                [int]$process.ProcessId
            }
        }

        return $matches | Sort-Object -Unique
    }

    try {
        $lines = @( & ps "-ax" "-o" "pid=" "-o" "command=" 2>$null )
    }
    catch {
        return @()
    }

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

function Get-ProcessIdsByCommandFragments {
    param(
        [Parameter(Mandatory)]
        [string[]]$Fragments
    )

    $requiredFragments = @($Fragments | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($requiredFragments.Count -eq 0) {
        return @()
    }

    if ([OperatingSystem]::IsWindows()) {
        $getCimInstance = Get-Command 'Get-CimInstance' -ErrorAction SilentlyContinue
        if ($null -eq $getCimInstance) {
            Write-Warning 'Get-CimInstance is unavailable; command-line-based restart matching may be incomplete on Windows.'
            return @()
        }

        try {
            $processes = @( Get-CimInstance -ClassName Win32_Process -ErrorAction SilentlyContinue )
        }
        catch {
            return @()
        }

        $matches = foreach ($process in $processes) {
            $command = $process.CommandLine
            if ([string]::IsNullOrWhiteSpace($command)) {
                continue
            }

            $isMatch = $true
            foreach ($fragment in $requiredFragments) {
                if (-not $command.Contains($fragment, [StringComparison]::OrdinalIgnoreCase)) {
                    $isMatch = $false
                    break
                }
            }

            if ($isMatch) {
                [int]$process.ProcessId
            }
        }

        return $matches | Sort-Object -Unique
    }

    try {
        $lines = @( & ps "-ax" "-o" "pid=" "-o" "command=" 2>$null )
    }
    catch {
        return @()
    }

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

        $isMatch = $true
        foreach ($fragment in $requiredFragments) {
            if (-not $command.Contains($fragment, [StringComparison]::OrdinalIgnoreCase)) {
                $isMatch = $false
                break
            }
        }

        if ($isMatch) {
            [int]$pidText
        }
    }

    return $matches | Sort-Object -Unique
}

function Stop-LocalProcesses {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [AllowNull()]
        [int[]]$ProcessIds = @()
    )

    $targets = @($ProcessIds | Where-Object { $null -ne $_ } | Sort-Object -Unique)
    if ($targets.Count -eq 0) {
        Write-Host "No running $Name processes found."
        return
    }

    Write-Host ("Stopping {0} process(es) for {1}: {2}" -f $targets.Count, $Name, ($targets -join ', ')) -ForegroundColor Yellow
    foreach ($processId in $targets) {
        Stop-Process -Id $processId -ErrorAction SilentlyContinue
    }

    Start-Sleep -Milliseconds 750

    foreach ($processId in $targets) {
        if (Get-Process -Id $processId -ErrorAction SilentlyContinue) {
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        }
    }
}

function Close-HostedTerminals {
    param(
        [Parameter(Mandatory)]
        [string[]]$Labels,
        [AllowNull()]
        [int[]]$HostProcessIds = @()
    )

    $hostTargets = @($HostProcessIds | Where-Object { $null -ne $_ -and $_ -ne $PID } | Sort-Object -Unique)
    if ([OperatingSystem]::IsWindows()) {
        if ($hostTargets.Count -gt 0) {
            Stop-LocalProcesses -Name 'hosted terminal window' -ProcessIds $hostTargets
        }

        return
    }

    if (-not [OperatingSystem]::IsMacOS()) {
        return
    }

    $terminalAppPath = if (Test-Path '/System/Applications/Utilities/Terminal.app') {
        '/System/Applications/Utilities/Terminal.app'
    }
    elseif (Test-Path '/Applications/Utilities/Terminal.app') {
        '/Applications/Utilities/Terminal.app'
    }
    else {
        $null
    }

    if ($null -eq $terminalAppPath) {
        return
    }

    $script = @'
on run argv
    set targetLabel to item 1 of argv

    tell application "Terminal"
        set matchingWindows to {}

        repeat with currentWindow in windows
            set shouldCloseWindow to false

            repeat with currentTab in tabs of currentWindow
                set tabTitle to ""
                set tabName to ""

                try
                    set tabTitle to custom title of currentTab
                end try

                try
                    set tabName to name of currentTab
                end try

                if tabTitle is targetLabel or tabName is targetLabel then
                    set shouldCloseWindow to true
                    exit repeat
                end if
            end repeat

            if shouldCloseWindow then
                copy currentWindow to end of matchingWindows
            end if
        end repeat

        repeat with targetWindow in matchingWindows
            try
                close targetWindow saving no
            end try
        end repeat

        repeat with currentWindow in windows
            if (count of tabs of currentWindow) is 0 then
                try
                    close currentWindow saving no
                end try
            end if
        end repeat
    end tell
end run
'@

    foreach ($label in ($Labels | Sort-Object -Unique)) {
        $script | & osascript - $label | Out-Null
    }
}

function Restart-SelectedServices {
    param(
        [Parameter(Mandatory)]
        [string]$RequestedService,
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [switch]$PreserveHostedTerminals
    )

    $apiProjectPath = Join-Path $RepositoryRoot 'src/SyncFactors.Api/SyncFactors.Api.csproj'
    $workerProjectPath = Join-Path $RepositoryRoot 'src/SyncFactors.Worker/SyncFactors.Worker.csproj'
    $mockProjectPath = Join-Path $RepositoryRoot 'src/SyncFactors.MockSuccessFactors/SyncFactors.MockSuccessFactors.csproj'

    $servicesToRestart = switch ($RequestedService) {
        'stack' { @('mock', 'api', 'worker') }
        'ui' { @('api') }
        default { @($RequestedService) }
    }

    $terminalLabels = $servicesToRestart | ForEach-Object {
        switch ($_) {
            'api' { 'SyncFactors .NET API' }
            'worker' { 'SyncFactors worker' }
            'mock' { 'SyncFactors mock API' }
        }
    }

    $hostedTerminalProcessIds = foreach ($serviceName in $servicesToRestart) {
        Get-ProcessIdsByCommandFragments -Fragments @(
            'scripts/codex/run.ps1',
            '-Service',
            $serviceName
        )
    }

    if (-not $PreserveHostedTerminals) {
        Close-HostedTerminals -Labels $terminalLabels -HostProcessIds $hostedTerminalProcessIds
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
    $env:SYNCFACTORS_PROFILE_CONFIG_PATH_ABS = Resolve-ProfileConfigPath `
        -Profile $Profile `
        -MockConfigPath $env:SYNCFACTORS_MOCK_CONFIG_PATH_ABS `
        -RealConfigPath $env:SYNCFACTORS_REAL_CONFIG_PATH_ABS

    $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS = $env:SYNCFACTORS_PROFILE_CONFIG_PATH_ABS
}
else {
    $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS = $env:SYNCFACTORS_CONFIG_PATH_ABS
}

$activeProfile = $env:SYNCFACTORS_RUN_PROFILE.ToLowerInvariant()
$repoRoot = Resolve-ProjectRoot

if ($Restart) {
    $preserveHostedTerminals = $false
    Restart-SelectedServices -RequestedService $Service -RepositoryRoot $repoRoot -PreserveHostedTerminals:$preserveHostedTerminals
}

switch ($Service) {
    'api' {
        $arguments = @(
            './scripts/Start-SyncFactorsNextApi.ps1',
            '-ConfigPath', $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS,
            '-MappingConfigPath', $env:SYNCFACTORS_MAPPING_CONFIG_PATH_ABS,
            '-SqlitePath', $env:SYNCFACTORS_SQLITE_PATH_ABS,
            '-Urls', "https://127.0.0.1:$($env:SYNCFACTORS_API_PORT)"
        )

        if ($SkipBuild) {
            $arguments += '-SkipBuild'
        }

        & pwsh @arguments
        exit $LASTEXITCODE
    }
    'ui' {
        $arguments = @(
            './scripts/Start-SyncFactorsNextApi.ps1',
            '-ConfigPath', $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS,
            '-MappingConfigPath', $env:SYNCFACTORS_MAPPING_CONFIG_PATH_ABS,
            '-SqlitePath', $env:SYNCFACTORS_SQLITE_PATH_ABS,
            '-Urls', "https://127.0.0.1:$($env:SYNCFACTORS_API_PORT)"
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
        $reuseHostedTerminals = $false
        $workerArguments = @('-Service', 'worker') + $sharedArguments
        $startMockApi = $activeProfile -eq 'mock'
        $startSyncFactorsApi = $true

        if ($startMockApi) {
            $mockArguments = @('-Service', 'mock') + $sharedArguments
            & $terminalScriptPath 'SyncFactors mock API' './scripts/codex/run.ps1' $mockArguments -ReuseIfExists:$reuseHostedTerminals
        }

        if ($startSyncFactorsApi) {
            $apiArguments = @('-Service', 'api') + $sharedArguments
            & $terminalScriptPath 'SyncFactors .NET API' './scripts/codex/run.ps1' $apiArguments -ReuseIfExists:$reuseHostedTerminals
        }

        & $terminalScriptPath 'SyncFactors worker' './scripts/codex/run.ps1' $workerArguments -ReuseIfExists:$reuseHostedTerminals
    }
}
