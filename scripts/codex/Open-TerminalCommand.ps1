[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Label,
    [Parameter(Mandatory)]
    [string]$Command,
    [string[]]$Arguments = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '../..')).ProviderPath

if ([OperatingSystem]::IsMacOS()) {
    & (Join-Path $scriptDir 'open-terminal-command.sh') $Label $Command @Arguments
    exit $LASTEXITCODE
}

if ([OperatingSystem]::IsWindows()) {
    $argumentList = @()

    if ($Command.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase)) {
        $argumentList += @('-NoExit', '-File', $Command)
        $argumentList += $Arguments
    }
    else {
        $argumentList += @('-NoExit', '-Command', "& $Command $($Arguments -join ' ')")
    }

    Start-Process -FilePath 'pwsh' -ArgumentList $argumentList -WorkingDirectory $repoRoot | Out-Null
    exit 0
}

throw "Opening a separate terminal is only implemented for macOS and Windows."
