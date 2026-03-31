[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
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

function Invoke-SolutionBuild {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot
    )

    dotnet build (Join-Path $ProjectRoot 'SyncFactors.Next.sln') -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed."
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
        if (-not $SkipBuild) {
            Invoke-SolutionBuild -ProjectRoot $ProjectRoot
        }

        dotnet run @Arguments --project $ProjectPath
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet run failed."
        }
    }
    finally {
        Pop-Location
    }
}
