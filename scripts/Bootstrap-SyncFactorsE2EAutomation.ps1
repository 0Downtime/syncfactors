[CmdletBinding()]
param(
    [string]$Username = 'syncfactors-automation',
    [string]$Password,
    [switch]$Admin,
    [switch]$ShowPassword,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

$projectRoot = Resolve-ProjectRoot
$envFile = Join-Path $projectRoot '.env.worktree'
$worktreeEnvScript = Join-Path $projectRoot 'scripts/codex/WorktreeEnv.ps1'
$automationProjectPath = Join-Path $projectRoot 'src/SyncFactors.Automation/SyncFactors.Automation.csproj'

. $worktreeEnvScript

if (-not (Test-Path $envFile)) {
    throw "Missing $envFile. Run the normal worktree bootstrap first."
}

function New-AutomationPassword {
    $bytes = [byte[]]::new(18)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', 'A').Replace('/', 'b')
    return "SfAuto1$token"
}

if ([string]::IsNullOrWhiteSpace($Password)) {
    $Password = New-AutomationPassword
}

if ($Password.Length -lt 12 -or
    -not ($Password.ToCharArray() | Where-Object { [char]::IsUpper($_) }) -or
    -not ($Password.ToCharArray() | Where-Object { [char]::IsLower($_) }) -or
    -not ($Password.ToCharArray() | Where-Object { [char]::IsDigit($_) })) {
    throw "Automation password must be at least 12 characters and include uppercase, lowercase, and numeric characters."
}

Set-SyncFactorsSecureStoreValue -RepoRoot $projectRoot -EnvFilePath $envFile -VariableName 'SYNCFACTORS_AUTOMATION_USERNAME' -Value $Username | Out-Null
Set-SyncFactorsSecureStoreValue -RepoRoot $projectRoot -EnvFilePath $envFile -VariableName 'SYNCFACTORS_AUTOMATION_PASSWORD' -Value $Password | Out-Null
Set-WorktreeEnvPlaceholder -Path $envFile -VariableName 'SYNCFACTORS_AUTOMATION_USERNAME'
Set-WorktreeEnvPlaceholder -Path $envFile -VariableName 'SYNCFACTORS_AUTOMATION_PASSWORD'

Set-WorktreeEnvValue -Path $envFile -VariableName 'SYNCFACTORS__AUTH__MODE' -Value 'hybrid'
Set-WorktreeEnvValue -Path $envFile -VariableName 'SYNCFACTORS__AUTH__LOCALBREAKGLASS__ENABLED' -Value 'true'

. (Join-Path $projectRoot 'scripts/codex/Load-WorktreeEnv.ps1')

Push-Location $projectRoot
try {
    Initialize-DotnetEnvironment -ProjectRoot $projectRoot
    if (-not $SkipBuild) {
        Invoke-SolutionBuild -ProjectRoot $projectRoot
    }

    $dotnetArgs = @('run')
    if ($SkipBuild) {
        $dotnetArgs += '--no-build'
    }
    else {
        $dotnetArgs += '--no-restore'
    }

    $dotnetArgs += @(
        '--project', $automationProjectPath,
        '--',
        'bootstrap-local-user',
        '--sqlite', $env:SYNCFACTORS_SQLITE_PATH_ABS,
        '--username', $Username,
        '--password', $Password
    )

    if ($Admin) {
        $dotnetArgs += '--admin'
    }

    dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Automation local user bootstrap failed."
    }
}
finally {
    Pop-Location
}

Write-Host "Automation credentials stored in the configured secure store." -ForegroundColor Green
Write-Host "Local break-glass auth enabled in .env.worktree for hybrid OIDC + automation login." -ForegroundColor Green
Write-Host "Username: $Username"
if ($ShowPassword) {
    Write-Host "Password: $Password"
}
else {
    Write-Host "Password: <stored in secure store>"
}
Write-Host "Restart the API/stack so hybrid auth settings are loaded." -ForegroundColor Yellow
