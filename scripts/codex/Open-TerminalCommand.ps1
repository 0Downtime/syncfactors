[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Label,
    [Parameter(Mandatory)]
    [string]$Command,
    [string[]]$Arguments = @(),
    [switch]$ReuseIfExists
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '../..')).ProviderPath

function ConvertTo-PowerShellLiteral {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    return "'" + $Value.Replace("'", "''") + "'"
}

function ConvertTo-PowerShellCommandArgument {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    if ($Value -match '^-[A-Za-z][A-Za-z0-9]*(?::\$(?:true|false))?$') {
        return $Value
    }

    return ConvertTo-PowerShellLiteral -Value $Value
}

if ([OperatingSystem]::IsMacOS()) {
    $terminalArguments = @()
    if ($ReuseIfExists) {
        $terminalArguments += '--reuse-existing'
    }

    if ($Command.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase)) {
        & (Join-Path $scriptDir 'open-terminal-command.sh') @terminalArguments $Label 'pwsh' '-File' $Command @Arguments
    }
    else {
        & (Join-Path $scriptDir 'open-terminal-command.sh') @terminalArguments $Label $Command @Arguments
    }

    exit $LASTEXITCODE
}

if ([OperatingSystem]::IsWindows()) {
    $commandInvocationParts = @('&', (ConvertTo-PowerShellLiteral -Value $Command))
    foreach ($argument in $Arguments) {
        $commandInvocationParts += ConvertTo-PowerShellCommandArgument -Value $argument
    }

    $wrappedCommandLines = [System.Collections.Generic.List[string]]::new()
    $wrappedCommandLines.Add('$ErrorActionPreference = ' + (ConvertTo-PowerShellLiteral -Value 'Stop'))
    $wrappedCommandLines.Add('Set-Location -LiteralPath ' + (ConvertTo-PowerShellLiteral -Value $repoRoot))
    $wrappedCommandLines.Add('$Host.UI.RawUI.WindowTitle = ' + (ConvertTo-PowerShellLiteral -Value $Label))
    $wrappedCommandLines.Add('Write-Host (' + (ConvertTo-PowerShellLiteral -Value ("Starting {0}" -f $Label)) + ')')
    $wrappedCommandLines.Add('try {')
    $wrappedCommandLines.Add('    ' + ($commandInvocationParts -join ' '))
    $wrappedCommandLines.Add('    $exitCode = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }')
    $wrappedCommandLines.Add('}')
    $wrappedCommandLines.Add('catch {')
    $wrappedCommandLines.Add('    Write-Error $_')
    $wrappedCommandLines.Add('    $exitCode = 1')
    $wrappedCommandLines.Add('}')
    $wrappedCommandLines.Add('Write-Host (' + (ConvertTo-PowerShellLiteral -Value ("`n[{0}] exited with status " -f $Label)) + ' + $exitCode + ''.'')')
    $wrappedCommandLines.Add('exit $exitCode')
    $wrappedCommand = $wrappedCommandLines -join [Environment]::NewLine

    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($wrappedCommand))

    Start-Process -FilePath 'pwsh' -ArgumentList @('-NoLogo', '-EncodedCommand', $encodedCommand) -WorkingDirectory $repoRoot | Out-Null
    exit 0
}

throw "Opening a separate terminal is only implemented for macOS and Windows."
