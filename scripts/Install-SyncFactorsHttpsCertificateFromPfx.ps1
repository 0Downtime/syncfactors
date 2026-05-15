[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PfxPath,
    [Parameter(Mandatory)]
    [string]$PfxPassword,
    [string]$ProjectRoot,
    [switch]$SkipStoreImport,
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$StoreLocation = 'CurrentUser',
    [string]$ServiceAccount
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Start-SyncFactorsCommon.ps1')

function ConvertTo-AccountSid {
    param(
        [Parameter(Mandatory)]
        [string]$Identity
    )

    $normalized = $Identity.Trim()
    $localName = $null
    if ($normalized.StartsWith('.\')) {
        $localName = $normalized.Substring(2)
    }
    elseif ($normalized.StartsWith("$env:COMPUTERNAME\", [System.StringComparison]::OrdinalIgnoreCase)) {
        $localName = $normalized.Substring($env:COMPUTERNAME.Length + 1)
    }
    elseif (-not $normalized.Contains('\') -and -not $normalized.Contains('@')) {
        $localName = $normalized
    }

    if (-not [string]::IsNullOrWhiteSpace($localName)) {
        $localUser = Get-LocalUser -Name $localName -ErrorAction SilentlyContinue
        if ($localUser) {
            return $localUser.SID
        }
    }

    try {
        $account = [System.Security.Principal.NTAccount]::new($normalized)
        return $account.Translate([System.Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "Could not resolve Windows account '$Identity' to a SID. For local accounts, create the account first or pass '.\name', '$env:COMPUTERNAME\name', or just 'name'."
    }
}

function Add-ExistingPath {
    param(
        [Parameter(Mandatory)]
        [System.Collections.Generic.List[string]]$Paths,
        [string]$Path
    )

    if (-not [string]::IsNullOrWhiteSpace($Path) -and (Test-Path -Path $Path -PathType Leaf)) {
        $Paths.Add((Resolve-Path -Path $Path).ProviderPath)
    }
}

function Resolve-CertificatePrivateKeyPaths {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $paths = [System.Collections.Generic.List[string]]::new()
    $machineKeysRoot = Join-Path $env:ProgramData 'Microsoft\Crypto'

    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    try {
        if ($rsa -is [System.Security.Cryptography.RSACng] -and -not [string]::IsNullOrWhiteSpace($rsa.Key.UniqueName)) {
            Add-ExistingPath -Paths $paths -Path (Join-Path $machineKeysRoot "Keys\$($rsa.Key.UniqueName)")
        }
        elseif ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            $containerInfo = $rsa.CspKeyContainerInfo
            if ($containerInfo.MachineKeyStore -and -not [string]::IsNullOrWhiteSpace($containerInfo.UniqueKeyContainerName)) {
                Add-ExistingPath -Paths $paths -Path (Join-Path $machineKeysRoot "RSA\MachineKeys\$($containerInfo.UniqueKeyContainerName)")
            }
        }
    }
    finally {
        if ($rsa) {
            $rsa.Dispose()
        }
    }

    $ecdsa = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($Certificate)
    try {
        if ($ecdsa -is [System.Security.Cryptography.ECDsaCng] -and -not [string]::IsNullOrWhiteSpace($ecdsa.Key.UniqueName)) {
            Add-ExistingPath -Paths $paths -Path (Join-Path $machineKeysRoot "Keys\$($ecdsa.Key.UniqueName)")
        }
    }
    finally {
        if ($ecdsa) {
            $ecdsa.Dispose()
        }
    }

    return @($paths | Select-Object -Unique)
}

function Grant-CertificatePrivateKeyReadAccess {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)]
        [string]$Identity
    )

    if (-not [OperatingSystem]::IsWindows()) {
        throw 'Certificate private-key ACL updates are only supported on Windows.'
    }

    $sid = ConvertTo-AccountSid -Identity $Identity
    $privateKeyPaths = @(Resolve-CertificatePrivateKeyPaths -Certificate $Certificate)
    if ($privateKeyPaths.Count -eq 0) {
        throw "Could not resolve a machine private-key file for certificate '$($Certificate.Thumbprint)'. Grant '$Identity' read access to the private key manually."
    }

    foreach ($privateKeyPath in $privateKeyPaths) {
        $acl = Get-Acl -Path $privateKeyPath
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [System.Security.AccessControl.FileSystemRights]'Read',
            [System.Security.AccessControl.AccessControlType]::Allow)
        $acl.SetAccessRule($rule)
        Set-Acl -Path $privateKeyPath -AclObject $acl
    }

    return $privateKeyPaths
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-ProjectRoot
}

if (-not [string]::IsNullOrWhiteSpace($ServiceAccount)) {
    if ($SkipStoreImport.IsPresent) {
        throw '-ServiceAccount requires certificate store import. Remove -SkipStoreImport.'
    }

    if ($StoreLocation -ne 'LocalMachine') {
        throw "-ServiceAccount requires -StoreLocation LocalMachine so the Windows service can load the certificate."
    }
}

$resolvedPfxPath = Resolve-RequiredPath -Path $PfxPath -Label 'PFX certificate'
$paths = Get-SyncFactorsTlsAssetPaths -ProjectRoot $ProjectRoot
New-Item -ItemType Directory -Force -Path $paths.CertificateDirectory | Out-Null

$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
$certificate.Import($resolvedPfxPath, $PfxPassword, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)

Copy-Item -Path $resolvedPfxPath -Destination $paths.CertificatePath -Force
Set-SyncFactorsSecretFile -Path $paths.PasswordPath -Value $PfxPassword

if (-not $SkipStoreImport) {
    if (-not [OperatingSystem]::IsWindows()) {
        throw 'Certificate store import is only supported on Windows. Use -SkipStoreImport to only configure the runtime asset files.'
    }

    $storeName = [System.Security.Cryptography.X509Certificates.StoreName]::My
    $storeLocationEnum = [System.Security.Cryptography.X509Certificates.StoreLocation]::$StoreLocation
    $x509Store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, $storeLocationEnum)
    $storeCertificate = $null

    try {
        $x509Store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)

        $existing = $x509Store.Certificates.Find(
            [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $certificate.Thumbprint,
            $false
        )

        if ($existing.Count -eq 0) {
            $x509Store.Add($certificate)
            $existing = $x509Store.Certificates.Find(
                [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                $certificate.Thumbprint,
                $false
            )
        }

        if ($existing.Count -gt 0) {
            $storeCertificate = $existing[0]
        }
    }
    finally {
        $x509Store.Close()
    }

    if (-not [string]::IsNullOrWhiteSpace($ServiceAccount)) {
        if ($null -eq $storeCertificate) {
            throw "Certificate '$($certificate.Thumbprint)' was not found in $StoreLocation\\My after import."
        }

        $privateKeyPaths = Grant-CertificatePrivateKeyReadAccess -Certificate $storeCertificate -Identity $ServiceAccount
    }
}

Write-Host 'SyncFactors HTTPS certificate configured from PFX.' -ForegroundColor Cyan
Write-Host "Source PFX: $resolvedPfxPath"
Write-Host "Runtime certificate: $($paths.CertificatePath)"
Write-Host "Password file: $($paths.PasswordPath)"

if ($SkipStoreImport) {
    Write-Host 'Certificate store import skipped.'
}
elseif ([OperatingSystem]::IsWindows()) {
    Write-Host "Store import: $StoreLocation\\My"
}
if (-not [string]::IsNullOrWhiteSpace($ServiceAccount)) {
    Write-Host "Private-key read access granted to: $ServiceAccount"
    foreach ($privateKeyPath in $privateKeyPaths) {
        Write-Host "Private-key file: $privateKeyPath"
    }
}

Write-Host 'Default API URL: https://127.0.0.1:5087'
Write-Host 'Use the hostname or IP from your certificate SAN when enabling remote access.' -ForegroundColor Cyan
