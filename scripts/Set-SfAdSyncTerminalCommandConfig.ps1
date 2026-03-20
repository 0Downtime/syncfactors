[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$MappingConfigPath,
    [string]$InstallDirectory,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$CommandName = 'synctui',
    [switch]$ShowCurrent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SfAdDefaultInstallDirectory {
    if ($IsWindows) {
        $windowsAppsDirectory = Join-Path -Path (Join-Path -Path (Join-Path -Path $HOME -ChildPath 'AppData') -ChildPath 'Local') -ChildPath 'Microsoft/WindowsApps'
        if (Test-Path -Path $windowsAppsDirectory -PathType Container) {
            return $windowsAppsDirectory
        }
    }

    return Join-Path -Path $HOME -ChildPath '.local/bin'
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

$resolvedInstallDirectory = if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    [System.IO.Path]::GetFullPath((Get-SfAdDefaultInstallDirectory))
} else {
    [System.IO.Path]::GetFullPath($InstallDirectory)
}

$metadataPath = Get-SfAdInstallMetadataPath -ResolvedInstallDirectory $resolvedInstallDirectory -CommandName $CommandName
if (-not (Test-Path -Path $metadataPath -PathType Leaf)) {
    throw "Install metadata was not found at '$metadataPath'. Install '$CommandName' first."
}

$metadata = Get-Content -Path $metadataPath -Raw | ConvertFrom-Json -Depth 20
if ([string]::IsNullOrWhiteSpace("$($metadata.projectRoot)")) {
    throw "Install metadata at '$metadataPath' is missing the project root."
}

$installerPath = Join-Path -Path ([System.IO.Path]::GetFullPath("$($metadata.projectRoot)")) -ChildPath 'scripts/Install-SfAdSyncTerminalCommand.ps1'
if (-not (Test-Path -Path $installerPath -PathType Leaf)) {
    throw "Installer script was not found at '$installerPath'."
}

if ($ShowCurrent) {
    return [pscustomobject]@{
        commandName = $CommandName
        installDirectory = $resolvedInstallDirectory
        configPath = $metadata.configPath
        mappingConfigPath = $metadata.mappingConfigPath
        metadataPath = $metadataPath
    }
}

$installArguments = @{
    ProjectRoot = [System.IO.Path]::GetFullPath("$($metadata.projectRoot)")
    InstallDirectory = $resolvedInstallDirectory
    CommandName = $CommandName
    Force = $true
    SkipPathUpdate = $true
}

if ($PSBoundParameters.ContainsKey('ConfigPath')) {
    $installArguments['ConfigPath'] = $ConfigPath
}

if ($PSBoundParameters.ContainsKey('MappingConfigPath')) {
    $installArguments['MappingConfigPath'] = $MappingConfigPath
}

if (-not $installArguments.ContainsKey('ConfigPath') -and -not $installArguments.ContainsKey('MappingConfigPath')) {
    throw 'Pass -ConfigPath and optionally -MappingConfigPath, or use -ShowCurrent.'
}

& $installerPath @installArguments
