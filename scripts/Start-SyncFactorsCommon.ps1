[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
}

function Initialize-DotnetEnvironment {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot
    )

    $nugetHttpCachePath = Join-Path $ProjectRoot 'state/nuget/http-cache'
    $nugetPackagesPath = Join-Path $ProjectRoot 'state/nuget/packages'
    $nugetPluginsCachePath = Join-Path $ProjectRoot 'state/nuget/plugin-cache'
    New-Item -ItemType Directory -Force -Path $nugetHttpCachePath | Out-Null
    New-Item -ItemType Directory -Force -Path $nugetPackagesPath | Out-Null
    New-Item -ItemType Directory -Force -Path $nugetPluginsCachePath | Out-Null
    $env:NUGET_HTTP_CACHE_PATH = $nugetHttpCachePath
    $env:NUGET_PACKAGES = $nugetPackagesPath
    $env:NUGET_PLUGINS_CACHE_PATH = $nugetPluginsCachePath
}

function Resolve-RequiredPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label path was not provided."
    }

    return (Resolve-Path $Path).ProviderPath
}

function Set-StandardLoggingEnvironment {
    param(
        [string]$DefaultLevel = 'Information',
        [hashtable]$Overrides = @{}
    )

    $env:Logging__LogLevel__Default = $DefaultLevel
    foreach ($entry in $Overrides.GetEnumerator()) {
        Set-Item -Path ("Env:{0}" -f $entry.Key) -Value ([string]$entry.Value)
    }
}

function Get-SyncFactorsRuntimeRoot {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot
    )

    if ([OperatingSystem]::IsWindows()) {
        $basePath = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    }
    else {
        $basePath = $env:XDG_DATA_HOME
        if ([string]::IsNullOrWhiteSpace($basePath)) {
            $basePath = Join-Path $HOME '.local/share'
        }
    }

    return Join-Path $basePath 'SyncFactors'
}

function Test-SyncFactorsLocalFileLoggingEnabled {
    $value = $env:SYNCFACTORS_LOCAL_FILE_LOGGING_ENABLED
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $false
    }

    switch ($value.Trim().ToLowerInvariant()) {
        '1' { return $true }
        'on' { return $true }
        'true' { return $true }
        'yes' { return $true }
        default { return $false }
    }
}

function Get-SyncFactorsLocalLogDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($env:SYNCFACTORS_LOCAL_LOG_DIRECTORY)) {
        if ([System.IO.Path]::IsPathRooted($env:SYNCFACTORS_LOCAL_LOG_DIRECTORY)) {
            return [System.IO.Path]::GetFullPath($env:SYNCFACTORS_LOCAL_LOG_DIRECTORY)
        }

        return [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot $env:SYNCFACTORS_LOCAL_LOG_DIRECTORY))
    }

    return Join-Path (Get-SyncFactorsRuntimeRoot -ProjectRoot $ProjectRoot) 'logs'
}

function Get-SyncFactorsTlsAssetPaths {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot
    )

    $runtimeRoot = Get-SyncFactorsRuntimeRoot -ProjectRoot $ProjectRoot
    $certDirectory = Join-Path $runtimeRoot 'certs'

    return [pscustomobject]@{
        RuntimeRoot = $runtimeRoot
        CertificateDirectory = $certDirectory
        CertificatePath = Join-Path $certDirectory 'syncfactors-api-devcert.pfx'
        PasswordPath = Join-Path $certDirectory 'syncfactors-api-devcert.password'
    }
}

function Get-SyncFactorsTlsPassword {
    param(
        [Parameter(Mandatory)]
        [string]$PasswordPath
    )

    if (-not (Test-Path $PasswordPath)) {
        throw "Missing TLS certificate password file '$PasswordPath'. Run pwsh ./scripts/Install-SyncFactorsHttpsCertificate.ps1 or pwsh ./scripts/Install-SyncFactorsHttpsCertificateFromPfx.ps1 first."
    }

    return (Get-Content -Path $PasswordPath -Raw).Trim()
}

function Set-SyncFactorsSecretFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Value
    )

    Set-Content -Path $Path -Value $Value -NoNewline

    if ([OperatingSystem]::IsWindows()) {
        return
    }

    try {
        [System.IO.File]::SetUnixFileMode(
            $Path,
            [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite
        )
    }
    catch {
        Write-Warning "Could not restrict permissions for secret file '$Path': $($_.Exception.Message)"
    }
}

function Normalize-SyncFactorsCertificateThumbprint {
    param(
        [string]$Thumbprint
    )

    if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        return ''
    }

    return ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
}

function Add-SyncFactorsCertificateHostCandidate {
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.List[string]]$Candidates,
        [string]$HostName
    )

    if ([string]::IsNullOrWhiteSpace($HostName)) {
        return $false
    }

    $normalized = $HostName.Trim().Trim('[', ']').TrimEnd('.')
    if ([string]::IsNullOrWhiteSpace($normalized) -or (Test-SyncFactorsWildcardHost -HostName $normalized)) {
        return $false
    }

    if (-not $Candidates.Contains($normalized)) {
        $Candidates.Add($normalized)
    }

    return $true
}

function Test-SyncFactorsWildcardHost {
    param(
        [string]$HostName
    )

    return $HostName -eq '*' -or
        $HostName -eq '+' -or
        $HostName -eq '0.0.0.0' -or
        $HostName -eq '::'
}

function Get-SyncFactorsCertificateHostCandidates {
    param(
        [string]$Urls
    )

    $candidates = [System.Collections.Generic.List[string]]::new()
    $hadPublicHost = Add-SyncFactorsCertificateHostCandidate -Candidates $candidates -HostName $env:SYNCFACTORS_API_PUBLIC_HOST
    $hasWildcardBinding = $false

    if (-not [string]::IsNullOrWhiteSpace($Urls)) {
        foreach ($rawUrl in ($Urls -split ';' | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
            try {
                $uri = [System.Uri]::new($rawUrl)
                $hasWildcardBinding = (Test-SyncFactorsWildcardHost -HostName $uri.Host) -or $hasWildcardBinding
                $null = Add-SyncFactorsCertificateHostCandidate -Candidates $candidates -HostName $uri.Host
            }
            catch {
            }
        }
    }

    if ($hadPublicHost -or $hasWildcardBinding -or $candidates.Count -eq 0) {
        $null = Add-SyncFactorsCertificateHostCandidate -Candidates $candidates -HostName ([Environment]::MachineName)
        try {
            $null = Add-SyncFactorsCertificateHostCandidate -Candidates $candidates -HostName ([System.Net.Dns]::GetHostEntry('').HostName)
        }
        catch {
        }
    }

    return $candidates.ToArray()
}

function Test-SyncFactorsCertificateHostMatch {
    param(
        [Parameter(Mandatory)]
        [string]$HostName,
        [Parameter(Mandatory)]
        [string]$CertificateName
    )

    $hostValue = $HostName.Trim().TrimEnd('.').ToLowerInvariant()
    $certValue = $CertificateName.Trim().TrimEnd('.').ToLowerInvariant()
    if ($hostValue -eq $certValue) {
        return $true
    }

    if (-not $certValue.StartsWith('*.')) {
        return $false
    }

    $suffix = $certValue.Substring(1)
    if (-not $hostValue.EndsWith($suffix)) {
        return $false
    }

    $prefix = $hostValue.Substring(0, $hostValue.Length - $suffix.Length)
    return $prefix.Length -gt 0 -and -not $prefix.Contains('.')
}

function Get-SyncFactorsCertificateDnsNames {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $names = [System.Collections.Generic.List[string]]::new()
    $dnsNameListProperty = $Certificate.PSObject.Properties['DnsNameList']
    if ($null -ne $dnsNameListProperty) {
        foreach ($dnsName in @($dnsNameListProperty.Value | ForEach-Object { $_.Unicode })) {
            $null = Add-SyncFactorsCertificateHostCandidate -Candidates $names -HostName $dnsName
        }
    }

    $simpleName = $Certificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::DnsName, $false)
    $null = Add-SyncFactorsCertificateHostCandidate -Candidates $names -HostName $simpleName
    return $names.ToArray()
}

function Test-SyncFactorsUsableServerCertificate {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if (-not $Certificate.HasPrivateKey -or $Certificate.NotAfter -le (Get-Date) -or $Certificate.NotBefore -gt (Get-Date)) {
        return $false
    }

    $eku = @($Certificate.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } | Select-Object -First 1)
    if ($eku.Count -eq 0) {
        return $true
    }

    return @($eku[0].EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.1' }).Count -gt 0
}

function Find-SyncFactorsMachineCertificate {
    param(
        [string]$Urls,
        [string]$Thumbprint
    )

    if (-not [OperatingSystem]::IsWindows()) {
        return $null
    }

    $normalizedThumbprint = Normalize-SyncFactorsCertificateThumbprint -Thumbprint $Thumbprint
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        [System.Security.Cryptography.X509Certificates.StoreName]::My,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)

    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $certificates = @($store.Certificates | Where-Object { Test-SyncFactorsUsableServerCertificate -Certificate $_ })

        if (-not [string]::IsNullOrWhiteSpace($normalizedThumbprint)) {
            return $certificates |
                Where-Object { (Normalize-SyncFactorsCertificateThumbprint -Thumbprint $_.Thumbprint) -eq $normalizedThumbprint } |
                Sort-Object -Property NotAfter -Descending |
                Select-Object -First 1
        }

        $hostCandidates = @(Get-SyncFactorsCertificateHostCandidates -Urls $Urls)
        if ($hostCandidates.Count -eq 0) {
            return $null
        }

        return $certificates |
            Where-Object {
                $matched = $false
                $certificateNames = @(Get-SyncFactorsCertificateDnsNames -Certificate $_)
                foreach ($candidate in $hostCandidates) {
                    foreach ($certificateName in $certificateNames) {
                        if (Test-SyncFactorsCertificateHostMatch -HostName $candidate -CertificateName $certificateName) {
                            $matched = $true
                            break
                        }
                    }

                    if ($matched) {
                        break
                    }
                }

                $matched
            } |
            Sort-Object -Property NotAfter -Descending |
            Select-Object -First 1
    }
    finally {
        $store.Close()
    }
}

function Initialize-SyncFactorsHttpsEnvironment {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot,
        [Parameter(Mandatory)]
        [string]$Urls
    )

    if ($Urls -match '(^|;)http://') {
        throw "SyncFactors launch scripts enforce HTTPS-only bindings. Use https:// URLs only."
    }

    $explicitCertificatePath = -not [string]::IsNullOrWhiteSpace($env:SYNCFACTORS_TLS_CERT_PATH)
    $explicitCertificatePassword = -not [string]::IsNullOrWhiteSpace($env:SYNCFACTORS_TLS_CERT_PASSWORD)
    if (-not $explicitCertificatePath -and -not $explicitCertificatePassword) {
        $machineCertificate = Find-SyncFactorsMachineCertificate -Urls $Urls -Thumbprint $env:SYNCFACTORS_TLS_CERT_THUMBPRINT
        if ($null -ne $machineCertificate) {
            $env:SYNCFACTORS_TLS_CERT_THUMBPRINT = $machineCertificate.Thumbprint
            $env:SyncFactors__Tls__MachineCertificateThumbprint = $machineCertificate.Thumbprint
            return
        }
    }

    $tlsAssets = Get-SyncFactorsTlsAssetPaths -ProjectRoot $ProjectRoot
    $certificatePath = if (-not $explicitCertificatePath) {
        $tlsAssets.CertificatePath
    }
    else {
        $env:SYNCFACTORS_TLS_CERT_PATH
    }

    $certificatePassword = if (-not $explicitCertificatePassword) {
        Get-SyncFactorsTlsPassword -PasswordPath $tlsAssets.PasswordPath
    }
    else {
        $env:SYNCFACTORS_TLS_CERT_PASSWORD
    }

    if (-not (Test-Path $certificatePath)) {
        throw "Missing TLS certificate '$certificatePath'. Run pwsh ./scripts/Install-SyncFactorsHttpsCertificate.ps1 or pwsh ./scripts/Install-SyncFactorsHttpsCertificateFromPfx.ps1 first."
    }

    $env:ASPNETCORE_Kestrel__Certificates__Default__Path = $certificatePath
    $env:ASPNETCORE_Kestrel__Certificates__Default__Password = $certificatePassword
}

function Resolve-SyncFactorsBuildVersion {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($env:SYNCFACTORS_BUILD_VERSION)) {
        return $env:SYNCFACTORS_BUILD_VERSION.Trim()
    }

    $releaseInfoScript = Join-Path $ProjectRoot 'scripts/Get-SyncFactorsReleaseInfo.ps1'
    if ((Get-Command 'git' -ErrorAction SilentlyContinue) -and (Test-Path $releaseInfoScript)) {
        try {
            $commitSha = (& git -C $ProjectRoot rev-parse HEAD 2>$null).Trim()
            if (-not [string]::IsNullOrWhiteSpace($commitSha)) {
                $releaseInfo = & $releaseInfoScript -Channel Stable -CommitSha $commitSha
                if (-not [string]::IsNullOrWhiteSpace([string]$releaseInfo.version)) {
                    return [string]$releaseInfo.version
                }
            }
        }
        catch {
            Write-Warning "Could not resolve git-derived SyncFactors build version. Falling back to VERSION file. $_"
        }
    }

    $versionPath = Join-Path $ProjectRoot 'VERSION'
    if (Test-Path $versionPath) {
        return (Get-Content -Path $versionPath -Raw).Trim()
    }

    return '0.0.0'
}

function Invoke-SolutionBuild {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot,
        [string]$BuildVersion
    )

    Initialize-DotnetEnvironment -ProjectRoot $ProjectRoot

    if ([string]::IsNullOrWhiteSpace($BuildVersion)) {
        $BuildVersion = Resolve-SyncFactorsBuildVersion -ProjectRoot $ProjectRoot
    }

    dotnet build (Join-Path $ProjectRoot 'SyncFactors.Next.sln') `
        -m:1 `
        -p:UseSharedCompilation=false `
        "-p:Version=$BuildVersion" `
        "-p:InformationalVersion=$BuildVersion"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed."
    }
}

function Invoke-FrontendBuild {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot
    )

    $frontendRoot = Join-Path $ProjectRoot 'src/SyncFactors.Api'
    $packageJsonPath = Join-Path $frontendRoot 'package.json'
    $nodeModulesPath = Join-Path $frontendRoot 'node_modules'

    if (-not (Test-Path $packageJsonPath)) {
        return
    }

    if (-not (Get-Command 'npm' -ErrorAction SilentlyContinue)) {
        throw "npm is required to build the SyncFactors UI bundle."
    }

    if (-not (Test-Path $nodeModulesPath)) {
        throw "Missing frontend dependencies under '$nodeModulesPath'. Run 'cd src/SyncFactors.Api && npm install' first."
    }

    Push-Location $frontendRoot
    try {
        npm run build:ui
        if ($LASTEXITCODE -ne 0) {
            throw "npm run build:ui failed."
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-DotnetProjectRun {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,
        [Parameter(Mandatory)]
        [string]$ProjectRoot,
        [switch]$SkipBuild,
        [string[]]$Arguments = @()
    )

    Push-Location $ProjectRoot
    try {
        Initialize-DotnetEnvironment -ProjectRoot $ProjectRoot

        $buildVersion = Resolve-SyncFactorsBuildVersion -ProjectRoot $ProjectRoot
        Write-Host "Build version: $buildVersion" -ForegroundColor Cyan

        $dotnetRunArguments = @()

        if (-not $SkipBuild) {
            Invoke-SolutionBuild -ProjectRoot $ProjectRoot -BuildVersion $buildVersion
            $dotnetRunArguments += '--no-build'
        }
        else {
            $dotnetRunArguments += '--no-build'
        }

        $dotnetRunArguments += @('--project', $ProjectPath)
        if ($Arguments.Count -gt 0) {
            $dotnetRunArguments += $Arguments
        }

        dotnet run @dotnetRunArguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet run failed."
        }
    }
    finally {
        Pop-Location
    }
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
        [int[]]$HostProcessIds = @(),
        [switch]$MatchContains
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
    set useContainsMatch to item 2 of argv

    tell application "Terminal"
        set matchingWindows to {}

        repeat with currentWindow in windows
            set shouldCloseWindow to false

            repeat with currentTab in tabs of currentWindow
                set tabTitle to ""
                set tabName to ""
                set labelMatches to false

                try
                    set tabTitle to custom title of currentTab
                end try

                try
                    set tabName to name of currentTab
                end try

                if useContainsMatch is "true" then
                    if tabTitle contains targetLabel or tabName contains targetLabel then
                        set labelMatches to true
                    end if
                else
                    if tabTitle is targetLabel or tabName is targetLabel then
                        set labelMatches to true
                    end if
                end if

                if labelMatches then
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

    $matchMode = if ($MatchContains) { 'true' } else { 'false' }
    foreach ($label in ($Labels | Sort-Object -Unique)) {
        $script | & osascript - $label $matchMode | Out-Null
    }
}
