[CmdletBinding()]
param(
    [string]$ApiServiceName = 'SyncFactors.Api',
    [string]$WorkerServiceName = 'SyncFactors.Worker',
    [switch]$DryRunOnly,
    [switch]$EnableLiveWrites,
    [Parameter(Mandatory)]
    [ValidateLength(32, 512)]
    [string]$DeploymentNonce,
    [Parameter(Mandatory)]
    [DateTimeOffset]$WorkerStartedAfter,
    [string]$ExpectedWorkerVersion,
    [string]$ExpectedWorkerCommit,
    [string]$ExpectedApiVersion,
    [string]$ExpectedApiCommit,
    [string]$TlsCertificateThumbprint,
    [string]$HealthUrl = 'https://localhost:5087/readyz',
    [switch]$SkipHttpHealthCheck,
    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-HttpsHealthUrl {
    param([Parameter(Mandatory)][string]$Url)
    $uri = [Uri]$Url
    if ($uri.Scheme -ne 'https') {
        throw 'Production deployment readiness requires an HTTPS HealthUrl so the deployment nonce is never sent over plaintext.'
    }
}

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
        if ($null -ne $entry -and $entry.StartsWith("$Name=", [System.StringComparison]::OrdinalIgnoreCase)) {
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

function Normalize-CertificateThumbprint {
    param([string]$Thumbprint)
    if ([string]::IsNullOrWhiteSpace($Thumbprint)) { return $null }
    $normalized = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($normalized -notmatch '^(?:[0-9A-F]{40}|[0-9A-F]{64})$') {
        throw 'The readiness certificate thumbprint must be 40 or 64 hexadecimal characters.'
    }
    return $normalized
}

function Get-CertificateHashAlgorithm {
    param([Parameter(Mandatory)][int]$ExpectedHexLength)

    switch ($ExpectedHexLength) {
        40 { return [Security.Cryptography.HashAlgorithmName]::SHA1 }
        64 { return [Security.Cryptography.HashAlgorithmName]::SHA256 }
        default { throw "Unsupported certificate thumbprint length '$ExpectedHexLength'." }
    }
}

function Resolve-ReadinessCertificateThumbprint {
    param([string]$ExplicitThumbprint, [string]$ServiceName, [string]$Url)

    $uri = [Uri]$Url
    if ($uri.Scheme -ne 'https') { return $null }
    foreach ($candidateName in @('SyncFactors__Tls__MachineCertificateThumbprint', 'SYNCFACTORS_TLS_CERT_THUMBPRINT')) {
        $candidate = if (-not [string]::IsNullOrWhiteSpace($ExplicitThumbprint)) {
            $ExplicitThumbprint
        }
        else {
            Get-ServiceEnvironmentValue -ServiceName $ServiceName -Name $candidateName
        }
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            return Normalize-CertificateThumbprint $candidate
        }
    }

    $certificatePath = Get-ServiceEnvironmentValue -ServiceName $ServiceName -Name 'ASPNETCORE_Kestrel__Certificates__Default__Path'
    if (-not [string]::IsNullOrWhiteSpace($certificatePath)) {
        if (-not (Test-Path -Path $certificatePath -PathType Leaf)) {
            throw "Configured API PFX '$certificatePath' was not found for readiness certificate pinning."
        }
        $certificatePassword = Get-ServiceEnvironmentValue -ServiceName $ServiceName -Name 'ASPNETCORE_Kestrel__Certificates__Default__Password'
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath, $certificatePassword)
        try {
            return Normalize-CertificateThumbprint $certificate.Thumbprint
        }
        finally {
            $certificate.Dispose()
        }
    }

    throw 'HTTPS readiness requires an explicit or installed API certificate thumbprint; certificate validation cannot be skipped.'
}

function Invoke-PinnedHttpRequest {
    param([string]$Url, [hashtable]$Headers, [string]$CertificateThumbprint)

    $expectedThumbprint = Normalize-CertificateThumbprint $CertificateThumbprint
    $hashAlgorithm = Get-CertificateHashAlgorithm -ExpectedHexLength $expectedThumbprint.Length
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.ServerCertificateCustomValidationCallback = {
        param($request, $certificate, $chain, $errors)
        if ($null -eq $certificate) { return $false }
        $actual = ($certificate.GetCertHashString($hashAlgorithm) -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
        return [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
            [Convert]::FromHexString($expectedThumbprint),
            [Convert]::FromHexString($actual))
    }.GetNewClosure()
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    $requestMessage = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Url)
    try {
        foreach ($header in $Headers.GetEnumerator()) {
            [void]$requestMessage.Headers.TryAddWithoutValidation($header.Key, [string]$header.Value)
        }
        $response = $client.SendAsync($requestMessage).GetAwaiter().GetResult()
        try {
            return [pscustomobject]@{
                StatusCode = [int]$response.StatusCode
                Content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $requestMessage.Dispose()
        $client.Dispose()
        $handler.Dispose()
    }
}

function Invoke-HttpHealthCheck {
    param(
        [Parameter(Mandatory)]
        [string]$Url,
        [Parameter(Mandatory)]
        [DateTimeOffset]$Deadline,
        [Parameter(Mandatory)]
        [hashtable]$Headers,
        [string]$CertificateThumbprint
    )

    $lastFailure = $null
    do {
        try {
            $response = Invoke-PinnedHttpRequest `
                -Url $Url `
                -Headers $Headers `
                -CertificateThumbprint $CertificateThumbprint
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                try {
                    $body = $response.Content | ConvertFrom-Json
                    $statusProperty = $body.PSObject.Properties['status']
                    $attestedProperty = $body.PSObject.Properties['attested']
                    if ($null -ne $statusProperty -and
                        $null -ne $attestedProperty -and
                        [string]::Equals([string]$statusProperty.Value, 'ready', [System.StringComparison]::OrdinalIgnoreCase) -and
                        $attestedProperty.Value -eq $true) {
                        return $response.StatusCode
                    }

                    $lastFailure = 'Readiness response did not contain status=ready and attested=true.'
                }
                catch {
                    $lastFailure = 'Readiness response was not valid JSON attestation.'
                }
            }
            else {
                $lastFailure = "HTTP $($response.StatusCode)"
            }
        }
        catch {
            $lastFailure = $_.Exception.Message
        }

        Start-Sleep -Seconds 2
    }
    while ([DateTimeOffset]::UtcNow -lt $Deadline)

    throw "Health check '$Url' did not succeed within the deployment verification timeout. Last failure: $lastFailure"
}

function New-ReadinessAttestationHeaders {
    param(
        [Parameter(Mandatory)][string]$Nonce,
        [Parameter(Mandatory)][DateTimeOffset]$StartedAfter,
        [string]$WorkerVersion,
        [string]$WorkerCommit,
        [string]$ApiVersion,
        [string]$ApiCommit
    )

    $headers = @{
        'X-SyncFactors-Deployment-Nonce' = $Nonce
        'X-SyncFactors-Worker-Started-After' = $StartedAfter.ToString('O')
    }
    if (-not [string]::IsNullOrWhiteSpace($WorkerVersion)) {
        $headers['X-SyncFactors-Expected-Worker-Version'] = $WorkerVersion
    }
    if (-not [string]::IsNullOrWhiteSpace($WorkerCommit)) {
        $headers['X-SyncFactors-Expected-Worker-Commit'] = $WorkerCommit
    }
    if (-not [string]::IsNullOrWhiteSpace($ApiVersion)) {
        $headers['X-SyncFactors-Expected-Api-Version'] = $ApiVersion
    }
    if (-not [string]::IsNullOrWhiteSpace($ApiCommit)) {
        $headers['X-SyncFactors-Expected-Api-Commit'] = $ApiCommit
    }
    return $headers
}

if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'SyncFactors Windows deployment verification can only run on Windows.'
}

Assert-HttpsHealthUrl -Url $HealthUrl

if ($DryRunOnly.IsPresent -and $EnableLiveWrites.IsPresent) {
    throw 'DryRunOnly and EnableLiveWrites cannot be specified together.'
}

$expectedDryRunOnly = -not $EnableLiveWrites.IsPresent
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$services = @($ApiServiceName, $WorkerServiceName)
$serviceResults = @()
$auditIntegrityKeys = @()

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

    $auditIntegrityKey = Get-ServiceEnvironmentValue `
        -ServiceName $serviceName `
        -Name 'SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY'
    if ([string]::IsNullOrWhiteSpace($auditIntegrityKey)) {
        throw "Service '$serviceName' must have SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY configured."
    }
    $auditIntegrityKeys += $auditIntegrityKey

    $serviceResults += [pscustomobject]@{
        name = $serviceName
        status = $service.Status.ToString()
        dryRunOnly = $parsedValue
    }
}

if (@($auditIntegrityKeys | Select-Object -Unique).Count -ne 1) {
    throw 'The API and worker must use the same security audit integrity key.'
}

$healthStatusCode = $null
if (-not $SkipHttpHealthCheck.IsPresent) {
    $resolvedCertificateThumbprint = Resolve-ReadinessCertificateThumbprint `
        -ExplicitThumbprint $TlsCertificateThumbprint `
        -ServiceName $ApiServiceName `
        -Url $HealthUrl
    $attestationHeaders = New-ReadinessAttestationHeaders `
        -Nonce $DeploymentNonce `
        -StartedAfter $WorkerStartedAfter `
        -WorkerVersion $ExpectedWorkerVersion `
        -WorkerCommit $ExpectedWorkerCommit `
        -ApiVersion $ExpectedApiVersion `
        -ApiCommit $ExpectedApiCommit

    $healthStatusCode = Invoke-HttpHealthCheck `
        -Url $HealthUrl `
        -Deadline $deadline `
        -Headers $attestationHeaders `
        -CertificateThumbprint $resolvedCertificateThumbprint
}

[pscustomobject]@{
    ready = -not $SkipHttpHealthCheck.IsPresent
    dryRunOnly = $expectedDryRunOnly
    auditIntegrityConfigured = $true
    services = $serviceResults
    healthUrl = if ($SkipHttpHealthCheck.IsPresent) { $null } else { $HealthUrl }
    healthStatusCode = $healthStatusCode
}
