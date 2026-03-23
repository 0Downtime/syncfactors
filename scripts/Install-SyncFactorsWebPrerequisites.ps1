[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [ValidateSet('Auto', 'User', 'Machine')]
    [string]$Scope = 'Auto',
    [string]$SqliteInstallDirectory,
    [switch]$SkipNodeInstall,
    [switch]$SkipSqliteInstall,
    [switch]$SkipNpmInstall,
    [switch]$ForceNodeInstall,
    [switch]$ForceSqliteInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-SyncFactorsStatus {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('INFO', 'OK', 'WARN', 'FAIL', 'STEP')]
        [string]$Level,
        [Parameter(Mandatory)]
        [string]$Message
    )

    $color = switch ($Level) {
        'INFO' { 'Cyan' }
        'OK' { 'Green' }
        'WARN' { 'Yellow' }
        'FAIL' { 'Red' }
        'STEP' { 'Magenta' }
    }

    Write-Host "[$Level] $Message" -ForegroundColor $color
}

function Test-SyncFactorsIsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-SyncFactorsProjectRoot {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $Path = Split-Path -Path $PSScriptRoot -Parent
    }

    return (Resolve-Path -Path $Path).ProviderPath
}

function Resolve-SyncFactorsInstallScope {
    param([string]$RequestedScope)

    if ($RequestedScope -eq 'Auto') {
        if (Test-SyncFactorsIsAdministrator) {
            return 'Machine'
        }

        return 'User'
    }

    if ($RequestedScope -eq 'Machine' -and -not (Test-SyncFactorsIsAdministrator)) {
        throw "Machine scope requires an elevated PowerShell session. Re-run as Administrator or use -Scope User."
    }

    return $RequestedScope
}

function Get-SyncFactorsPathEnvironmentTarget {
    param([string]$EffectiveScope)

    if ($EffectiveScope -eq 'Machine') {
        return 'Machine'
    }

    return 'User'
}

function Get-SyncFactorsDefaultSqliteInstallDirectory {
    param([string]$EffectiveScope)

    if ($EffectiveScope -eq 'Machine') {
        return Join-Path -Path $env:ProgramFiles -ChildPath 'SQLite'
    }

    return Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Programs\SQLite'
}

function Test-SyncFactorsPathContainsDirectory {
    param(
        [AllowNull()]
        [string]$PathValue,
        [Parameter(Mandatory)]
        [string]$Directory
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $false
    }

    $normalizedDirectory = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    foreach ($entry in ($PathValue -split ';')) {
        if ([string]::IsNullOrWhiteSpace($entry)) {
            continue
        }

        try {
            $normalizedEntry = [System.IO.Path]::GetFullPath($entry).TrimEnd('\', '/')
        } catch {
            continue
        }

        if ($normalizedEntry -ieq $normalizedDirectory) {
            return $true
        }
    }

    return $false
}

function Add-SyncFactorsDirectoryToPath {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,
        [Parameter(Mandatory)]
        [string]$EffectiveScope
    )

    $target = Get-SyncFactorsPathEnvironmentTarget -EffectiveScope $EffectiveScope
    $persistentPath = [Environment]::GetEnvironmentVariable('Path', $target)
    if (-not (Test-SyncFactorsPathContainsDirectory -PathValue $persistentPath -Directory $Directory)) {
        $newPath = if ([string]::IsNullOrWhiteSpace($persistentPath)) {
            $Directory
        } else {
            "$persistentPath;$Directory"
        }
        [Environment]::SetEnvironmentVariable('Path', $newPath, $target)
        Write-SyncFactorsStatus -Level OK -Message "Persisted '$Directory' to the $target PATH."
    } else {
        Write-SyncFactorsStatus -Level INFO -Message "'$Directory' is already present on the $target PATH."
    }

    if (-not (Test-SyncFactorsPathContainsDirectory -PathValue $env:Path -Directory $Directory)) {
        $env:Path = "$env:Path;$Directory"
        Write-SyncFactorsStatus -Level OK -Message "Updated PATH for the current PowerShell session."
    } else {
        Write-SyncFactorsStatus -Level INFO -Message "Current PowerShell session already sees '$Directory' on PATH."
    }
}

function Get-SyncFactorsCommand {
    param([Parameter(Mandatory)][string]$Name)

    return Get-Command -Name $Name -ErrorAction SilentlyContinue
}

function Test-SyncFactorsCommandAvailable {
    param([Parameter(Mandatory)][string]$Name)

    return $null -ne (Get-SyncFactorsCommand -Name $Name)
}

function Get-SyncFactorsNodeRelease {
    Write-SyncFactorsStatus -Level INFO -Message 'Querying the official Node.js release index.'
    $releases = Invoke-RestMethod -Uri 'https://nodejs.org/dist/index.json'
    $matchingRelease = $releases |
        Where-Object { $_.lts -and $_.files -contains 'win-x64-msi' } |
        Select-Object -First 1

    if ($null -eq $matchingRelease) {
        throw 'Unable to find a Windows x64 LTS Node.js MSI in the Node.js release index.'
    }

    $version = "$($matchingRelease.version)"
    $msiName = "node-$($version)-x64.msi"
    return [pscustomobject]@{
        Version = $version
        Uri = "https://nodejs.org/dist/$version/$msiName"
        FileName = $msiName
    }
}

function Get-SyncFactorsSqliteToolsPackage {
    Write-SyncFactorsStatus -Level INFO -Message 'Querying the official SQLite download page.'
    $response = Invoke-WebRequest -Uri 'https://www.sqlite.org/download.html'
    $match = [regex]::Match($response.Content, 'href="(?<relative>\d{4}/sqlite-tools-win-x64-[^"]+\.zip)"')
    if (-not $match.Success) {
        throw 'Unable to find a Windows x64 SQLite tools zip on the SQLite download page.'
    }

    $relativePath = $match.Groups['relative'].Value
    return [pscustomobject]@{
        Uri = "https://www.sqlite.org/$relativePath"
        FileName = [System.IO.Path]::GetFileName($relativePath)
    }
}

function Invoke-SyncFactorsProcess {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $startInfo.WorkingDirectory = $WorkingDirectory
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
    } finally {
        $process.Dispose()
    }

    if ($process.ExitCode -ne 0) {
        $details = @($stderr, $stdout) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        throw "Command failed: $FilePath $($ArgumentList -join ' ')$([Environment]::NewLine)$($details -join [Environment]::NewLine)"
    }

    return [pscustomobject]@{
        StdOut = $stdout
        StdErr = $stderr
    }
}

function Install-SyncFactorsNode {
    param(
        [Parameter(Mandatory)]
        [string]$TempDirectory,
        [Parameter(Mandatory)]
        [string]$EffectiveScope
    )

    $release = Get-SyncFactorsNodeRelease
    $msiPath = Join-Path -Path $TempDirectory -ChildPath $release.FileName
    Write-SyncFactorsStatus -Level INFO -Message "Downloading Node.js LTS $($release.Version) from $($release.Uri)."
    Invoke-WebRequest -Uri $release.Uri -OutFile $msiPath

    if (-not (Test-Path -Path $msiPath -PathType Leaf)) {
        throw "Node.js installer download did not produce '$msiPath'."
    }

    Write-SyncFactorsStatus -Level INFO -Message 'Running the Node.js MSI installer silently.'
    $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', $msiPath, '/qn', '/norestart') -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Node.js MSI installation failed with exit code $($process.ExitCode)."
    }

    $nodeCommand = Get-SyncFactorsCommand -Name 'node'
    $npmCommand = Get-SyncFactorsCommand -Name 'npm'
    if ($null -eq $nodeCommand -or $null -eq $npmCommand) {
        $candidateDirectories = @(
            Join-Path -Path $env:ProgramFiles -ChildPath 'nodejs'
            Join-Path -Path $env:LOCALAPPDATA -ChildPath 'Programs\nodejs'
        ) | Where-Object { Test-Path -Path $_ -PathType Container }

        foreach ($candidateDirectory in $candidateDirectories) {
            Add-SyncFactorsDirectoryToPath -Directory $candidateDirectory -EffectiveScope $EffectiveScope
        }

        $nodeCommand = Get-SyncFactorsCommand -Name 'node'
        $npmCommand = Get-SyncFactorsCommand -Name 'npm'
    }

    if ($null -eq $nodeCommand -or $null -eq $npmCommand) {
        throw 'Node.js installation completed but node/npm were not found on PATH.'
    }

    $nodeVersion = (& $nodeCommand.Source --version).Trim()
    $npmVersion = (& $npmCommand.Source --version).Trim()
    Write-SyncFactorsStatus -Level OK -Message "Node.js is available: node $nodeVersion, npm $npmVersion."
}

function Install-SyncFactorsSqlite {
    param(
        [Parameter(Mandatory)]
        [string]$InstallDirectory,
        [Parameter(Mandatory)]
        [string]$EffectiveScope,
        [Parameter(Mandatory)]
        [string]$TempDirectory
    )

    $package = Get-SyncFactorsSqliteToolsPackage
    $zipPath = Join-Path -Path $TempDirectory -ChildPath $package.FileName
    $extractDirectory = Join-Path -Path $TempDirectory -ChildPath 'sqlite-tools'

    Write-SyncFactorsStatus -Level INFO -Message "Downloading SQLite tools from $($package.Uri)."
    Invoke-WebRequest -Uri $package.Uri -OutFile $zipPath
    if (-not (Test-Path -Path $zipPath -PathType Leaf)) {
        throw "SQLite tools download did not produce '$zipPath'."
    }

    if (Test-Path -Path $extractDirectory -PathType Container) {
        Remove-Item -Path $extractDirectory -Recurse -Force
    }

    Write-SyncFactorsStatus -Level INFO -Message 'Extracting SQLite tools.'
    Expand-Archive -Path $zipPath -DestinationPath $extractDirectory -Force

    $sqliteBinary = Get-ChildItem -Path $extractDirectory -Filter 'sqlite3.exe' -Recurse -File | Select-Object -First 1
    if ($null -eq $sqliteBinary) {
        throw 'SQLite archive extraction did not contain sqlite3.exe.'
    }

    if (-not (Test-Path -Path $InstallDirectory -PathType Container)) {
        New-Item -Path $InstallDirectory -ItemType Directory -Force | Out-Null
    }

    Write-SyncFactorsStatus -Level INFO -Message "Copying sqlite3.exe into '$InstallDirectory'."
    Copy-Item -Path $sqliteBinary.FullName -Destination (Join-Path -Path $InstallDirectory -ChildPath 'sqlite3.exe') -Force

    Add-SyncFactorsDirectoryToPath -Directory $InstallDirectory -EffectiveScope $EffectiveScope

    $sqliteCommand = Get-SyncFactorsCommand -Name 'sqlite3'
    if ($null -eq $sqliteCommand) {
        throw 'SQLite installation completed but sqlite3.exe was not found on PATH.'
    }

    $sqliteVersion = (& $sqliteCommand.Source --version).Trim()
    Write-SyncFactorsStatus -Level OK -Message "SQLite CLI is available: $sqliteVersion."
}

function Invoke-SyncFactorsNpmInstall {
    param([Parameter(Mandatory)][string]$ResolvedProjectRoot)

    $npmCommand = Get-SyncFactorsCommand -Name 'npm'
    if ($null -eq $npmCommand) {
        throw 'npm is not available on PATH. Install Node.js first.'
    }

    Write-SyncFactorsStatus -Level INFO -Message "Running npm install in '$ResolvedProjectRoot'."
    Push-Location -Path $ResolvedProjectRoot
    try {
        & $npmCommand.Source install
        if ($LASTEXITCODE -ne 0) {
            throw "npm install failed with exit code $LASTEXITCODE."
        }
    } finally {
        Pop-Location
    }

    $nodeModulesPath = Join-Path -Path $ResolvedProjectRoot -ChildPath 'node_modules'
    if (-not (Test-Path -Path $nodeModulesPath -PathType Container)) {
        throw "npm install completed without creating '$nodeModulesPath'."
    }

    Write-SyncFactorsStatus -Level OK -Message 'npm dependencies are installed.'
}

$resolvedProjectRoot = Resolve-SyncFactorsProjectRoot -Path $ProjectRoot
$effectiveScope = Resolve-SyncFactorsInstallScope -RequestedScope $Scope
$resolvedSqliteInstallDirectory = if ([string]::IsNullOrWhiteSpace($SqliteInstallDirectory)) {
    Get-SyncFactorsDefaultSqliteInstallDirectory -EffectiveScope $effectiveScope
} else {
    [System.IO.Path]::GetFullPath($SqliteInstallDirectory)
}
$tempDirectory = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ("syncfactors-install-{0}" -f ([guid]::NewGuid().Guid))
New-Item -Path $tempDirectory -ItemType Directory -Force | Out-Null

$summary = [ordered]@{
    projectRoot = $resolvedProjectRoot
    scope = $effectiveScope
    sqliteInstallDirectory = $resolvedSqliteInstallDirectory
    nodeInstalled = $false
    sqliteInstalled = $false
    npmDependenciesInstalled = $false
}

Write-SyncFactorsStatus -Level STEP -Message 'Starting SyncFactors web prerequisite installation.'
Write-SyncFactorsStatus -Level INFO -Message "Project root: $resolvedProjectRoot"
Write-SyncFactorsStatus -Level INFO -Message "Install scope: $effectiveScope"
Write-SyncFactorsStatus -Level INFO -Message "SQLite install directory: $resolvedSqliteInstallDirectory"

try {
    Write-SyncFactorsStatus -Level STEP -Message 'Checking Node.js and npm.'
    $nodeCommand = Get-SyncFactorsCommand -Name 'node'
    $npmCommand = Get-SyncFactorsCommand -Name 'npm'
    if ($SkipNodeInstall) {
        if ($null -eq $nodeCommand -or $null -eq $npmCommand) {
            throw 'SkipNodeInstall was specified, but node/npm are not currently available on PATH.'
        }
        Write-SyncFactorsStatus -Level OK -Message "Skipping Node.js installation. Found node at '$($nodeCommand.Source)' and npm at '$($npmCommand.Source)'."
    } elseif ($ForceNodeInstall -or $null -eq $nodeCommand -or $null -eq $npmCommand) {
        Install-SyncFactorsNode -TempDirectory $tempDirectory -EffectiveScope $effectiveScope
    } else {
        $nodeVersion = (& $nodeCommand.Source --version).Trim()
        $npmVersion = (& $npmCommand.Source --version).Trim()
        Write-SyncFactorsStatus -Level OK -Message "Node.js already installed: node $nodeVersion, npm $npmVersion."
    }
    $summary.nodeInstalled = $true

    Write-SyncFactorsStatus -Level STEP -Message 'Checking SQLite CLI.'
    $sqliteCommand = Get-SyncFactorsCommand -Name 'sqlite3'
    if ($SkipSqliteInstall) {
        if ($null -eq $sqliteCommand) {
            throw 'SkipSqliteInstall was specified, but sqlite3 is not currently available on PATH.'
        }
        Write-SyncFactorsStatus -Level OK -Message "Skipping SQLite installation. Found sqlite3 at '$($sqliteCommand.Source)'."
    } elseif ($ForceSqliteInstall -or $null -eq $sqliteCommand) {
        Install-SyncFactorsSqlite -InstallDirectory $resolvedSqliteInstallDirectory -EffectiveScope $effectiveScope -TempDirectory $tempDirectory
    } else {
        $sqliteVersion = (& $sqliteCommand.Source --version).Trim()
        Write-SyncFactorsStatus -Level OK -Message "SQLite CLI already installed: $sqliteVersion."
    }
    $summary.sqliteInstalled = $true

    Write-SyncFactorsStatus -Level STEP -Message 'Checking npm project dependencies.'
    if ($SkipNpmInstall) {
        Write-SyncFactorsStatus -Level WARN -Message 'Skipping npm install because -SkipNpmInstall was specified.'
    } else {
        Invoke-SyncFactorsNpmInstall -ResolvedProjectRoot $resolvedProjectRoot
        $summary.npmDependenciesInstalled = $true
    }

    Write-SyncFactorsStatus -Level STEP -Message 'Final verification.'
    foreach ($requiredCommand in @('node', 'npm', 'sqlite3')) {
        if (-not (Test-SyncFactorsCommandAvailable -Name $requiredCommand)) {
            throw "Final verification failed: '$requiredCommand' is not available on PATH."
        }
        Write-SyncFactorsStatus -Level OK -Message "Verified '$requiredCommand' is available on PATH."
    }

    Write-SyncFactorsStatus -Level OK -Message 'SyncFactors web prerequisite installation completed successfully.'
    [pscustomobject]$summary
} catch {
    Write-SyncFactorsStatus -Level FAIL -Message $_.Exception.Message
    throw
} finally {
    if (Test-Path -Path $tempDirectory -PathType Container) {
        Remove-Item -Path $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
