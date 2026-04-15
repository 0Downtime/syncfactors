[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('list-secure-store-variable-names', 'assert-secure-store-variable-name', 'set-worktree-env-placeholder', 'resolve-keychain-service-name')]
    [string]$Action,
    [string]$EnvFilePath,
    [string]$VariableName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptDir 'WorktreeEnv.ps1')

switch ($Action) {
    'list-secure-store-variable-names' {
        foreach ($name in Get-SyncFactorsSecureStoreVariableNames) {
            Write-Output $name
        }
    }
    'assert-secure-store-variable-name' {
        Assert-SyncFactorsSecureStoreVariableName -VariableName $VariableName
    }
    'set-worktree-env-placeholder' {
        Set-WorktreeEnvPlaceholder -Path $EnvFilePath -VariableName $VariableName
    }
    'resolve-keychain-service-name' {
        Write-Output (Resolve-SyncFactorsKeychainServiceName -EnvFilePath $EnvFilePath)
    }
}
