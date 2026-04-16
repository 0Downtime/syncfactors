[CmdletBinding()]
param(
    [switch]$RemoveEmptyValues,
    [switch]$PromptForMissingValues
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

function Read-SecretValue {
    param(
        [Parameter(Mandatory)]
        [string]$VariableName
    )

    $secureValue = Read-Host -Prompt "Value for $VariableName" -AsSecureString
    if ($null -eq $secureValue) {
        return ''
    }

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        if ($bstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
}

$values = Read-WorktreeEnvFile -Path $envFile
if ($values.Count -eq 0) {
    throw "No environment variables were found in $envFile."
}

$secretNames = Get-SyncFactorsSecureStoreVariableNames
$writtenCount = 0
$removedCount = 0
$skippedCount = 0

foreach ($name in $secretNames) {
    $hasEntry = $values.Contains($name)
    $value = if ($hasEntry) { [string]$values[$name] } else { $null }

    if ($RemoveEmptyValues -and $hasEntry -and [string]::IsNullOrWhiteSpace($value)) {
        Remove-SyncFactorsCredentialValue -RepoRoot $repoRoot -VariableName $name
        $removedCount += 1
        Write-Host "Removed $name from Windows Credential Manager"
        continue
    }

    if (-not $hasEntry -or [string]::IsNullOrWhiteSpace($value)) {
        if ($PromptForMissingValues) {
            $value = Read-SecretValue -VariableName $name
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                Set-SyncFactorsSecureStoreValue -RepoRoot $repoRoot -EnvFilePath $envFile -VariableName $name -Value $value | Out-Null
                $writtenCount += 1
                Write-Host "Stored $name in Windows Credential Manager"
                continue
            }
        }

        $skippedCount += 1
        Write-Host "Skipped $name because .env.worktree does not define a non-empty value"
        continue
    }

    Set-SyncFactorsSecureStoreValue -RepoRoot $repoRoot -EnvFilePath $envFile -VariableName $name -Value $value | Out-Null
    $writtenCount += 1
    Write-Host "Stored $name in Windows Credential Manager"
}

Write-Host "Credential import complete. Stored $writtenCount value(s); removed $removedCount empty value(s); skipped $skippedCount missing or blank value(s)."
