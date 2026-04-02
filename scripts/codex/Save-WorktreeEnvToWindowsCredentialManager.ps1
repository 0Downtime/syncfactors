[CmdletBinding()]
param(
    [switch]$RemoveEmptyValues
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not [OperatingSystem]::IsWindows()) {
    throw 'This script only runs on Windows because it writes to Windows Credential Manager.'
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '../..')).ProviderPath
$envFile = Join-Path $repoRoot '.env.worktree'

if (-not (Test-Path $envFile)) {
    throw "Missing $envFile. Run pwsh ./scripts/codex/setup-worktree.ps1 first, or copy ./.env.worktree.example to ./.env.worktree."
}

. (Join-Path $scriptDir 'WorktreeEnv.ps1')

$values = Read-WorktreeEnvFile -Path $envFile
if ($values.Count -eq 0) {
    throw "No environment variables were found in $envFile."
}

$writtenCount = 0
$removedCount = 0

foreach ($entry in $values.GetEnumerator()) {
    $name = [string]$entry.Key
    $value = [string]$entry.Value

    if ($RemoveEmptyValues -and [string]::IsNullOrEmpty($value)) {
        Remove-SyncFactorsCredentialValue -RepoRoot $repoRoot -VariableName $name
        $removedCount += 1
        Write-Host "Removed $name from Windows Credential Manager"
        continue
    }

    Set-SyncFactorsCredentialValue -RepoRoot $repoRoot -VariableName $name -Value $value
    $writtenCount += 1
    Write-Host "Stored $name in Windows Credential Manager"
}

Write-Host "Credential import complete. Stored $writtenCount value(s); removed $removedCount empty value(s)."
