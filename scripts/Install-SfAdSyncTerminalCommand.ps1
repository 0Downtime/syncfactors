[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [string]$InstallDirectory,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$CommandName = 'synctui',
    [string]$ProjectRoot,
    [string]$ShellProfilePath,
    [switch]$Uninstall,
    [switch]$RemovePathUpdate,
    [switch]$SkipPathUpdate,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-SfAdInstallerProjectRoot {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $Path = Split-Path -Path $PSScriptRoot -Parent
    }

    return (Resolve-Path -Path $Path).ProviderPath
}

function Resolve-SfAdOptionalLocalConfigPath {
    param(
        [Parameter(Mandatory)]
        [string]$ConfigDirectory,
        [Parameter(Mandatory)]
        [string]$Filter,
        [Parameter(Mandatory)]
        [string]$Description
    )

    $candidates = @(
        Get-ChildItem -Path $ConfigDirectory -Filter $Filter -File -ErrorAction SilentlyContinue |
            Sort-Object -Property Name
    )

    if ($candidates.Count -eq 0) {
        return $null
    }

    if ($candidates.Count -gt 1) {
        throw "Multiple local $Description files were found under '$ConfigDirectory'. Pass the path explicitly."
    }

    return $candidates[0].FullName
}

function Resolve-SfAdRequiredConfigPath {
    param(
        [AllowNull()]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$ConfigDirectory
    )

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        return (Resolve-Path -Path $Path).ProviderPath
    }

    $resolvedPath = Resolve-SfAdOptionalLocalConfigPath -ConfigDirectory $ConfigDirectory -Filter 'local*.sync-config.json' -Description 'sync config'
    if ($null -ne $resolvedPath) {
        return $resolvedPath
    }

    throw "ConfigPath is required. No local sync config file matching 'local*.sync-config.json' was found under '$ConfigDirectory'."
}

function Resolve-SfAdOptionalMappingConfigPath {
    param(
        [AllowNull()]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$ConfigDirectory
    )

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        return (Resolve-Path -Path $Path).ProviderPath
    }

    return Resolve-SfAdOptionalLocalConfigPath -ConfigDirectory $ConfigDirectory -Filter 'local*.mapping-config.json' -Description 'mapping config'
}

function Get-SfAdDefaultInstallDirectory {
    if ($IsWindows) {
        $windowsAppsDirectory = Join-Path -Path (Join-Path -Path (Join-Path -Path $HOME -ChildPath 'AppData') -ChildPath 'Local') -ChildPath 'Microsoft/WindowsApps'
        if (Test-Path -Path $windowsAppsDirectory -PathType Container) {
            return $windowsAppsDirectory
        }
    }

    return Join-Path -Path $HOME -ChildPath '.local/bin'
}

function Test-SfAdPathContainsDirectory {
    param(
        [AllowNull()]
        [string]$PathValue,
        [Parameter(Mandatory)]
        [string]$Directory
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $false
    }

    $normalizedDirectory = [System.IO.Path]::GetFullPath($Directory).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)

    foreach ($entry in ($PathValue -split [System.IO.Path]::PathSeparator)) {
        if ([string]::IsNullOrWhiteSpace($entry)) {
            continue
        }

        try {
            $normalizedEntry = [System.IO.Path]::GetFullPath($entry).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        } catch {
            continue
        }

        if ($normalizedEntry -ieq $normalizedDirectory) {
            return $true
        }
    }

    return $false
}

function Remove-SfAdPathDirectory {
    param(
        [AllowNull()]
        [string]$PathValue,
        [Parameter(Mandatory)]
        [string]$Directory
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return ''
    }

    $normalizedDirectory = [System.IO.Path]::GetFullPath($Directory).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $entries = @()
    foreach ($entry in ($PathValue -split [System.IO.Path]::PathSeparator)) {
        if ([string]::IsNullOrWhiteSpace($entry)) {
            continue
        }

        $normalizedEntry = $null
        try {
            $normalizedEntry = [System.IO.Path]::GetFullPath($entry).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        } catch {
            $normalizedEntry = $null
        }

        if ($null -ne $normalizedEntry -and $normalizedEntry -ieq $normalizedDirectory) {
            continue
        }

        $entries += $entry
    }

    return [string]::Join([System.IO.Path]::PathSeparator, $entries)
}

function Get-SfAdInstallMetadataPath {
    param(
        [Parameter(Mandatory)]
        [string]$ResolvedInstallDirectory,
        [Parameter(Mandatory)]
        [string]$CommandName
    )

    return Join-Path -Path $ResolvedInstallDirectory -ChildPath ".$CommandName.install.json"
}

function Get-SfAdConfigHelperCommandName {
    param(
        [Parameter(Mandatory)]
        [string]$CommandName
    )

    return "$CommandName-config"
}

function Read-SfAdInstallMetadata {
    param(
        [Parameter(Mandatory)]
        [string]$MetadataPath
    )

    if (-not (Test-Path -Path $MetadataPath -PathType Leaf)) {
        return $null
    }

    return Get-Content -Path $MetadataPath -Raw | ConvertFrom-Json -Depth 20
}

function Save-SfAdInstallMetadata {
    param(
        [Parameter(Mandatory)]
        [string]$MetadataPath,
        [Parameter(Mandatory)]
        [pscustomobject]$Metadata
    )

    $metadataDirectory = Split-Path -Path $MetadataPath -Parent
    if ($metadataDirectory -and -not (Test-Path -Path $metadataDirectory -PathType Container)) {
        New-Item -Path $metadataDirectory -ItemType Directory -Force | Out-Null
    }

    $Metadata | ConvertTo-Json -Depth 10 | Set-Content -Path $MetadataPath
}

function ConvertTo-SfAdPowerShellLiteral {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return '$null'
    }

    return "'$($Value.Replace("'", "''"))'"
}

function ConvertTo-SfAdDoubleQuotedShellValue {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return ''
    }

    return $Value.Replace('"', '\"').Replace('$', '\$').Replace('`', '\`')
}

function Get-SfAdPowerShellShimContent {
    param(
        [Parameter(Mandatory)]
        [string]$DashboardPath,
        [Parameter(Mandatory)]
        [string]$ResolvedConfigPath,
        [AllowNull()]
        [string]$ResolvedMappingConfigPath
    )

    $resolvedMappingConfigLiteral = ConvertTo-SfAdPowerShellLiteral -Value $ResolvedMappingConfigPath

    return @"
[CmdletBinding(PositionalBinding = `$false)]
param(
    [string]`$ConfigPath,
    [string]`$MappingConfigPath,
    [ValidateRange(1, 3600)]
    [int]`$RefreshIntervalSeconds,
    [ValidateRange(1, 1000)]
    [int]`$HistoryLimit,
    [switch]`$PauseAutoRefresh,
    [switch]`$RunOnce,
    [switch]`$AsText
)

Set-StrictMode -Version Latest
`$ErrorActionPreference = 'Stop'

`$effectiveConfigPath = if (`$PSBoundParameters.ContainsKey('ConfigPath')) {
    `$ConfigPath
} else {
    $(ConvertTo-SfAdPowerShellLiteral -Value $ResolvedConfigPath)
}

`$effectiveMappingConfigPath = if (`$PSBoundParameters.ContainsKey('MappingConfigPath')) {
    `$MappingConfigPath
} else {
    $resolvedMappingConfigLiteral
}

`$namedArguments = @{
    ConfigPath = `$effectiveConfigPath
}

if (-not [string]::IsNullOrWhiteSpace("`$effectiveMappingConfigPath")) {
    `$namedArguments['MappingConfigPath'] = `$effectiveMappingConfigPath
}

if (`$PSBoundParameters.ContainsKey('RefreshIntervalSeconds')) {
    `$namedArguments['RefreshIntervalSeconds'] = `$RefreshIntervalSeconds
}

if (`$PSBoundParameters.ContainsKey('HistoryLimit')) {
    `$namedArguments['HistoryLimit'] = `$HistoryLimit
}

if (`$PauseAutoRefresh) {
    `$namedArguments['PauseAutoRefresh'] = `$true
}

if (`$RunOnce) {
    `$namedArguments['RunOnce'] = `$true
}

if (`$AsText) {
    `$namedArguments['AsText'] = `$true
}

& $(ConvertTo-SfAdPowerShellLiteral -Value $DashboardPath) @namedArguments
if (Get-Variable -Name LASTEXITCODE -ErrorAction SilentlyContinue) {
    exit `$LASTEXITCODE
}
"@
}

function Get-SfAdCmdShimContent {
    param(
        [Parameter(Mandatory)]
        [string]$DashboardPath,
        [Parameter(Mandatory)]
        [string]$ResolvedConfigPath,
        [AllowNull()]
        [string]$ResolvedMappingConfigPath
    )

    $mappingArguments = if ([string]::IsNullOrWhiteSpace($ResolvedMappingConfigPath)) {
        ''
    } else {
        " -MappingConfigPath ""$ResolvedMappingConfigPath"""
    }

    return @"
@echo off
setlocal
where pwsh >nul 2>nul
if not errorlevel 1 (
    set "_SFAD_SYNC_PWSH=pwsh"
) else (
    set "_SFAD_SYNC_PWSH=powershell"
)
"%_SFAD_SYNC_PWSH%" -NoLogo -NoProfile -File "$DashboardPath" -ConfigPath "$ResolvedConfigPath"$mappingArguments %*
exit /b %errorlevel%
"@
}

function Get-SfAdShellShimContent {
    param(
        [Parameter(Mandatory)]
        [string]$DashboardPath,
        [Parameter(Mandatory)]
        [string]$ResolvedConfigPath,
        [AllowNull()]
        [string]$ResolvedMappingConfigPath
    )

    $dashboardValue = ConvertTo-SfAdDoubleQuotedShellValue -Value $DashboardPath
    $configValue = ConvertTo-SfAdDoubleQuotedShellValue -Value $ResolvedConfigPath
    $mappingArgument = if ([string]::IsNullOrWhiteSpace($ResolvedMappingConfigPath)) {
        ''
    } else {
        " -MappingConfigPath ""$(ConvertTo-SfAdDoubleQuotedShellValue -Value $ResolvedMappingConfigPath)"""
    }

    return @"
#!/usr/bin/env sh
set -eu

if command -v pwsh >/dev/null 2>&1; then
    exec pwsh -NoLogo -NoProfile -File "$dashboardValue" -ConfigPath "$configValue"$mappingArgument "`$@"
fi

printf '%s\n' 'pwsh was not found in PATH.' >&2
exit 1
"@
}

function Get-SfAdConfigHelperPowerShellShimContent {
    param(
        [Parameter(Mandatory)]
        [string]$HelperScriptPath,
        [Parameter(Mandatory)]
        [string]$ResolvedInstallDirectory,
        [Parameter(Mandatory)]
        [string]$CommandName
    )

    return @"
[CmdletBinding(PositionalBinding = `$false)]
param(
    [string]`$ConfigPath,
    [string]`$MappingConfigPath,
    [switch]`$ShowCurrent
)

Set-StrictMode -Version Latest
`$ErrorActionPreference = 'Stop'

`$namedArguments = @{
    InstallDirectory = $(ConvertTo-SfAdPowerShellLiteral -Value $ResolvedInstallDirectory)
    CommandName = $(ConvertTo-SfAdPowerShellLiteral -Value $CommandName)
}

if (`$PSBoundParameters.ContainsKey('ConfigPath')) {
    `$namedArguments['ConfigPath'] = `$ConfigPath
}

if (`$PSBoundParameters.ContainsKey('MappingConfigPath')) {
    `$namedArguments['MappingConfigPath'] = `$MappingConfigPath
}

if (`$ShowCurrent) {
    `$namedArguments['ShowCurrent'] = `$true
}

& $(ConvertTo-SfAdPowerShellLiteral -Value $HelperScriptPath) @namedArguments
if (Get-Variable -Name LASTEXITCODE -ErrorAction SilentlyContinue) {
    exit `$LASTEXITCODE
}
"@
}

function Get-SfAdConfigHelperCmdShimContent {
    param(
        [Parameter(Mandatory)]
        [string]$HelperScriptPath,
        [Parameter(Mandatory)]
        [string]$ResolvedInstallDirectory,
        [Parameter(Mandatory)]
        [string]$CommandName
    )

    return @"
@echo off
setlocal
where pwsh >nul 2>nul
if not errorlevel 1 (
    set "_SFAD_SYNC_PWSH=pwsh"
) else (
    set "_SFAD_SYNC_PWSH=powershell"
)
"%_SFAD_SYNC_PWSH%" -NoLogo -NoProfile -File "$HelperScriptPath" -InstallDirectory "$ResolvedInstallDirectory" -CommandName "$CommandName" %*
exit /b %errorlevel%
"@
}

function Get-SfAdConfigHelperShellShimContent {
    param(
        [Parameter(Mandatory)]
        [string]$HelperScriptPath,
        [Parameter(Mandatory)]
        [string]$ResolvedInstallDirectory,
        [Parameter(Mandatory)]
        [string]$CommandName
    )

    $helperValue = ConvertTo-SfAdDoubleQuotedShellValue -Value $HelperScriptPath
    $installDirectoryValue = ConvertTo-SfAdDoubleQuotedShellValue -Value $ResolvedInstallDirectory
    $commandNameValue = ConvertTo-SfAdDoubleQuotedShellValue -Value $CommandName

    return @"
#!/usr/bin/env sh
set -eu

if command -v pwsh >/dev/null 2>&1; then
    exec pwsh -NoLogo -NoProfile -File "$helperValue" -InstallDirectory "$installDirectoryValue" -CommandName "$commandNameValue" "`$@"
fi

printf '%s\n' 'pwsh was not found in PATH.' >&2
exit 1
"@
}

function Get-SfAdShellProfilePath {
    param([string]$OverridePath)

    if (-not [string]::IsNullOrWhiteSpace($OverridePath)) {
        return [System.IO.Path]::GetFullPath($OverridePath)
    }

    $shellName = [System.IO.Path]::GetFileName("$env:SHELL")
    switch -Regex ($shellName) {
        '^zsh$' { return Join-Path -Path $HOME -ChildPath '.zprofile' }
        '^bash$' { return Join-Path -Path $HOME -ChildPath '.bash_profile' }
        default { return Join-Path -Path $HOME -ChildPath '.profile' }
    }
}

function Add-SfAdInstallDirectoryToPath {
    param(
        [Parameter(Mandatory)]
        [string]$ResolvedInstallDirectory,
        [string]$ShellProfilePathOverride
    )

    if (Test-SfAdPathContainsDirectory -PathValue $env:PATH -Directory $ResolvedInstallDirectory) {
        return [pscustomobject]@{
            updated = $false
            currentSessionUpdated = $false
            shellProfilePath = $null
            mode = 'None'
            message = 'Install directory is already available on PATH.'
        }
    }

    $pathSeparator = [System.IO.Path]::PathSeparator
    $env:PATH = "$ResolvedInstallDirectory$pathSeparator$env:PATH"

    if (-not [string]::IsNullOrWhiteSpace($ShellProfilePathOverride)) {
        $profilePath = Get-SfAdShellProfilePath -OverridePath $ShellProfilePathOverride
        $profileDirectory = Split-Path -Path $profilePath -Parent
        if ($profileDirectory -and -not (Test-Path -Path $profileDirectory -PathType Container)) {
            New-Item -Path $profileDirectory -ItemType Directory -Force | Out-Null
        }

        $escapedDirectory = ConvertTo-SfAdDoubleQuotedShellValue -Value $ResolvedInstallDirectory
        $exportLine = "export PATH=""${escapedDirectory}:`$PATH"""

        if (Test-Path -Path $profilePath -PathType Leaf) {
            $profileContent = Get-Content -Path $profilePath -Raw
            if ($profileContent -notmatch [regex]::Escape($exportLine)) {
                if ($profileContent.Length -gt 0 -and -not $profileContent.EndsWith("`n")) {
                    Add-Content -Path $profilePath -Value ''
                }

                Add-Content -Path $profilePath -Value $exportLine
            }
        } else {
            Set-Content -Path $profilePath -Value $exportLine
        }

        return [pscustomobject]@{
            updated = $true
            currentSessionUpdated = $true
            shellProfilePath = $profilePath
            mode = 'ShellProfile'
            message = "Added install directory to PATH and persisted it in '$profilePath'."
        }
    }

    if ($IsWindows) {
        $userPath = [System.Environment]::GetEnvironmentVariable('Path', 'User')
        if (-not (Test-SfAdPathContainsDirectory -PathValue $userPath -Directory $ResolvedInstallDirectory)) {
            if ([string]::IsNullOrWhiteSpace($userPath)) {
                $userPath = $ResolvedInstallDirectory
            } else {
                $userPath = "$ResolvedInstallDirectory$pathSeparator$userPath"
            }

            [System.Environment]::SetEnvironmentVariable('Path', $userPath, 'User')
        }

        return [pscustomobject]@{
            updated = $true
            currentSessionUpdated = $true
            shellProfilePath = $null
            mode = 'UserPath'
            message = 'Added install directory to the Windows user PATH.'
        }
    }

    $profilePath = Get-SfAdShellProfilePath -OverridePath $ShellProfilePathOverride
    $profileDirectory = Split-Path -Path $profilePath -Parent
    if ($profileDirectory -and -not (Test-Path -Path $profileDirectory -PathType Container)) {
        New-Item -Path $profileDirectory -ItemType Directory -Force | Out-Null
    }

    $escapedDirectory = ConvertTo-SfAdDoubleQuotedShellValue -Value $ResolvedInstallDirectory
    $exportLine = "export PATH=""${escapedDirectory}:`$PATH"""

    if (Test-Path -Path $profilePath -PathType Leaf) {
        $profileContent = Get-Content -Path $profilePath -Raw
        if ($profileContent -notmatch [regex]::Escape($exportLine)) {
            if ($profileContent.Length -gt 0 -and -not $profileContent.EndsWith("`n")) {
                Add-Content -Path $profilePath -Value ''
            }

            Add-Content -Path $profilePath -Value $exportLine
        }
    } else {
        Set-Content -Path $profilePath -Value $exportLine
    }

    return [pscustomobject]@{
        updated = $true
        currentSessionUpdated = $true
        shellProfilePath = $profilePath
        mode = 'ShellProfile'
        message = "Added install directory to PATH and persisted it in '$profilePath'."
    }
}

function Remove-SfAdInstallDirectoryFromPath {
    param(
        [Parameter(Mandatory)]
        [string]$ResolvedInstallDirectory,
        [AllowNull()]
        [pscustomobject]$Metadata,
        [string]$ShellProfilePathOverride
    )

    if ($null -eq $Metadata -or -not $Metadata.pathUpdated) {
        return [pscustomobject]@{
            updated = $false
            currentSessionUpdated = $false
            shellProfilePath = $null
            mode = 'None'
            message = 'PATH cleanup skipped because the installer metadata did not record a PATH update.'
        }
    }

    $currentSessionUpdated = $false
    if (Test-SfAdPathContainsDirectory -PathValue $env:PATH -Directory $ResolvedInstallDirectory) {
        $env:PATH = Remove-SfAdPathDirectory -PathValue $env:PATH -Directory $ResolvedInstallDirectory
        $currentSessionUpdated = $true
    }

    $mode = "$($Metadata.pathUpdateMode)"
    switch ($mode) {
        'UserPath' {
            $userPath = [System.Environment]::GetEnvironmentVariable('Path', 'User')
            $newUserPath = Remove-SfAdPathDirectory -PathValue $userPath -Directory $ResolvedInstallDirectory
            if ($newUserPath -ne $userPath) {
                [System.Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
            }

            return [pscustomobject]@{
                updated = $true
                currentSessionUpdated = $currentSessionUpdated
                shellProfilePath = $null
                mode = 'UserPath'
                message = 'Removed install directory from the Windows user PATH.'
            }
        }
        'ShellProfile' {
            $profilePath = if (-not [string]::IsNullOrWhiteSpace($ShellProfilePathOverride)) {
                [System.IO.Path]::GetFullPath($ShellProfilePathOverride)
            } elseif (-not [string]::IsNullOrWhiteSpace("$($Metadata.shellProfilePath)")) {
                [System.IO.Path]::GetFullPath("$($Metadata.shellProfilePath)")
            } else {
                Get-SfAdShellProfilePath
            }

            $escapedDirectory = ConvertTo-SfAdDoubleQuotedShellValue -Value $ResolvedInstallDirectory
            $exportLine = "export PATH=""${escapedDirectory}:`$PATH"""
            if (Test-Path -Path $profilePath -PathType Leaf) {
                $remainingLines = @(
                    Get-Content -Path $profilePath |
                        Where-Object { $_ -ne $exportLine }
                )
                Set-Content -Path $profilePath -Value $remainingLines
            }

            return [pscustomobject]@{
                updated = $true
                currentSessionUpdated = $currentSessionUpdated
                shellProfilePath = $profilePath
                mode = 'ShellProfile'
                message = "Removed the installer PATH line from '$profilePath'."
            }
        }
        default {
            return [pscustomobject]@{
                updated = $false
                currentSessionUpdated = $currentSessionUpdated
                shellProfilePath = $null
                mode = 'None'
                message = 'PATH cleanup skipped because the installer metadata did not record a removable PATH update mode.'
            }
        }
    }
}

$resolvedInstallDirectory = if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    [System.IO.Path]::GetFullPath((Get-SfAdDefaultInstallDirectory))
} else {
    [System.IO.Path]::GetFullPath($InstallDirectory)
}
$configHelperCommandName = Get-SfAdConfigHelperCommandName -CommandName $CommandName
$shellCommandPath = Join-Path -Path $resolvedInstallDirectory -ChildPath $CommandName
$cmdCommandPath = Join-Path -Path $resolvedInstallDirectory -ChildPath "$CommandName.cmd"
$ps1CommandPath = Join-Path -Path $resolvedInstallDirectory -ChildPath "$CommandName.ps1"
$helperShellCommandPath = Join-Path -Path $resolvedInstallDirectory -ChildPath $configHelperCommandName
$helperCmdCommandPath = Join-Path -Path $resolvedInstallDirectory -ChildPath "$configHelperCommandName.cmd"
$helperPs1CommandPath = Join-Path -Path $resolvedInstallDirectory -ChildPath "$configHelperCommandName.ps1"
$metadataPath = Get-SfAdInstallMetadataPath -ResolvedInstallDirectory $resolvedInstallDirectory -CommandName $CommandName
$existingMetadata = Read-SfAdInstallMetadata -MetadataPath $metadataPath

if ($Uninstall) {
    $removedPaths = @()

    if ($PSCmdlet.ShouldProcess($resolvedInstallDirectory, "Uninstall terminal command '$CommandName'")) {
        foreach ($path in @($shellCommandPath, $cmdCommandPath, $ps1CommandPath, $helperShellCommandPath, $helperCmdCommandPath, $helperPs1CommandPath, $metadataPath)) {
            if (Test-Path -Path $path) {
                Remove-Item -Path $path -Force
                $removedPaths += $path
            }
        }
    }

    $pathRemoval = [pscustomobject]@{
        updated = $false
        currentSessionUpdated = $false
        shellProfilePath = $null
        mode = 'None'
        message = 'PATH cleanup skipped.'
    }

    if ($RemovePathUpdate -and $PSCmdlet.ShouldProcess($resolvedInstallDirectory, "Remove PATH registration for '$CommandName'")) {
        $pathRemoval = Remove-SfAdInstallDirectoryFromPath -ResolvedInstallDirectory $resolvedInstallDirectory -Metadata $existingMetadata -ShellProfilePathOverride $ShellProfilePath
    }

    return [pscustomobject]@{
        commandName = $CommandName
        installDirectory = $resolvedInstallDirectory
        shellCommandPath = $shellCommandPath
        cmdCommandPath = $cmdCommandPath
        ps1CommandPath = $ps1CommandPath
        metadataPath = $metadataPath
        removedPaths = $removedPaths
        removed = ($removedPaths.Count -gt 0)
        pathUpdated = [bool]$pathRemoval.updated
        currentSessionPathUpdated = [bool]$pathRemoval.currentSessionUpdated
        shellProfilePath = $pathRemoval.shellProfilePath
        pathUpdateMessage = $pathRemoval.message
    }
}

$resolvedProjectRoot = Resolve-SfAdInstallerProjectRoot -Path $ProjectRoot
$configDirectory = Join-Path -Path $resolvedProjectRoot -ChildPath 'config'
$dashboardPath = Join-Path -Path $resolvedProjectRoot -ChildPath 'scripts/Watch-SfAdSyncMonitor.ps1'
$configHelperScriptPath = Join-Path -Path $resolvedProjectRoot -ChildPath 'scripts/Set-SfAdSyncTerminalCommandConfig.ps1'

if (-not (Test-Path -Path $dashboardPath -PathType Leaf)) {
    throw "Dashboard script was not found at '$dashboardPath'."
}

if (-not (Test-Path -Path $configHelperScriptPath -PathType Leaf)) {
    throw "Config helper script was not found at '$configHelperScriptPath'."
}

$resolvedConfigPath = Resolve-SfAdRequiredConfigPath -Path $ConfigPath -ConfigDirectory $configDirectory
$resolvedMappingConfigPath = Resolve-SfAdOptionalMappingConfigPath -Path $MappingConfigPath -ConfigDirectory $configDirectory

foreach ($path in @($shellCommandPath, $cmdCommandPath, $ps1CommandPath, $helperShellCommandPath, $helperCmdCommandPath, $helperPs1CommandPath, $metadataPath)) {
    if ((Test-Path -Path $path) -and -not $Force) {
        throw "The terminal command path '$path' already exists. Re-run with -Force to overwrite it."
    }
}

if (-not (Test-Path -Path $resolvedInstallDirectory -PathType Container)) {
    New-Item -Path $resolvedInstallDirectory -ItemType Directory -Force | Out-Null
}

if ($PSCmdlet.ShouldProcess($resolvedInstallDirectory, "Install terminal command '$CommandName'")) {
    Set-Content -Path $shellCommandPath -Value (Get-SfAdShellShimContent -DashboardPath $dashboardPath -ResolvedConfigPath $resolvedConfigPath -ResolvedMappingConfigPath $resolvedMappingConfigPath)
    Set-Content -Path $cmdCommandPath -Value (Get-SfAdCmdShimContent -DashboardPath $dashboardPath -ResolvedConfigPath $resolvedConfigPath -ResolvedMappingConfigPath $resolvedMappingConfigPath)
    Set-Content -Path $ps1CommandPath -Value (Get-SfAdPowerShellShimContent -DashboardPath $dashboardPath -ResolvedConfigPath $resolvedConfigPath -ResolvedMappingConfigPath $resolvedMappingConfigPath)
    Set-Content -Path $helperShellCommandPath -Value (Get-SfAdConfigHelperShellShimContent -HelperScriptPath $configHelperScriptPath -ResolvedInstallDirectory $resolvedInstallDirectory -CommandName $CommandName)
    Set-Content -Path $helperCmdCommandPath -Value (Get-SfAdConfigHelperCmdShimContent -HelperScriptPath $configHelperScriptPath -ResolvedInstallDirectory $resolvedInstallDirectory -CommandName $CommandName)
    Set-Content -Path $helperPs1CommandPath -Value (Get-SfAdConfigHelperPowerShellShimContent -HelperScriptPath $configHelperScriptPath -ResolvedInstallDirectory $resolvedInstallDirectory -CommandName $CommandName)

    if (-not $IsWindows) {
        $chmodPath = if (Test-Path -Path '/bin/chmod' -PathType Leaf) {
            '/bin/chmod'
        } else {
            $chmodCommand = Get-Command chmod -CommandType Application -ErrorAction SilentlyContinue
            if ($null -eq $chmodCommand) {
                throw 'chmod was not found. The shell shim could not be marked executable.'
            }

            $chmodCommand.Source
        }

        & $chmodPath '+x' $shellCommandPath $helperShellCommandPath
    }
}

$pathUpdate = [pscustomobject]@{
    updated = $false
    currentSessionUpdated = $false
    shellProfilePath = $null
    mode = 'None'
    message = 'PATH update skipped.'
}

if (-not $SkipPathUpdate -and $PSCmdlet.ShouldProcess($resolvedInstallDirectory, "Register '$resolvedInstallDirectory' on PATH")) {
    $pathUpdate = Add-SfAdInstallDirectoryToPath -ResolvedInstallDirectory $resolvedInstallDirectory -ShellProfilePathOverride $ShellProfilePath
}

$metadata = [pscustomobject]@{
    commandName = $CommandName
    projectRoot = $resolvedProjectRoot
    installDirectory = $resolvedInstallDirectory
    configPath = $resolvedConfigPath
    mappingConfigPath = $resolvedMappingConfigPath
    dashboardPath = $dashboardPath
    shellCommandPath = $shellCommandPath
    cmdCommandPath = $cmdCommandPath
    ps1CommandPath = $ps1CommandPath
    configCommandName = $configHelperCommandName
    helperShellCommandPath = $helperShellCommandPath
    helperCmdCommandPath = $helperCmdCommandPath
    helperPs1CommandPath = $helperPs1CommandPath
    shellProfilePath = $pathUpdate.shellProfilePath
    pathUpdated = [bool]$pathUpdate.updated
    pathUpdateMode = $pathUpdate.mode
    installedAt = (Get-Date).ToString('o')
}

Save-SfAdInstallMetadata -MetadataPath $metadataPath -Metadata $metadata

[pscustomobject]@{
    commandName = $CommandName
    projectRoot = $resolvedProjectRoot
    installDirectory = $resolvedInstallDirectory
    configPath = $resolvedConfigPath
    mappingConfigPath = $resolvedMappingConfigPath
    dashboardPath = $dashboardPath
    shellCommandPath = $shellCommandPath
    cmdCommandPath = $cmdCommandPath
    ps1CommandPath = $ps1CommandPath
    configCommandName = $configHelperCommandName
    helperShellCommandPath = $helperShellCommandPath
    helperCmdCommandPath = $helperCmdCommandPath
    helperPs1CommandPath = $helperPs1CommandPath
    metadataPath = $metadataPath
    pathUpdated = [bool]$pathUpdate.updated
    currentSessionPathUpdated = [bool]$pathUpdate.currentSessionUpdated
    shellProfilePath = $pathUpdate.shellProfilePath
    pathUpdateMessage = $pathUpdate.message
}
