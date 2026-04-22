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

$scriptPath = $MyInvocation.MyCommand.Path
$scriptDir = Split-Path -Parent $scriptPath
$repoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $scriptDir) '..')).ProviderPath

. (Join-Path $scriptDir '..' 'SyncFactorsJson.ps1')

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

Local runner settings:
  config/local.codex-run.json        Local launcher settings (ignored by git).
  config/sample.codex-run.json       Tracked defaults copied into the local file when missing.

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

function ConvertTo-BooleanSetting {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Value,
        [Parameter(Mandatory)]
        [string]$SettingPath
    )

    if ($Value -is [bool]) {
        return $Value
    }

    try {
        return [System.Convert]::ToBoolean($Value, [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "Setting '$SettingPath' must be a JSON boolean."
    }
}

function Get-CodexRunSettings {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $localConfigPath = Join-Path $RepositoryRoot 'config/local.codex-run.json'
    $sampleConfigPath = Join-Path $RepositoryRoot 'config/sample.codex-run.json'
    $configPath = if (Test-Path $localConfigPath) { $localConfigPath } elseif (Test-Path $sampleConfigPath) { $sampleConfigPath } else { $null }

    $settings = [ordered]@{
        GitPullBeforeStackStart = $true
        ConfigPath = $configPath
    }

    if ($null -eq $configPath) {
        return [pscustomobject]$settings
    }

    try {
        $rawConfig = Get-Content -Path $configPath -Raw
        $parsedConfig = ConvertFrom-SyncFactorsJson -InputObject $rawConfig
    }
    catch {
        throw "Failed to read runner settings from '$configPath'. $_"
    }

    if ($parsedConfig -isnot [System.Collections.IDictionary]) {
        throw "Runner settings file '$configPath' must contain a JSON object."
    }

    if ($parsedConfig.Contains('git')) {
        $gitSettings = $parsedConfig['git']
        if ($gitSettings -isnot [System.Collections.IDictionary]) {
            throw "Setting 'git' in '$configPath' must be a JSON object."
        }

        if ($gitSettings.Contains('pullBeforeStackStart')) {
            $settings.GitPullBeforeStackStart = ConvertTo-BooleanSetting `
                -Value $gitSettings['pullBeforeStackStart'] `
                -SettingPath 'git.pullBeforeStackStart'
        }
    }

    return [pscustomobject]$settings
}

function Get-CurrentLauncherArgumentList {
    $arguments = @(
        '-NoProfile',
        '-File', $scriptPath,
        '-Service', $Service,
        '-Profile', $Profile
    )

    if ($Restart) {
        $arguments += '-Restart'
    }

    if ($SkipBuild) {
        $arguments += '-SkipBuild'
    }

    return $arguments
}

function Get-PowerShellExecutablePath {
    $pwshCommand = Get-Command 'pwsh' -ErrorAction SilentlyContinue
    if ($null -ne $pwshCommand -and -not [string]::IsNullOrWhiteSpace($pwshCommand.Source)) {
        return $pwshCommand.Source
    }

    $executableName = if ([OperatingSystem]::IsWindows()) { 'pwsh.exe' } else { 'pwsh' }
    return Join-Path $PSHOME $executableName
}

function Invoke-PrestartGitPull {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [pscustomobject]$RunSettings
    )

    if (-not $RunSettings.GitPullBeforeStackStart) {
        if (-not [string]::IsNullOrWhiteSpace($RunSettings.ConfigPath)) {
            Write-Host "Skipping git pull before stack start because git.pullBeforeStackStart is disabled in $($RunSettings.ConfigPath)." -ForegroundColor DarkGray
        }
        else {
            Write-Host 'Skipping git pull before stack start because the setting is disabled.' -ForegroundColor DarkGray
        }

        return
    }

    if (-not (Get-Command 'git' -ErrorAction SilentlyContinue)) {
        Write-Warning 'git is unavailable; skipping pull before stack start.'
        return
    }

    $branchName = ''
    try {
        $branchName = (& git -C $RepositoryRoot branch --show-current 2>$null).Trim()
    }
    catch {
        $branchName = ''
    }

    if ([string]::IsNullOrWhiteSpace($branchName)) {
        Write-Warning 'Current worktree is not on a named branch; skipping pull before stack start.'
        return
    }

    $upstreamName = ''
    try {
        $upstreamName = (& git -C $RepositoryRoot rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>$null).Trim()
    }
    catch {
        $upstreamName = ''
    }

    if ([string]::IsNullOrWhiteSpace($upstreamName)) {
        Write-Warning "Branch '$branchName' has no upstream; skipping pull before stack start."
        return
    }

    $headBefore = ''
    try {
        $headBefore = (& git -C $RepositoryRoot rev-parse HEAD 2>$null).Trim()
    }
    catch {
        $headBefore = ''
    }

    Write-Host "Running git pull --ff-only for $branchName ($upstreamName) before starting the stack..." -ForegroundColor Cyan
    & git -C $RepositoryRoot pull --ff-only
    if ($LASTEXITCODE -ne 0) {
        throw 'git pull --ff-only failed before starting the stack.'
    }

    $headAfter = ''
    try {
        $headAfter = (& git -C $RepositoryRoot rev-parse HEAD 2>$null).Trim()
    }
    catch {
        $headAfter = ''
    }

    if ($headBefore -eq $headAfter) {
        return
    }

    Write-Host "Git updated the worktree ($headBefore -> $headAfter). Relaunching the launcher so the latest scripts are used..." -ForegroundColor Cyan
    $pwshExecutable = Get-PowerShellExecutablePath
    $relaunchArguments = Get-CurrentLauncherArgumentList
    & $pwshExecutable @relaunchArguments
    if ($null -eq $LASTEXITCODE) {
        exit 0
    }

    exit $LASTEXITCODE
}

$runSettings = Get-CodexRunSettings -RepositoryRoot $repoRoot
if ($Service -eq 'stack') {
    Invoke-PrestartGitPull -RepositoryRoot $repoRoot -RunSettings $runSettings
}

. (Join-Path $scriptDir 'Load-WorktreeEnv.ps1')
. (Join-Path $scriptDir '..' 'Start-SyncFactorsCommon.ps1')
. (Join-Path $scriptDir '..' 'Sync-LocalConfigFormat.ps1')
. (Join-Path $scriptDir '..' 'Test-SyncFactorsActiveDirectoryOuAccess.ps1')

function Get-HashtableValue {
    param(
        [AllowNull()]
        [System.Collections.IDictionary]$Table,
        [Parameter(Mandatory)]
        [string]$Key
    )

    if ($null -eq $Table -or -not $Table.Contains($Key)) {
        return $null
    }

    return $Table[$Key]
}

function Get-JsonConfigDocument {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path $Path)) {
        throw "$Label file '$Path' could not be found."
    }

    try {
        $document = ConvertFrom-SyncFactorsJson -InputObject (Get-Content -Path $Path -Raw)
    }
    catch {
        throw "Failed to parse $Label file '$Path' as JSON. $_"
    }

    if ($document -isnot [System.Collections.IDictionary]) {
        throw "$Label file '$Path' must contain a JSON object."
    }

    return $document
}

function Test-HashtableHasAllKeys {
    param(
        [AllowNull()]
        [System.Collections.IDictionary]$Table,
        [Parameter(Mandatory)]
        [string[]]$Keys
    )

    if ($null -eq $Table) {
        return $false
    }

    foreach ($key in $Keys) {
        if (-not $Table.Contains($key)) {
            return $false
        }
    }

    return $true
}

function Assert-ConfigPathShapes {
    param(
        [Parameter(Mandatory)]
        [string]$ResolvedConfigPath,
        [Parameter(Mandatory)]
        [string]$MappingConfigPath
    )

    $syncDocument = Get-JsonConfigDocument -Path $ResolvedConfigPath -Label 'Sync config'
    $mappingDocument = Get-JsonConfigDocument -Path $MappingConfigPath -Label 'Mapping config'

    $syncLooksValid = Test-HashtableHasAllKeys -Table $syncDocument -Keys @('secrets', 'successFactors', 'ad')
    $syncLooksLikeMapping = $syncDocument.Contains('mappings')
    if (-not $syncLooksValid) {
        if ($syncLooksLikeMapping) {
            throw "Resolved sync config '$ResolvedConfigPath' looks like a mapping config because it contains 'mappings' but not the required sync config keys ('secrets', 'successFactors', 'ad'). Check SYNCFACTORS_CONFIG_PATH and leave it blank for profile-based resolution unless you intentionally want an explicit sync config override."
        }

        throw "Resolved sync config '$ResolvedConfigPath' is missing one or more required top-level keys: 'secrets', 'successFactors', 'ad'."
    }

    $mappingLooksValid = Test-HashtableHasAllKeys -Table $mappingDocument -Keys @('mappings')
    $mappingLooksLikeSync = Test-HashtableHasAllKeys -Table $mappingDocument -Keys @('secrets', 'successFactors', 'ad')
    if (-not $mappingLooksValid) {
        if ($mappingLooksLikeSync) {
            throw "Resolved mapping config '$MappingConfigPath' looks like a sync config because it contains 'secrets', 'successFactors', and 'ad' instead of a top-level 'mappings' array. Check SYNCFACTORS_MAPPING_CONFIG_PATH."
        }

        throw "Resolved mapping config '$MappingConfigPath' is missing the required top-level 'mappings' array."
    }
}

function Add-RequiredSecureStoreVariable {
    param(
        [System.Collections.Generic.List[string]]$RequiredVariables,
        [AllowNull()]
        [string]$VariableName
    )

    if ([string]::IsNullOrWhiteSpace($VariableName)) {
        return
    }

    if (-not (Test-SyncFactorsSecureStoreVariableName -VariableName $VariableName)) {
        throw "Required secret variable '$VariableName' is not in the SyncFactors secure-store allowlist."
    }

    if (-not $RequiredVariables.Contains($VariableName)) {
        $RequiredVariables.Add($VariableName)
    }
}

function Get-RequiredSyncConfigSecretNames {
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath
    )

    if ([string]::IsNullOrWhiteSpace($ConfigPath) -or -not (Test-Path $ConfigPath)) {
        return @()
    }

    $document = ConvertFrom-SyncFactorsJson -InputObject (Get-Content -Path $ConfigPath -Raw)
    if ($document -isnot [System.Collections.IDictionary]) {
        throw "Sync config '$ConfigPath' must contain a JSON object."
    }

    $required = [System.Collections.Generic.List[string]]::new()
    $secrets = Get-HashtableValue -Table $document -Key 'secrets'
    $successFactors = Get-HashtableValue -Table $document -Key 'successFactors'
    $auth = Get-HashtableValue -Table $successFactors -Key 'auth'
    $mode = [string](Get-HashtableValue -Table $auth -Key 'mode')

    if ($auth -is [System.Collections.IDictionary] -and $mode.Equals('basic', [StringComparison]::OrdinalIgnoreCase)) {
        $basic = Get-HashtableValue -Table $auth -Key 'basic'
        if ([string]::IsNullOrWhiteSpace([string](Get-HashtableValue -Table $basic -Key 'username'))) {
            Add-RequiredSecureStoreVariable -RequiredVariables $required -VariableName ([string](Get-HashtableValue -Table $secrets -Key 'successFactorsUsernameEnv'))
        }

        if ([string]::IsNullOrWhiteSpace([string](Get-HashtableValue -Table $basic -Key 'password'))) {
            Add-RequiredSecureStoreVariable -RequiredVariables $required -VariableName ([string](Get-HashtableValue -Table $secrets -Key 'successFactorsPasswordEnv'))
        }
    }

    if ($auth -is [System.Collections.IDictionary] -and $mode.Equals('oauth', [StringComparison]::OrdinalIgnoreCase)) {
        $oauth = Get-HashtableValue -Table $auth -Key 'oauth'
        if ([string]::IsNullOrWhiteSpace([string](Get-HashtableValue -Table $oauth -Key 'clientId'))) {
            Add-RequiredSecureStoreVariable -RequiredVariables $required -VariableName ([string](Get-HashtableValue -Table $secrets -Key 'successFactorsClientIdEnv'))
        }

        if ([string]::IsNullOrWhiteSpace([string](Get-HashtableValue -Table $oauth -Key 'clientSecret'))) {
            Add-RequiredSecureStoreVariable -RequiredVariables $required -VariableName ([string](Get-HashtableValue -Table $secrets -Key 'successFactorsClientSecretEnv'))
        }
    }

    $activeDirectory = Get-HashtableValue -Table $document -Key 'ad'
    if ([string]::IsNullOrWhiteSpace([string](Get-HashtableValue -Table $activeDirectory -Key 'server'))) {
        Add-RequiredSecureStoreVariable -RequiredVariables $required -VariableName ([string](Get-HashtableValue -Table $secrets -Key 'adServerEnv'))
    }

    return $required.ToArray()
}

function Test-SecretPromptAvailable {
    if (-not [Environment]::UserInteractive) {
        return $false
    }

    try {
        return -not [Console]::IsInputRedirected
    }
    catch {
        return $false
    }
}

function Get-RepoRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$Path
    )

    return [System.IO.Path]::GetRelativePath(
        [System.IO.Path]::GetFullPath($RepositoryRoot),
        [System.IO.Path]::GetFullPath($Path))
}

function Get-LauncherInvocationCommand {
    param(
        [Parameter(Mandatory)]
        [string]$ServiceName,
        [Parameter(Mandatory)]
        [string]$ProfileName,
        [switch]$RestartSelected,
        [switch]$SkipBuildStep
    )

    $parts = @(
        'pwsh',
        './scripts/codex/run.ps1',
        '-Service', $ServiceName,
        '-Profile', $ProfileName
    )

    if ($RestartSelected) {
        $parts += '-Restart'
    }

    if ($SkipBuildStep) {
        $parts += '-SkipBuild'
    }

    return ($parts -join ' ')
}

function Start-MockServiceHostedTerminal {
    param(
        [Parameter(Mandatory)]
        [string]$ProfileName,
        [switch]$RestartSelected,
        [switch]$SkipBuildStep
    )

    $terminalScriptPath = Join-Path $scriptDir 'Open-TerminalCommand.ps1'
    $reuseHostedTerminals = $false
    $mockArguments = @('-Service', 'mock', '-Profile', $ProfileName)

    if ($RestartSelected) {
        $mockArguments += '-Restart'
    }

    if ($SkipBuildStep) {
        $mockArguments += '-SkipBuild'
    }

    & $terminalScriptPath 'SyncFactors mock API' './scripts/codex/run.ps1' $mockArguments -ReuseIfExists:$reuseHostedTerminals
}

function Get-LocalConfigDriftRemediationMessage {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [pscustomobject[]]$DriftedConfigs,
        [Parameter(Mandatory)]
        [string]$RunCommand
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('Local config/env drift blocked startup because the launcher could not prompt in this headless session.')
    $lines.Add('Drifted files:')
    foreach ($config in $DriftedConfigs) {
        $lines.Add("  - $(Get-RepoRelativePath -RepositoryRoot $RepositoryRoot -Path $config.LocalConfigPath)")
    }

    $lines.Add('Fix the local config/env files with:')
    $lines.Add('  pwsh ./scripts/Update-LocalSyncFactorsConfig.ps1')
    $lines.Add('Then rerun the launcher with:')
    $lines.Add("  $RunCommand")

    return $lines -join [Environment]::NewLine
}

function Prompt-ForLocalConfigRewrite {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [pscustomobject[]]$DriftedConfigs
    )

    Write-Warning 'Local config/env files drifted from their tracked samples.'
    foreach ($config in $DriftedConfigs) {
        $localRelativePath = Get-RepoRelativePath -RepositoryRoot $RepositoryRoot -Path $config.LocalConfigPath
        $sampleRelativePath = Get-RepoRelativePath -RepositoryRoot $RepositoryRoot -Path $config.SampleConfigPath
        Write-Host "  $localRelativePath <- $sampleRelativePath" -ForegroundColor Yellow
    }

    $response = Read-Host 'Rewrite the drifted local config/env files to match the sample format now? [y/N]'
    return $response -match '^(y|yes)$'
}

function Ensure-TrackedLocalConfigFormats {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$RunCommand
    )

    $driftedConfigs = @(
        Get-TrackedLocalConfigDrift -RepositoryRoot $RepositoryRoot
        Get-TrackedWorktreeEnvDrift -RepositoryRoot $RepositoryRoot
    )
    if ($driftedConfigs.Length -eq 0) {
        return
    }

    if (-not (Test-SecretPromptAvailable)) {
        $message = Get-LocalConfigDriftRemediationMessage `
            -RepositoryRoot $RepositoryRoot `
            -DriftedConfigs $driftedConfigs `
            -RunCommand $RunCommand
        Write-Error $message
        throw $message
    }

    if (-not (Prompt-ForLocalConfigRewrite -RepositoryRoot $RepositoryRoot -DriftedConfigs $driftedConfigs)) {
        Write-Warning 'Continuing without rewriting the local config/env files. Run pwsh ./scripts/Update-LocalSyncFactorsConfig.ps1 when you want to realign them with the tracked samples.'
        return
    }

    $results = @(
        Sync-TrackedLocalConfigFormats -RepositoryRoot $RepositoryRoot
        Sync-TrackedWorktreeEnvFormats -RepositoryRoot $RepositoryRoot
    )
    foreach ($result in $results) {
        if ($result.BackupPath) {
            Write-Host "Backed up $(Get-RepoRelativePath -RepositoryRoot $RepositoryRoot -Path $result.LocalConfigPath) -> $(Get-RepoRelativePath -RepositoryRoot $RepositoryRoot -Path $result.BackupPath)" -ForegroundColor DarkGray
        }

        if ($result.Drifted) {
            Write-Host "Rewrote $(Get-RepoRelativePath -RepositoryRoot $RepositoryRoot -Path $result.LocalConfigPath) to match $(Get-RepoRelativePath -RepositoryRoot $RepositoryRoot -Path $result.SampleConfigPath)." -ForegroundColor Green
        }
    }
}

function ConvertTo-PlainText {
    param(
        [Parameter(Mandatory)]
        [System.Security.SecureString]$SecureString
    )

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
    }
}

function Prompt-ForSecretValue {
    param(
        [Parameter(Mandatory)]
        [string]$VariableName
    )

    $secureValue = Read-Host -Prompt "Enter value for $VariableName" -AsSecureString
    $value = ConvertTo-PlainText -SecureString $secureValue
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "A non-empty value is required for $VariableName."
    }

    return $value
}

function Get-SecureStoreDescription {
    param(
        [Parameter(Mandatory)]
        [string]$EnvFilePath
    )

    if ([OperatingSystem]::IsWindows()) {
        return 'Windows Credential Manager'
    }

    if ($IsMacOS) {
        $service = Resolve-SyncFactorsKeychainServiceName -EnvFilePath $EnvFilePath
        return "macOS Keychain ($service)"
    }

    return 'the platform secure store'
}

function Invoke-ApiLauncherProbe {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$Action,
        [switch]$NoBuild
    )

    $arguments = @(
        'run',
        '--project', (Join-Path $RepositoryRoot 'src/SyncFactors.Api/SyncFactors.Api.csproj'),
        '--no-launch-profile'
    )

    if ($NoBuild) {
        $arguments += '--no-build'
    }

    $arguments += @('--', '--launcher-probe', $Action)

    $output = @(& dotnet @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $details = ($output | Out-String).Trim()
        throw "Launcher probe '$Action' failed. $details"
    }

    $result = $output |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Last 1

    if ($null -eq $result) {
        throw "Launcher probe '$Action' did not return a result."
    }

    switch ($result.ToString().Trim().ToLowerInvariant()) {
        'true' { return $true }
        'false' { return $false }
        default { throw "Launcher probe '$Action' returned unexpected output '$result'." }
    }
}

function Get-RequiredAuthSecretNames {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$ServiceName,
        [switch]$ProbeNoBuild
    )

    $required = [System.Collections.Generic.List[string]]::new()

    if ($ServiceName -notin @('api', 'ui', 'stack')) {
        return @()
    }

    $authMode = [string]$env:SYNCFACTORS__AUTH__MODE
    $oidcConfigured = -not [string]::IsNullOrWhiteSpace([string]$env:SYNCFACTORS__AUTH__OIDC__AUTHORITY) -and
        -not [string]::IsNullOrWhiteSpace([string]$env:SYNCFACTORS__AUTH__OIDC__CLIENTID)

    if (($authMode.Equals('oidc', [StringComparison]::OrdinalIgnoreCase) -or $authMode.Equals('hybrid', [StringComparison]::OrdinalIgnoreCase)) -and $oidcConfigured) {
        Add-RequiredSecureStoreVariable -RequiredVariables $required -VariableName 'SYNCFACTORS__AUTH__OIDC__CLIENTSECRET'
    }

    $bootstrapUsernameConfigured = -not [string]::IsNullOrWhiteSpace([string]$env:SYNCFACTORS__AUTH__BOOTSTRAPADMIN__USERNAME)
    if ($bootstrapUsernameConfigured -and (Invoke-ApiLauncherProbe -RepositoryRoot $RepositoryRoot -Action 'bootstrap-required' -NoBuild:$ProbeNoBuild)) {
        Add-RequiredSecureStoreVariable -RequiredVariables $required -VariableName 'SYNCFACTORS__AUTH__BOOTSTRAPADMIN__PASSWORD'
    }

    return $required.ToArray()
}

function Get-RequiredSecretNamesForService {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$ServiceName,
        [Parameter(Mandatory)]
        [string]$ResolvedConfigPath,
        [switch]$ProbeNoBuild
    )

    $required = [System.Collections.Generic.List[string]]::new()

    if ($ServiceName -in @('api', 'ui', 'worker', 'stack')) {
        foreach ($name in Get-RequiredSyncConfigSecretNames -ConfigPath $ResolvedConfigPath) {
            Add-RequiredSecureStoreVariable -RequiredVariables $required -VariableName $name
        }
    }

    foreach ($name in Get-RequiredAuthSecretNames -RepositoryRoot $RepositoryRoot -ServiceName $ServiceName -ProbeNoBuild:$ProbeNoBuild) {
        Add-RequiredSecureStoreVariable -RequiredVariables $required -VariableName $name
    }

    return $required.ToArray()
}

function Ensure-RequiredSecureStoreValues {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$EnvFilePath,
        [AllowNull()]
        [string[]]$VariableNames
    )

    if (-not ([OperatingSystem]::IsWindows() -or $IsMacOS)) {
        return
    }

    $missing = @(
        @($VariableNames) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Where-Object { [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) } |
        Sort-Object -Unique
    )

    if ($missing.Length -eq 0) {
        return
    }

    if (-not (Test-SecretPromptAvailable)) {
        $storeDescription = Get-SecureStoreDescription -EnvFilePath $EnvFilePath
        throw "Missing required secret value(s): $($missing -join ', '). The launcher checked the current environment, $storeDescription, and .env.worktree. Start this command from an interactive terminal to be prompted, or populate $storeDescription first."
    }

    foreach ($name in $missing) {
        $value = Prompt-ForSecretValue -VariableName $name
        $storeLabel = Set-SyncFactorsSecureStoreValue -RepoRoot $RepositoryRoot -EnvFilePath $EnvFilePath -VariableName $name -Value $value
        [Environment]::SetEnvironmentVariable($name, $value)
        Write-Host "Stored $name in $storeLabel" -ForegroundColor Green
    }
}

function Ensure-ConfiguredActiveDirectoryOusAccessible {
    param(
        [Parameter(Mandatory)]
        [string]$ServiceName,
        [Parameter(Mandatory)]
        [string]$ResolvedConfigPath
    )

    if ($ServiceName -notin @('worker', 'stack')) {
        return
    }

    Assert-SyncFactorsConfiguredAdOusAccessible -ConfigPath $ResolvedConfigPath
}

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

function Format-UrlHost {
    param(
        [Parameter(Mandatory)]
        [string]$HostName
    )

    if ($HostName.Contains(':') -and -not ($HostName.StartsWith('[') -and $HostName.EndsWith(']'))) {
        return "[$HostName]"
    }

    return $HostName
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
$apiPublicHost = Format-UrlHost -HostName $env:SYNCFACTORS_API_PUBLIC_HOST
$apiPortalUrl = "https://$apiPublicHost`:$($env:SYNCFACTORS_API_PORT)"
$mockBaseUrl = "http://127.0.0.1:$($env:MOCK_SF_PORT)"
$mockAdminUrl = "$mockBaseUrl/admin"
$mockODataUrl = "$mockBaseUrl/odata/v2"
$worktreeEnvFile = Join-Path $repoRoot '.env.worktree'
$launcherCommand = Get-LauncherInvocationCommand `
    -ServiceName $Service `
    -ProfileName $Profile `
    -RestartSelected:$Restart `
    -SkipBuildStep:$SkipBuild
Ensure-TrackedLocalConfigFormats -RepositoryRoot $repoRoot -RunCommand $launcherCommand
if ($Service -in @('api', 'ui', 'worker', 'stack')) {
    Assert-ConfigPathShapes `
        -ResolvedConfigPath $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS `
        -MappingConfigPath $env:SYNCFACTORS_MAPPING_CONFIG_PATH_ABS
}
$requiredSecretNames = @(Get-RequiredSecretNamesForService `
    -RepositoryRoot $repoRoot `
    -ServiceName $Service `
    -ResolvedConfigPath $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS `
    -ProbeNoBuild:$SkipBuild)
Ensure-RequiredSecureStoreValues -RepositoryRoot $repoRoot -EnvFilePath $worktreeEnvFile -VariableNames $requiredSecretNames
Ensure-ConfiguredActiveDirectoryOusAccessible `
    -ServiceName $Service `
    -ResolvedConfigPath $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS

$env:SYNCFACTORS_LAUNCHER_PORTAL_URL = $apiPortalUrl
$env:SYNCFACTORS_LAUNCHER_MOCK_ADMIN_URL = if ($activeProfile -eq 'mock') { $mockAdminUrl } else { '' }
$env:SYNCFACTORS_LAUNCHER_MOCK_ODATA_URL = if ($activeProfile -eq 'mock') { $mockODataUrl } else { '' }

if ($Restart) {
    $preserveHostedTerminals = $false
    Restart-SelectedServices -RequestedService $Service -RepositoryRoot $repoRoot -PreserveHostedTerminals:$preserveHostedTerminals
}

switch ($Service) {
    'api' {
        $effectiveSkipBuild = $SkipBuild
        if ($activeProfile -eq 'mock' -and -not $SkipBuild) {
            Invoke-SolutionBuild -ProjectRoot $repoRoot
            $effectiveSkipBuild = $true
        }

        if ($activeProfile -eq 'mock') {
            Start-MockServiceHostedTerminal -ProfileName $Profile -RestartSelected:$Restart -SkipBuildStep:$effectiveSkipBuild
        }

        $apiBindHost = Format-UrlHost -HostName $env:SYNCFACTORS_API_BIND_HOST
        $arguments = @(
            './scripts/Start-SyncFactorsNextApi.ps1',
            '-ConfigPath', $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS,
            '-MappingConfigPath', $env:SYNCFACTORS_MAPPING_CONFIG_PATH_ABS,
            '-SqlitePath', $env:SYNCFACTORS_SQLITE_PATH_ABS,
            '-Urls', "https://$apiBindHost`:$($env:SYNCFACTORS_API_PORT)"
        )

        if ($effectiveSkipBuild) {
            $arguments += '-SkipBuild'
        }

        & pwsh @arguments
        exit $LASTEXITCODE
    }
    'ui' {
        $effectiveSkipBuild = $SkipBuild
        if ($activeProfile -eq 'mock' -and -not $SkipBuild) {
            Invoke-FrontendBuild -ProjectRoot $repoRoot
            Invoke-SolutionBuild -ProjectRoot $repoRoot
            $effectiveSkipBuild = $true
        }

        if ($activeProfile -eq 'mock') {
            Start-MockServiceHostedTerminal -ProfileName $Profile -RestartSelected:$Restart -SkipBuildStep:$effectiveSkipBuild
        }

        $apiBindHost = Format-UrlHost -HostName $env:SYNCFACTORS_API_BIND_HOST
        $arguments = @(
            './scripts/Start-SyncFactorsNextApi.ps1',
            '-ConfigPath', $env:SYNCFACTORS_RESOLVED_CONFIG_PATH_ABS,
            '-MappingConfigPath', $env:SYNCFACTORS_MAPPING_CONFIG_PATH_ABS,
            '-SqlitePath', $env:SYNCFACTORS_SQLITE_PATH_ABS,
            '-Urls', "https://$apiBindHost`:$($env:SYNCFACTORS_API_PORT)"
        )

        if ($effectiveSkipBuild) {
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
