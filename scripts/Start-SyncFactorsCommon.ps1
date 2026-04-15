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
    New-Item -ItemType Directory -Force -Path $nugetHttpCachePath | Out-Null
    $env:NUGET_HTTP_CACHE_PATH = $nugetHttpCachePath
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

    $tlsAssets = Get-SyncFactorsTlsAssetPaths -ProjectRoot $ProjectRoot
    $certificatePath = if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_TLS_CERT_PATH)) {
        $tlsAssets.CertificatePath
    }
    else {
        $env:SYNCFACTORS_TLS_CERT_PATH
    }

    $certificatePassword = if ([string]::IsNullOrWhiteSpace($env:SYNCFACTORS_TLS_CERT_PASSWORD)) {
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

function Invoke-SolutionBuild {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot
    )

    Initialize-DotnetEnvironment -ProjectRoot $ProjectRoot

    dotnet build (Join-Path $ProjectRoot 'SyncFactors.Next.sln') -m:1 -p:UseSharedCompilation=false
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

        $dotnetRunArguments = @()

        if (-not $SkipBuild) {
            Invoke-SolutionBuild -ProjectRoot $ProjectRoot
            $dotnetRunArguments += '--no-restore'
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
