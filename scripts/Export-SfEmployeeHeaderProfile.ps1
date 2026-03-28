param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$PersonIdExternal,

    [Parameter(Mandatory = $true, ParameterSetName = "OAuth")]
    [string]$ClientId,

    [Parameter(Mandatory = $true, ParameterSetName = "OAuth")]
    [string]$ClientSecret,

    [Parameter(Mandatory = $true, ParameterSetName = "OAuth")]
    [string]$CompanyId,

    [Parameter(ParameterSetName = "OAuth")]
    [string]$TokenUrl = "",

    [Parameter(Mandatory = $true, ParameterSetName = "Basic")]
    [string]$Username,

    [Parameter(Mandatory = $true, ParameterSetName = "Basic")]
    [string]$Password,

    [string]$OutputPath = ".\perperson-header-profile.json",
    [string[]]$ExcludeSelectPath = @(),
    [string[]]$ExcludeExpandPath = @(),
    [string[]]$AdditionalSelectPath = @(),
    [string[]]$AdditionalExpandPath = @()
)

$ErrorActionPreference = "Stop"

$scriptPath = Join-Path -Path $PSScriptRoot -ChildPath "Export-SfPerPerson-Sanitized.ps1"
if (-not (Test-Path -Path $scriptPath)) {
    throw "Required script not found: $scriptPath"
}

$commonParameters = @{
    BaseUrl = $BaseUrl
    PersonIdExternal = $PersonIdExternal
    OutputPath = $OutputPath
    IncludeHeaderProfile = $true
    SkipSanitization = $true
    ExcludeSelectPath = $ExcludeSelectPath
    ExcludeExpandPath = $ExcludeExpandPath
    AdditionalSelectPath = $AdditionalSelectPath
    AdditionalExpandPath = $AdditionalExpandPath
}

if ($PSCmdlet.ParameterSetName -eq "OAuth") {
    $commonParameters["ClientId"] = $ClientId
    $commonParameters["ClientSecret"] = $ClientSecret
    $commonParameters["CompanyId"] = $CompanyId
    if (-not [string]::IsNullOrWhiteSpace($TokenUrl)) {
        $commonParameters["TokenUrl"] = $TokenUrl
    }
}
else {
    $commonParameters["Username"] = $Username
    $commonParameters["Password"] = $Password
}

& $scriptPath @commonParameters
