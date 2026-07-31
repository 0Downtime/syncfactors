[CmdletBinding()]
param(
    [string]$ApiServiceName = 'SyncFactors.Api',
    [string]$WorkerServiceName = 'SyncFactors.Worker',
    [switch]$DryRunOnly,
    [switch]$EnableLiveWrites,
    [string]$HealthUrl = 'https://localhost:5087/readyz',
    [switch]$SkipHttpHealthCheck,
    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ServiceEnvironmentValue {
    param(
        [Parameter(Mandatory)]
        [string]$ServiceName,
        [Parameter(Mandatory)]
        [string]$Name
    )

    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (-not (Test-Path -Path $serviceKey)) {
        throw "Service registry key '$serviceKey' was not found."
    }

    $environment = Get-ItemPropertyValue -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue
    foreach ($entry in @($environment)) {
        if ($entry.StartsWith("$Name=", [System.StringComparison]::OrdinalIgnoreCase)) {
            return $entry.Substring($Name.Length + 1)
        }
    }

    return $null
}

function Wait-ServiceRunning {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [DateTimeOffset]$Deadline
    )

    do {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            throw "Service '$Name' is not installed."
        }

        if ($service.Status -eq 'Running') {
            return $service
        }

        Start-Sleep -Seconds 1
    }
    while ([DateTimeOffset]::UtcNow -lt $Deadline)

    throw "Service '$Name' did not reach Running within the deployment verification timeout. Last status: $($service.Status)."
}

function Invoke-HttpHealthCheck {
    param(
        [Parameter(Mandatory)]
        [string]$Url,
        [Parameter(Mandatory)]
        [DateTimeOffset]$Deadline
    )

    $lastFailure = $null
    do {
        try {
            $response = Invoke-WebRequest -Uri $Url -SkipCertificateCheck -UseBasicParsing -TimeoutSec 15
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                return $response.StatusCode
            }

            $lastFailure = "HTTP $($response.StatusCode)"
        }
        catch {
            $lastFailure = $_.Exception.Message
        }

        Start-Sleep -Seconds 2
    }
    while ([DateTimeOffset]::UtcNow -lt $Deadline)

    throw "Health check '$Url' did not succeed within the deployment verification timeout. Last failure: $lastFailure"
}

if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'SyncFactors Windows deployment verification can only run on Windows.'
}

if ($DryRunOnly.IsPresent -and $EnableLiveWrites.IsPresent) {
    throw 'DryRunOnly and EnableLiveWrites cannot be specified together.'
}

$expectedDryRunOnly = -not $EnableLiveWrites.IsPresent
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$services = @($ApiServiceName, $WorkerServiceName)
$serviceResults = @()

foreach ($serviceName in $services) {
    $service = Wait-ServiceRunning -Name $serviceName -Deadline $deadline
    $configuredValue = Get-ServiceEnvironmentValue -ServiceName $serviceName -Name 'SyncFactors__Runtime__DryRunOnly'
    $parsedValue = $false
    if ([string]::IsNullOrWhiteSpace($configuredValue) -or
        -not [bool]::TryParse($configuredValue, [ref]$parsedValue)) {
        throw "Service '$serviceName' must have an explicit boolean SyncFactors__Runtime__DryRunOnly environment value."
    }

    if ($parsedValue -ne $expectedDryRunOnly) {
        $expectedLabel = $expectedDryRunOnly.ToString().ToLowerInvariant()
        throw "Service '$serviceName' has SyncFactors__Runtime__DryRunOnly=$configuredValue; expected $expectedLabel."
    }

    $serviceResults += [pscustomobject]@{
        name = $serviceName
        status = $service.Status.ToString()
        dryRunOnly = $parsedValue
    }
}

$healthStatusCode = $null
if (-not $SkipHttpHealthCheck.IsPresent) {
    $healthStatusCode = Invoke-HttpHealthCheck -Url $HealthUrl -Deadline $deadline
}

[pscustomobject]@{
    ready = $true
    dryRunOnly = $expectedDryRunOnly
    services = $serviceResults
    healthUrl = if ($SkipHttpHealthCheck.IsPresent) { $null } else { $HealthUrl }
    healthStatusCode = $healthStatusCode
}
